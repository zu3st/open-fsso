using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using OpenFsso.Receiver;

namespace OpenFsso.Tests;

[TestClass]
public sealed class RejectionLogTests
{
    private sealed class Lines : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
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

        Assert.HasCount(RejectionLog.Limit, lines.Messages);
        clock.Advance(TimeSpan.FromSeconds(61));
        log.Identity("NTLM");
        Assert.Contains("Suppressed 15", lines.Messages[RejectionLog.Limit]);
        Assert.Contains("NTLM", lines.Messages[^1]);
    }

    [TestMethod]
    public void OnlyParsedAddressesReachTheLog()
    {
        var lines = new Lines();
        var log = new RejectionLog(lines, new FakeTimeProvider());
        log.SocketClaim(new string('x', 4000), Source);
        log.SocketClaim("::ffff:192.0.2.99", Source);
        Assert.AreEqual("Rejected socket IP claim invalid from 192.0.2.10", lines.Messages[0]);
        Assert.AreEqual("Rejected socket IP claim ::ffff:192.0.2.99 from 192.0.2.10", lines.Messages[1]);
    }
}
