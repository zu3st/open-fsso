using System.Net;
using Microsoft.Extensions.Time.Testing;
using OpenFsso.Core;

namespace OpenFsso.Tests;

[TestClass]
public sealed class LeaseTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("192.0.2.10");
    private static readonly AuthenticatedUser Alice = new("sid-alice", "DOMAIN\\Alice");
    private static readonly AuthenticatedUser Bob = new("sid-bob", "DOMAIN\\Bob");
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PresenceRequest Request(Guid generation, Guid? reference = null) => new(1, generation, Ip.ToString(), reference);

    // FakeTimeProvider only moves forward; this shifts the wall clock alone.
    private sealed class WallClockShift : FakeTimeProvider
    {
        public TimeSpan Offset
        {
            get; set;
        }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => base.GetUtcNow().UtcTicks;
    }

    [TestMethod]
    public void StartupBarrierRejectsMissingAndPrePolicyConnectionTimestamps()
    {
        var barrier = new StartupBarrier();
        Assert.IsFalse(barrier.Accepts(1));
        barrier.Activate(100);
        Assert.IsFalse(barrier.Accepts(0));
        Assert.IsFalse(barrier.Accepts(99));
        Assert.IsFalse(barrier.Accepts(100));
        Assert.IsTrue(barrier.Accepts(101));
    }

    [TestMethod]
    public void SyslogFormattingPreservesLiteralPlaceholderTextInNames()
    {
        var user = new AuthenticatedUser("sid", @"DOMAIN\{ip}{user}");
        Assert.AreEqual("ip=192.0.2.10 user=DOMAIN\\{ip}{user}", SyslogFormat.Render("ip={ip} user={user}", new("login", "192.0.2.10", user, Guid.NewGuid(), "test")));
    }

    [TestMethod]
    public void DeliveryFailuresRemainPendingUntilCompleteReassertion()
    {
        var delivery = new DeliveryRecovery();
        Assert.IsTrue(delivery.Healthy);
        delivery.Failed();
        Assert.IsFalse(delivery.Healthy);
        long attempt = delivery.Version;
        delivery.Failed();
        delivery.CompleteReassert(attempt);
        Assert.IsFalse(delivery.Healthy);
        delivery.CompleteReassert(delivery.Version);
        Assert.IsTrue(delivery.Healthy);
    }

    [TestMethod]
    public void DuplicateRenewalsDoNotEmitLogins()
    {
        var s = new LeaseStore(new FakeTimeProvider());
        var first = s.Presence(Alice, Ip, Request(A));
        Assert.HasCount(1, first.Changes);
        var duplicate = s.Presence(Alice, Ip, Request(A, first.Reply.LeaseReference));
        Assert.AreEqual(200, duplicate.Status);
        Assert.IsEmpty(duplicate.Changes);
    }

    [TestMethod]
    public void SameUserWithANewGenerationRenewsWithoutLogout()
    {
        var s = new LeaseStore(new FakeTimeProvider());
        var first = s.Presence(Alice, Ip, Request(A));
        var unreferenced = s.Presence(Alice, Ip, Request(B));
        Assert.AreEqual(409, unreferenced.Status);
        var renewed = s.Presence(Alice, Ip, Request(B, unreferenced.Reply.LeaseReference));
        Assert.AreEqual(200, renewed.Status);
        Assert.IsEmpty(renewed.Changes);
        Assert.IsEmpty(s.Release(Ip, Request(A, first.Reply.LeaseReference)).Changes);
        Assert.AreEqual("logout", s.Release(Ip, Request(B, first.Reply.LeaseReference)).Changes.Single().Action);
    }

    [TestMethod]
    public void ReplacementIsLogoutThenLoginAndStaleOperationsCannotEraseIt()
    {
        var s = new LeaseStore(new FakeTimeProvider());
        var alice = s.Presence(Alice, Ip, Request(A));
        var unreferenced = s.Presence(Bob, Ip, Request(B));
        Assert.AreEqual(409, unreferenced.Status);
        Assert.IsEmpty(unreferenced.Changes);
        var bob = s.Presence(Bob, Ip, Request(B, alice.Reply.LeaseReference));
        Assert.AreEqual("logout", bob.Changes[0].Action);
        Assert.AreEqual(Alice, bob.Changes[0].User);
        Assert.AreEqual("login", bob.Changes[1].Action);
        Assert.AreEqual(Bob, bob.Changes[1].User);
        Assert.IsEmpty(s.Release(Ip, Request(A, alice.Reply.LeaseReference)).Changes);
        Assert.AreEqual(409, s.Presence(Alice, Ip, Request(A, alice.Reply.LeaseReference)).Status);
        Assert.IsEmpty(s.Presence(Bob, Ip, Request(B, bob.Reply.LeaseReference)).Changes);
    }

    [TestMethod]
    public void ReleaseByLeaseReferenceEmitsLogoutWithoutUserCredentials()
    {
        var s = new LeaseStore(new FakeTimeProvider());
        var alice = s.Presence(Alice, Ip, Request(A));
        var released = s.Release(Ip, Request(A, alice.Reply.LeaseReference));
        Assert.AreEqual(200, released.Status);
        Assert.AreEqual("logout", released.Changes.Single().Action);
        Assert.AreEqual(Alice, released.Changes.Single().User);
        Assert.AreEqual("release", released.Changes.Single().Reason);
        Assert.AreEqual(0, s.Count);
    }

    [TestMethod]
    public void ReleaseNeedsTheMatchingReferenceGenerationAndSocketIp()
    {
        var s = new LeaseStore(new FakeTimeProvider());
        var alice = s.Presence(Alice, Ip, Request(A));
        var other = IPAddress.Parse("192.0.2.11");
        Assert.IsEmpty(s.Release(Ip, Request(A, Guid.NewGuid())).Changes);
        Assert.IsEmpty(s.Release(Ip, Request(B, alice.Reply.LeaseReference)).Changes);
        Assert.IsEmpty(s.Release(other, new(1, A, other.ToString(), alice.Reply.LeaseReference)).Changes);
        Assert.AreEqual(400, s.Release(Ip, Request(A)).Status);
        Assert.AreEqual(400, s.Release(Ip, new(1, A, "192.0.2.99", alice.Reply.LeaseReference)).Status);
        Assert.AreEqual(1, s.Count);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("garbage")]
    [DataRow("192.0.2.99")]
    public void InvalidSocketClaimsChangeNoStateIncludingExpiry(string? claim)
    {
        var clock = new FakeTimeProvider();
        var s = new LeaseStore(clock);
        var first = s.Presence(Alice, Ip, Request(A));
        clock.Advance(TimeSpan.FromSeconds(181));
        Assert.AreEqual(400, s.Presence(Bob, Ip, new(1, B, claim, first.Reply.LeaseReference)).Status);
        Assert.AreEqual(1, s.Count);
        Assert.HasCount(1, s.Expire());
        Assert.AreEqual(0, s.Count);
    }

    [TestMethod]
    public void RequestsExpireOnlyTheirOwnLeaseAndLeaveTheRestToTheWorker()
    {
        var clock = new FakeTimeProvider();
        var s = new LeaseStore(clock);
        var other = IPAddress.Parse("192.0.2.11");
        var alice = s.Presence(Alice, Ip, Request(A));
        s.Presence(Bob, other, new(1, B, other.ToString()));
        clock.Advance(TimeSpan.FromSeconds(181));
        var renewed = s.Presence(Alice, Ip, Request(A, alice.Reply.LeaseReference));
        Assert.AreEqual("logout", renewed.Changes[0].Action);
        Assert.AreEqual(Ip.ToString(), renewed.Changes[0].Ip);
        Assert.AreEqual("login", renewed.Changes[1].Action);
        Assert.HasCount(2, renewed.Changes);
        Assert.AreEqual(2, s.Count);
        Assert.AreEqual(other.ToString(), s.Expire().Single().Ip);
    }

    [TestMethod]
    public void MonotonicExpirySurvivesWallClockRollbackAndRemovesOnce()
    {
        var clock = new WallClockShift();
        var s = new LeaseStore(clock);
        s.Presence(Alice, Ip, Request(A));
        clock.Advance(TimeSpan.FromSeconds(179));
        Assert.IsEmpty(s.Expire());
        clock.Offset = TimeSpan.FromDays(-365);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.HasCount(1, s.Expire());
        Assert.IsEmpty(s.Expire());
    }

    [TestMethod]
    public void IpPathMigrationCleansOnlyTheAuthenticatedOwnersOldMapping()
    {
        var s = new LeaseStore(new FakeTimeProvider());
        var first = s.Presence(Alice, Ip, Request(A));
        var moved = s.Presence(Alice, IPAddress.Parse("192.0.2.11"), new(1, A, "192.0.2.11", first.Reply.LeaseReference));
        Assert.HasCount(2, moved.Changes);
        Assert.AreEqual("logout", moved.Changes[0].Action);
        Assert.AreEqual(1, s.Count);
        var attack = s.Release(Ip, Request(B, moved.Reply.LeaseReference));
        Assert.IsEmpty(attack.Changes);
        Assert.AreEqual(1, s.Count);
    }

    [TestMethod]
    public void MappedIpv4PeersCompareCanonicallyAndUnsupportedVersionsFailClosed()
    {
        var s = new LeaseStore(new FakeTimeProvider());
        Assert.AreEqual(200, s.Presence(Alice, Ip.MapToIPv6(), Request(A)).Status);
        Assert.AreEqual(400, s.Presence(Alice, Ip, new(2, A, Ip.ToString())).Status);
        Assert.AreEqual(400, s.Presence(Alice, Ip, new(1, Guid.Empty, Ip.ToString())).Status);
    }

    [TestMethod]
    public void ReceiverRestartStartsEmptyWithoutReplay()
    {
        var clock = new FakeTimeProvider();
        var first = new LeaseStore(clock);
        first.Presence(Alice, Ip, Request(A));
        var restarted = new LeaseStore(clock);
        Assert.AreEqual(0, restarted.Count);
        Assert.IsEmpty(restarted.Reassert("login"));
        Assert.HasCount(1, restarted.Presence(Alice, Ip, Request(A)).Changes);
    }

    [TestMethod]
    public async Task ConcurrentDuplicateRequestsAndExpiryShareOneEmissionPipeline()
    {
        var clock = new FakeTimeProvider();
        var events = new List<MappingChange>();
        int inside = 0;
        var pipeline = new SerializedLeases(new LeaseStore(clock), async change =>
        {
            Assert.AreEqual(1, Interlocked.Increment(ref inside));
            await Task.Delay(1);
            events.Add(change);
            Interlocked.Decrement(ref inside);
        });
        var first = await pipeline.PresenceAsync(Alice, Ip, Request(A), default);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => pipeline.PresenceAsync(Alice, Ip, Request(A, first.Reply.LeaseReference), default)));
        Assert.HasCount(1, events);
        clock.Advance(TimeSpan.FromSeconds(181));
        await Task.WhenAll(pipeline.TickAsync(null, default), pipeline.PresenceAsync(Bob, Ip, Request(B, first.Reply.LeaseReference), default));
        Assert.HasCount(3, events);
        Assert.AreEqual("logout", events[1].Action);
        Assert.AreEqual(Alice, events[1].User);
        Assert.AreEqual(Bob, events[2].User);
        Assert.AreEqual(1, pipeline.Count);
    }

    [TestMethod]
    public async Task PeriodicReassertionSendsAnUpdatePerLeaseWithoutChangingState()
    {
        var events = new List<MappingChange>();
        var pipeline = new SerializedLeases(new LeaseStore(new FakeTimeProvider()), change =>
        {
            events.Add(change);
            return Task.CompletedTask;
        });
        await pipeline.PresenceAsync(Alice, Ip, Request(A), default);
        await pipeline.TickAsync("update", default);
        Assert.AreEqual("update", events[1].Action);
        Assert.AreEqual(Alice, events[1].User);
        Assert.AreEqual(Ip.ToString(), events[1].Ip);
        Assert.HasCount(2, events);
        Assert.AreEqual(1, pipeline.Count);
    }

    [TestMethod]
    public async Task CancelledDelayedRequestDoesNotMutateNewerMapping()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var pipeline = new SerializedLeases(new LeaseStore(new FakeTimeProvider()), async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var creating = pipeline.PresenceAsync(Alice, Ip, Request(A), default);
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var stale = pipeline.PresenceAsync(Bob, Ip, Request(B), cancellation.Token);
        cancellation.Cancel();
        release.SetResult();
        await creating;
        await Assert.ThrowsAsync<OperationCanceledException>(() => stale);
        Assert.AreEqual(1, pipeline.Count);
    }

    [TestMethod]
    public async Task ExpiryRemovesLocalStateEvenWhenOutputIsUnavailable()
    {
        var clock = new FakeTimeProvider();
        bool fail = false;
        var pipeline = new SerializedLeases(new LeaseStore(clock), _ => fail ? Task.FromException(new IOException("sink unavailable")) : Task.CompletedTask);
        await pipeline.PresenceAsync(Alice, Ip, Request(A), default);
        clock.Advance(TimeSpan.FromSeconds(181));
        fail = true;
        await Assert.ThrowsAsync<IOException>(() => pipeline.TickAsync(null, default));
        Assert.AreEqual(0, pipeline.Count);
    }
}
