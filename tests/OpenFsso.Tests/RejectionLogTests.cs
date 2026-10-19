using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using OpenFsso.Receiver;

namespace OpenFsso.Tests;

[TestClass]
public sealed class RejectionLogTests
{
    // Records each message with the fields of the scope it was written in.
    private sealed class Lines : ILogger
    {
        private IReadOnlyList<KeyValuePair<string, object?>> scope = [];
        public List<(string Message, Dictionary<string, object?> Fields)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            scope = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception), scope.ToDictionary(x => x.Key, x => x.Value)));
    }

    private static readonly IPAddress Source = IPAddress.Parse("192.0.2.10");

    [TestMethod]
    public void FloodsAreLimitedPerMinuteAndSummarized()
    {
        var lines = new Lines();
        var clock = new FakeTimeProvider();
        var log = new RejectionLog(lines, clock);
        for (int i = 0; i < 25; i++)
        {
            log.SocketClaim("192.0.2.99", Source);
        }

        Assert.HasCount(RejectionLog.Limit, lines.Entries);
        clock.Advance(TimeSpan.FromSeconds(61));
        log.Identity("NTLM");
        Assert.AreEqual(15, lines.Entries[RejectionLog.Limit].Fields["Suppressed"]);
        Assert.AreEqual("NTLM", lines.Entries[^1].Fields["AuthenticationType"]);
    }

    [TestMethod]
    public void OnlyParsedAddressesReachTheLog()
    {
        var lines = new Lines();
        var log = new RejectionLog(lines, new FakeTimeProvider());
        log.SocketClaim(new string('x', 4000), Source);
        log.SocketClaim("::ffff:192.0.2.99", Source);
        Assert.AreEqual("Rejected socket IP claim", lines.Entries[0].Message);
        Assert.AreEqual("invalid", lines.Entries[0].Fields["ClaimedIp"]);
        Assert.AreEqual("192.0.2.10", lines.Entries[0].Fields["SourceIp"]);
        Assert.AreEqual("::ffff:192.0.2.99", lines.Entries[1].Fields["ClaimedIp"]);
    }
}
