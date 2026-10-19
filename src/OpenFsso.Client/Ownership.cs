using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using OpenFsso.Logging;
using OpenFsso.Windows;

namespace OpenFsso.Client;

public sealed record OwnerRecord(uint SessionId, string Sid, string Name, string LogonId, long LogonTime, Guid Generation);
public sealed record OwnershipSnapshot(long Revision, OwnerRecord? Owner);
public sealed class Ownership(ClientOptions options, ILogger<Ownership> logger) : IDisposable
{
    private readonly object gate = new();
    private OwnershipSnapshot current = new(0, null);
    private CancellationTokenSource? selection;
    private uint? pendingSession;
    private bool disposed;
    public event Action? Changed;
    public OwnershipSnapshot Current => Volatile.Read(ref current);
    private string RecordPath => Path.Combine(options.StateDirectory, "owner.json");

    // At service start, including restarts and right after installation. The
    // saved owner stays the owner while its session is attached, locked or not,
    // since lock keeps the owner. Otherwise a single attached session becomes the
    // owner. Several attached sessions without a matching record wait for the
    // next logon or unlock, because their recency is unknown.
    public void Restore()
    {
        try
        {
            var attached = Sessions.Eligible(options.AllowRdp).Where(x => x.Attached).ToArray();
            var saved = File.Exists(RecordPath) ? JsonSerializer.Deserialize(File.ReadAllText(RecordPath), ClientJson.Default.OwnerRecord) : null;
            OwnerRecord owner;
            if (saved != null && attached.Any(x => Matches(saved, x)))
            {
                owner = saved; // Same generation, so the receivers renew the same lease.
            }
            else if (attached.Length == 1)
            {
                var session = attached[0];
                owner = new OwnerRecord(session.SessionId, session.Sid, session.Name, session.LogonId, session.LogonTime, Guid.NewGuid());
                Save(owner);
            }
            else
            {
                logger.Write(LogLevel.Information, "Waiting for an interactive logon or unlock", ("AttachedSessions", attached.Length));
                return;
            }

            Volatile.Write(ref current, new(1, owner));
            logger.Write(LogLevel.Information, "Selected interactive owner at service start", ("UserName", owner.Name), ("UserSid", owner.Sid), ("SessionId", owner.SessionId), ("Generation", owner.Generation));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or Win32Exception or InvalidOperationException)
        {
            logger.Write(LogLevel.Error, "Owner selection at service start failed closed", ("ErrorType", e.GetType().Name));
        }
    }

    public void Select(uint sessionId)
    {
        try
        {
            ushort protocol = Sessions.Protocol(sessionId);
            if (!Sessions.Eligible(protocol, options.AllowRdp))
            {
                return;
            }
        }
        catch (Win32Exception e)
        {
            logger.Write(LogLevel.Warning, "Cannot validate session protocol", ("SessionId", sessionId), ("ErrorCode", e.NativeErrorCode));
            return;
        }

        CancellationToken token;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            selection?.Cancel();
            selection?.Dispose();
            selection = new();
            token = selection.Token;
            pendingSession = sessionId;
            Volatile.Write(ref current, new(current.Revision + 1, current.Owner?.SessionId == sessionId ? current.Owner : null));
        }

        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 12 && !token.IsCancellationRequested; attempt++)
            {
                try
                {
                    using var handle = SessionToken.Open(sessionId);
                    var identity = Sessions.Read(sessionId, handle);
                    if (!Sessions.Eligible(identity.Protocol, options.AllowRdp))
                    {
                        return;
                    }

                    bool published = false;
                    lock (gate)
                    {
                        if (disposed || token.IsCancellationRequested)
                        {
                            return;
                        }

                        var previous = current.Owner;
                        var next = previous != null && Matches(previous, identity) ? previous : new OwnerRecord(identity.SessionId, identity.Sid, identity.Name, identity.LogonId, identity.LogonTime, Guid.NewGuid());
                        try
                        {
                            Save(next);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            Volatile.Write(ref current, new(current.Revision + 1, null));
                            logger.Write(LogLevel.Error, "Owner state could not be saved; renewals stopped", ("ErrorType", e.GetType().Name));
                            published = true;
                            next = null;
                        }

                        if (next != null)
                        {
                            Volatile.Write(ref current, new(current.Revision + 1, next));
                            if (previous?.Generation != next.Generation)
                            {
                                logger.Write(LogLevel.Information, "Selected interactive owner", ("UserName", next.Name), ("UserSid", next.Sid), ("SessionId", next.SessionId), ("Generation", next.Generation));
                            }

                            published = true;
                        }
                    }

                    if (published)
                    {
                        Changed?.Invoke();
                    }

                    return;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                {
                    if (attempt == 11)
                    {
                        logger.Write(LogLevel.Warning, "Interactive session token is unavailable", ("SessionId", sessionId), ("ErrorType", e.GetType().Name));
                    }
                }

                try
                {
                    await Task.Delay(500, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    public void Logoff(uint sessionId)
    {
        bool changed = false;
        lock (gate)
        {
            if (pendingSession == sessionId)
            {
                selection?.Cancel();
                pendingSession = null;
            }

            if (current.Owner?.SessionId == sessionId)
            {
                selection?.Cancel();
                logger.Write(LogLevel.Information, "Owner logged off", ("UserName", current.Owner.Name), ("UserSid", current.Owner.Sid));
                Volatile.Write(ref current, new(current.Revision + 1, null));
                try
                {
                    File.Delete(RecordPath);
                }
                catch (IOException)
                {
                }

                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public SafeAccessTokenHandle Open(OwnerRecord owner)
    {
        var token = SessionToken.Open(owner.SessionId);
        try
        {
            var identity = Sessions.Read(owner.SessionId, token);
            if (!Matches(owner, identity) || !Sessions.Eligible(identity.Protocol, options.AllowRdp))
            {
                throw new InvalidOperationException("The selected Windows logon no longer exists.");
            }

            return token;
        }
        catch
        {
            token.Dispose();
            throw;
        }
    }

    private static bool Matches(OwnerRecord owner, SessionIdentity identity) => owner.SessionId == identity.SessionId && owner.Sid == identity.Sid && owner.LogonId == identity.LogonId && owner.LogonTime == identity.LogonTime;
    private void Save(OwnerRecord owner)
    {
        string temporary = RecordPath + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, owner, ClientJson.Default.OwnerRecord);
            stream.Flush(true);
        }

        File.Move(temporary, RecordPath, overwrite: true);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            selection?.Cancel();
            selection?.Dispose();
        }
    }
}
