using System.Net;

namespace OpenFsso.Core;

public sealed record PresenceRequest(int Version, Guid Generation, string? ConnectionLocalIp, Guid? LeaseReference = null);
public sealed record PresenceReply(Guid? LeaseReference, int LeaseSeconds, string? Error = null);
public sealed record AuthenticatedUser(string Sid, string Name);
public sealed record MappingChange(string Action, string Ip, AuthenticatedUser User, Guid Generation, string Reason);
public sealed record Decision(int Status, PresenceReply Reply, IReadOnlyList<MappingChange> Changes);
public sealed class LeaseStore(TimeProvider clock, int leaseSeconds = 180)
{
    private sealed record Lease(string Ip, AuthenticatedUser User, Guid Generation, Guid Reference, long Renewed);
    private readonly Dictionary<string, Lease> leases = new(StringComparer.Ordinal);
    public int Count => leases.Count;

    public Decision Presence(AuthenticatedUser user, IPAddress peerAddress, PresenceRequest request)
    {
        // Validation precedes even opportunistic expiry: an invalid claim changes nothing.
        if (!Valid(peerAddress, request, out string ip) || string.IsNullOrWhiteSpace(user.Sid))
        {
            return Invalid();
        }

        var changes = Expire().ToList();
        leases.TryGetValue(ip, out var current);
        if (current != null && request.LeaseReference != current.Reference)
        {
            return new(409, new(current.Reference, leaseSeconds, "reconcile-current-lease"), changes);
        }

        // A previous path may be reconciled only by the same authenticated logon owner.
        var previous = leases.Values.FirstOrDefault(x => x.Reference == request.LeaseReference && x.Ip != ip && x.User.Sid == user.Sid && x.Generation == request.Generation);
        if (previous != null)
        {
            Remove(previous, "path-changed", changes);
        }

        if (current != null && current.User.Sid == user.Sid && current.Generation == request.Generation)
        {
            leases[ip] = current with
            {
                Renewed = clock.GetTimestamp()
            };
            return new(200, new(current.Reference, leaseSeconds), changes);
        }

        if (current != null)
        {
            Remove(current, "replaced", changes);
        }

        var next = new Lease(ip, user, request.Generation, Guid.NewGuid(), clock.GetTimestamp());
        leases[ip] = next;
        changes.Add(new("login", ip, user, next.Generation, "presence"));
        return new(200, new(next.Reference, leaseSeconds), changes);
    }

    // The lease reference, known only to the client that received it, authorizes
    // removal after logoff, when the user's Kerberos credentials are gone.
    public Decision Release(IPAddress peerAddress, PresenceRequest request)
    {
        if (!Valid(peerAddress, request, out string ip) || request.LeaseReference == null)
        {
            return Invalid();
        }

        var changes = Expire().ToList();
        if (leases.TryGetValue(ip, out var current) && current.Reference == request.LeaseReference && current.Generation == request.Generation)
        {
            Remove(current, "release", changes);
        }

        return new(200, new(null, leaseSeconds), changes);
    }

    private static bool Valid(IPAddress peerAddress, PresenceRequest request, out string ip)
    {
        var peer = Normalize(peerAddress);
        ip = peer.ToString();
        return request.Version == 1 && request.Generation != Guid.Empty && request.LeaseReference != Guid.Empty && IPAddress.TryParse(request.ConnectionLocalIp, out var claimed) && Normalize(claimed).Equals(peer);
    }

    private Decision Invalid() => new(400, new(null, leaseSeconds, "invalid-request-or-socket-ip"), []);

    public IReadOnlyList<MappingChange> Expire()
    {
        var changes = new List<MappingChange>();
        foreach (var lease in leases.Values.Where(x => clock.GetElapsedTime(x.Renewed) >= TimeSpan.FromSeconds(leaseSeconds)).ToArray())
        {
            Remove(lease, "expired", changes);
        }

        return changes;
    }

    public IReadOnlyList<MappingChange> Reassert() => leases.Values.Select(x => new MappingChange("login", x.Ip, x.User, x.Generation, "collector-reassert")).ToArray();
    private void Remove(Lease lease, string reason, List<MappingChange> changes)
    {
        leases.Remove(lease.Ip);
        changes.Add(new("logout", lease.Ip, lease.User, lease.Generation, reason));
    }

    public static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

public sealed class SerializedLeases(LeaseStore store, Func<MappingChange, Task> emit)
{
    private readonly SemaphoreSlim pipeline = new(1);
    private int count;
    public int Count => Volatile.Read(ref count);

    public Task<Decision> PresenceAsync(AuthenticatedUser user, IPAddress peer, PresenceRequest request, CancellationToken ct) => RunAsync(() => store.Presence(user, peer, request), ct);
    public Task<Decision> ReleaseAsync(IPAddress peer, PresenceRequest request, CancellationToken ct) => RunAsync(() => store.Release(peer, request), ct);

    private async Task<Decision> RunAsync(Func<Decision> apply, CancellationToken ct)
    {
        await pipeline.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var decision = apply();
            Volatile.Write(ref count, store.Count);
            foreach (var change in decision.Changes)
            {
                await emit(change);
            }

            return decision;
        }
        finally
        {
            pipeline.Release();
        }
    }

    public async Task TickAsync(bool reassert, CancellationToken ct)
    {
        await pipeline.WaitAsync(ct);
        try
        {
            var expired = store.Expire();
            Volatile.Write(ref count, store.Count);
            foreach (var change in expired)
            {
                await emit(change);
            }

            if (reassert)
            {
                foreach (var change in store.Reassert())
                {
                    await emit(change);
                }
            }
        }
        finally
        {
            pipeline.Release();
        }
    }
}

// One reply on a presence session: the HTTP-style status and the lease reply.
public sealed record SessionReply(int Status, PresenceReply Reply);
