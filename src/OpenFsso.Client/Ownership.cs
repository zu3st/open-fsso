using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
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

    public void Restore()
    {
        try
        {
            if (!File.Exists(RecordPath))
            {
                logger.LogInformation("Waiting for an interactive logon or unlock");
                return;
            }

            var saved = JsonSerializer.Deserialize<OwnerRecord>(File.ReadAllText(RecordPath));
            var sessions = Sessions.Eligible(options.AllowRdp);
            // A locked/disconnected session, multiple sessions, or enabled RDP makes
            // recency ambiguous after downtime. Wait for a new event in those cases.
            if (saved != null && !options.AllowRdp && sessions.Count == 1 && sessions[0].Unlocked && Matches(saved, sessions[0]))
            {
                Volatile.Write(ref current, new(1, saved));
                logger.LogInformation("Validated owner record for {UserName} ({UserSid})", saved.Name, saved.Sid);
            }
            else
            {
                logger.LogWarning("Saved ownership is ambiguous; waiting for logon or unlock");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or Win32Exception or InvalidOperationException)
        {
            logger.LogError("Owner recovery failed closed; error {ErrorType}", e.GetType().Name);
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
            logger.LogWarning("Cannot validate session protocol; session {SessionId}, Windows error {NativeCode}", sessionId, e.NativeErrorCode);
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
                            logger.LogError("Owner state could not be saved; renewals stopped; error {ErrorType}", e.GetType().Name);
                            published = true;
                            next = null;
                        }

                        if (next != null)
                        {
                            Volatile.Write(ref current, new(current.Revision + 1, next));
                            if (previous?.Generation != next.Generation)
                            {
                                logger.LogInformation("Selected interactive owner {UserName} ({UserSid}), session {SessionId}, generation {Generation}", next.Name, next.Sid, next.SessionId, next.Generation);
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
                        logger.LogWarning("Interactive session token is unavailable; session {SessionId}, error {ErrorType}", sessionId, e.GetType().Name);
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
                logger.LogInformation("Owner logged off; {UserName} ({UserSid})", current.Owner.Name, current.Owner.Sid);
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
            JsonSerializer.Serialize(stream, owner);
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
