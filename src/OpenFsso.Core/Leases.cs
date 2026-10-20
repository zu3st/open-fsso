using System.Net;

namespace OpenFsso.Core;

public sealed record PresenceRequest(int Version, Guid Generation, string? ConnectionLocalIp, Guid? LeaseReference = null);
public sealed record PresenceReply(Guid? LeaseReference, int LeaseSeconds, string? Error = null);
public sealed record AuthenticatedUser(string Sid, string Name);
public sealed record MappingChange(string Action, string Ip, AuthenticatedUser User, Guid Generation, string Reason);
public sealed record Decision(int Status, PresenceReply Reply, IReadOnlyList<MappingChange> Changes);

// Valid: the domain accepts the account. Revoked: disabled, locked out, expired
// or gone. Unknown: the check itself failed, so nothing changes.
public enum AccountState
{
    Valid,
    Revoked,
    Unknown
}

// Detail names the domain's answer, such as the NTSTATUS codes, for the log.
public sealed record AccountCheck(AccountState State, string Detail);

public sealed class LeaseStore(TimeProvider clock, int leaseSeconds = 180)
{
    private sealed record Lease(string Ip, AuthenticatedUser User, Guid Generation, Guid Reference, long Renewed);
    private readonly Dictionary<string, Lease> leases = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Lease> references = [];
    private readonly Dictionary<string, (long Since, TimeSpan For)> revoked = new(StringComparer.Ordinal);
    public int Count => leases.Count;

    public Decision Presence(AuthenticatedUser user, IPAddress peerAddress, PresenceRequest request)
    {
        // Validation precedes even opportunistic expiry: an invalid claim changes nothing.
        if (!Valid(peerAddress, request, out string ip) || string.IsNullOrWhiteSpace(user.Sid))
        {
            return Invalid();
        }

        if (IsRevoked(user.Sid))
        {
            return new(403, new(null, leaseSeconds, "account-revoked"), []);
        }

        // Each request touches only its own leases. The worker expires the rest.
        var changes = new List<MappingChange>();
        var current = Live(ip, changes);
        if (current != null && request.LeaseReference != current.Reference)
        {
            return new(409, new(current.Reference, leaseSeconds, "reconcile-current-lease"), changes);
        }

        // A previous path may be reconciled only by the same authenticated logon owner.
        if (request.LeaseReference is { } reference && references.TryGetValue(reference, out var previous) && previous.Ip != ip && previous.User.Sid == user.Sid && previous.Generation == request.Generation)
        {
            Remove(previous, "path-changed", changes);
        }

        // The same user keeps the mapping across a new logon or client restart.
        // The new generation means a release from the old one no longer applies.
        if (current != null && current.User == user)
        {
            Store(current with
            {
                Generation = request.Generation,
                Renewed = clock.GetTimestamp()
            });
            return new(200, new(current.Reference, leaseSeconds), changes);
        }

        if (current != null)
        {
            Remove(current, "replaced", changes);
        }

        var next = new Lease(ip, user, request.Generation, Guid.NewGuid(), clock.GetTimestamp());
        Store(next);
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

        var changes = new List<MappingChange>();
        if (Live(ip, changes) is { } current && current.Reference == request.LeaseReference && current.Generation == request.Generation)
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

    // The lease at ip, or null after removing it when it has expired.
    private Lease? Live(string ip, List<MappingChange> changes)
    {
        if (leases.TryGetValue(ip, out var lease) && Expired(lease))
        {
            Remove(lease, "expired", changes);
            return null;
        }

        return lease;
    }

    private bool Expired(Lease lease) => clock.GetElapsedTime(lease.Renewed) >= TimeSpan.FromSeconds(leaseSeconds);

    private void Store(Lease lease)
    {
        leases[lease.Ip] = lease;
        references[lease.Reference] = lease;
    }

    private Decision Invalid() => new(400, new(null, leaseSeconds, "invalid-request-or-socket-ip"), []);

    public IReadOnlyList<MappingChange> Expire()
    {
        var changes = new List<MappingChange>();
        foreach (var lease in leases.Values.Where(Expired).ToArray())
        {
            Remove(lease, "expired", changes);
        }

        return changes;
    }

    // "login" restores the collector's list after its restart or a lost send.
    // "update" keeps its entries from reaching the dead entry timeout.
    public IReadOnlyList<MappingChange> Reassert(string action) => leases.Values.Select(x => new MappingChange(action, x.Ip, x.User, x.Generation, "collector-reassert")).ToArray();

    // True while a revocation blocks the account from new mappings.
    public bool IsRevoked(string sid)
    {
        if (revoked.TryGetValue(sid, out var block) && clock.GetElapsedTime(block.Since) < block.For)
        {
            return true;
        }

        revoked.Remove(sid);
        return false;
    }

    // Removes the account's mappings and refuses new ones for the given time.
    public IReadOnlyList<MappingChange> Revoke(string sid, TimeSpan block)
    {
        var changes = new List<MappingChange>();
        foreach (var lease in leases.Values.Where(x => x.User.Sid == sid).ToArray())
        {
            Remove(lease, "account-revoked", changes);
        }

        revoked[sid] = (clock.GetTimestamp(), block);
        return changes;
    }
    private void Remove(Lease lease, string reason, List<MappingChange> changes)
    {
        leases.Remove(lease.Ip);
        references.Remove(lease.Reference);
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

    public async Task<bool> IsRevokedAsync(string sid, CancellationToken ct)
    {
        await pipeline.WaitAsync(ct);
        try
        {
            return store.IsRevoked(sid);
        }
        finally
        {
            pipeline.Release();
        }
    }

    public async Task RevokeAsync(string sid, TimeSpan block, CancellationToken ct)
    {
        await pipeline.WaitAsync(ct);
        try
        {
            var changes = store.Revoke(sid, block);
            Volatile.Write(ref count, store.Count);
            foreach (var change in changes)
            {
                await emit(change);
            }
        }
        finally
        {
            pipeline.Release();
        }
    }

    public async Task TickAsync(string? reassert, CancellationToken ct)
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

            if (reassert != null)
            {
                foreach (var change in store.Reassert(reassert))
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
