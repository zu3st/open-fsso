using Microsoft.Extensions.Logging.Abstractions;
using OpenFsso.Client;
using OpenFsso.Configuration;
using OpenFsso.Core;

namespace OpenFsso.Tests;

[TestClass]
public sealed class FabricTests
{
    private const string Client = """
        Receivers:
          - {Name: A, Host: receiver-a.test, Address: 192.0.2.1, Port: 8443}
          - {Name: B, Host: receiver-b.test, Address: 192.0.2.2, Port: 8443}
        StateDirectory: /State
        LogDirectory: /Logs
        """;

    private static ClientOptions Options(string rule) => YamlConfiguration.Parse<ClientOptions>(Client + "\nonFabric:\n" + string.Join('\n', rule.Split('\n').Select(line => "  " + line)), ConfigurationSchema.Client);
    private static FabricNetwork Network(string ip, string? server = null, string? mac = null) => new(1, [ip], server == null ? [] : [new(server, mac)]);
    private static FabricNetwork Flags(int mask) => new(1, Enumerable.Range(0, 4).Where(bit => (mask & (1 << bit)) != 0).Select(bit => $"10.{bit + 1}.0.1").ToArray(), []);

    [TestMethod]
    public void AnyAcceptsDirectCidrAlternativesOrDhcpServerIp()
    {
        var evaluator = new FabricEvaluator(Options("""
                any:
                  - clientIp: [10.12.0.0/16, 10.13.13.0/24]
                  - dhcpServer: {ip: 10.14.14.1}
            """).OnFabric);
        Assert.IsTrue(evaluator.Matches([Network("10.12.255.254")]));
        Assert.IsTrue(evaluator.Matches([Network("10.13.13.0")]));
        Assert.IsFalse(evaluator.Matches([Network("10.13.14.1")]));
        Assert.IsTrue(evaluator.Matches([Network("192.0.2.20", "10.14.14.1")]));
        Assert.IsFalse(evaluator.Matches([]));
        Assert.IsEmpty(evaluator.MacTargets);
    }

    [TestMethod]
    public void AllRequiresClientIpAndDhcpIdentityOnTheSameAdapter()
    {
        var evaluator = new FabricEvaluator(Options("""
                all:
                  - clientIp: [10.12.0.0/16]
                  - dhcpServer: {ip: 10.14.14.1, mac: '00:11:22:33:44:55'}
            """).OnFabric);
        Assert.IsTrue(evaluator.Matches([Network("10.12.0.1", "10.14.14.1", "00-11-22-33-44-55")]));
        Assert.IsFalse(evaluator.Matches([Network("10.12.0.1"), Network("192.0.2.1", "10.14.14.1", "001122334455")]));
        Assert.IsFalse(evaluator.Matches([Network("10.12.0.1", "10.14.14.1", "001122334456")]));
        Assert.IsFalse(evaluator.Matches([Network("10.12.0.1", "10.14.14.1")]));
        Assert.IsFalse(evaluator.Matches([Network("10.12.0.1", "10.14.14.2", "001122334455")]));
        Assert.IsTrue(evaluator.MacTargets.SetEquals(["10.14.14.1"]));
    }

    [TestMethod]
    public void CidrsSupportIpv6AndDoNotMixAddressFamilies()
    {
        var evaluator = new FabricEvaluator(Options("""
                clientIp: ['2001:db8:1234::/48']
            """).OnFabric);
        Assert.IsTrue(evaluator.Matches([Network("2001:db8:1234::abcd")]));
        Assert.IsFalse(evaluator.Matches([Network("2001:db8:1235::1")]));
        Assert.IsFalse(evaluator.Matches([Network("10.12.0.1")]));
    }

    [TestMethod]
    public void SchemaAcceptsTheDocumentedExample() =>
        YamlConfiguration.Parse<ClientOptions>(File.ReadAllText("config/client.example.yaml"), ConfigurationSchema.Client);

    [TestMethod]
    [DataRow("    {}")]
    [DataRow("    null")]
    [DataRow("    any: []")]
    [DataRow("    all: []")]
    [DataRow("    any: [{clientIp: [10.0.0.0/8]}]")]
    [DataRow("    all: [{clientIp: [10.0.0.0/8]}]")]
    [DataRow("    any: [{all: []}, {clientIp: [10.0.0.0/8]}]")]
    [DataRow("    all: [{}, {clientIp: [10.0.0.0/8]}]")]
    [DataRow("    publicIp: 192.0.2.1")]
    [DataRow("    clientIp: []")]
    [DataRow("    clientIp: [10.0.0.1/999]")]
    [DataRow("    {clientIp: [10.0.0.0/8], dhcpServer: {ip: 10.0.0.1}}")]
    [DataRow("    {any: [{clientIp: [10.0.0.0/8]}, {clientIp: [192.0.2.0/24]}], all: [{clientIp: [10.0.0.0/8]}, {clientIp: [192.0.2.0/24]}]}")]
    [DataRow("    {not: {clientIp: [10.0.0.0/8]}, clientIp: [192.0.2.0/24]}")]
    [DataRow("    not: []")]
    [DataRow("    not: null")]
    [DataRow("    not: {}")]
    [DataRow("    dhcpServer: {mac: '001122334455'}")]
    [DataRow("    dhcpServer: {ip: invalid}")]
    [DataRow("    dhcpServer: {ip: 10.0.0.1, mac: invalid}")]
    public void SchemaRejectsMalformedGroupsAtTheirPath(string condition)
    {
        var error = Assert.Throws<InvalidDataException>(() => Options(condition));
        Assert.Contains("/onFabric", error.Message);
        Assert.DoesNotContain("/Receivers", error.Message);
        if (condition.Contains("999"))
        {
            Assert.Contains("/clientIp/0", error.Message.Split('\n')[1]);
            Assert.Contains("cidr", error.Message);
        }

        if (condition.Contains("mac: invalid"))
        {
            Assert.Contains("/dhcpServer/mac", error.Message.Split('\n')[1]);
            Assert.Contains("regular expression", error.Message);
        }
    }

    [TestMethod]
    public void RecursiveAndOrMatchesEveryRowOfTheCompoundTruthTable()
    {
        var evaluator = new FabricEvaluator(Options("""
            any:
              - all:
                  - clientIp: [10.1.0.1/32]
                  - clientIp: [10.2.0.1/32]
              - all:
                  - clientIp: [10.3.0.1/32]
                  - clientIp: [10.4.0.1/32]
            """).OnFabric);
        for (int mask = 0; mask < 16; mask++)
        {
            bool expected = (mask & 3) == 3 || (mask & 12) == 12;
            Assert.AreEqual(expected, evaluator.Matches([Flags(mask)]), $"Compound rule failed for {mask}.");
        }

        Assert.IsFalse(evaluator.Matches([Flags(5), Flags(10)]));
    }

    [TestMethod]
    public void DirectLeavesMixWithRecursiveAndOrAndUnaryNot()
    {
        var evaluator = new FabricEvaluator(Options("""
            any:
              - clientIp: [10.1.0.1/32]
              - all:
                  - clientIp: [10.2.0.1/32]
                  - not:
                      any:
                        - clientIp: [10.3.0.1/32]
                        - clientIp: [10.4.0.1/32]
              - clientIp: [10.4.0.1/32]
            """).OnFabric);
        for (int mask = 0; mask < 16; mask++)
        {
            bool expected = (mask & 1) != 0 || (mask & 2) != 0 && (mask & 12) == 0 || (mask & 8) != 0;
            Assert.AreEqual(expected, evaluator.Matches([Flags(mask)]), $"Mixed rule failed for {mask}.");
        }

        var not = new FabricEvaluator(Options("    not: {clientIp: [10.1.0.1/32]}").OnFabric);
        Assert.IsFalse(not.Matches([Flags(1)]));
        Assert.IsTrue(not.Matches([Flags(2)]));
        Assert.IsFalse(not.Matches([]));
        var twice = new FabricEvaluator(Options("    not: {not: {clientIp: [10.1.0.1/32]}}").OnFabric);
        Assert.IsTrue(twice.Matches([Flags(1)]));
        Assert.IsFalse(twice.Matches([Flags(2)]));
    }

    [TestMethod]
    public void NestedMacNegationObservesTheServerMacBeforeEvaluating()
    {
        var options = Options("    not: {dhcpServer: {ip: 10.14.14.1, mac: '00:11:22:33:44:55'}}");
        var source = new NetworkSource { Networks = [Network("10.12.0.1", "10.14.14.1", "001122334455")] };
        var monitor = new FabricMonitor(options, source, new ClientStatusJournal(["A", "B"]), NullLogger<FabricMonitor>.Instance);
        Assert.IsFalse(monitor.Check().Allowed);
        Assert.IsTrue(source.LastTargets.SetEquals(["10.14.14.1"]));
        Assert.AreEqual(1, source.Reads);
        source.Networks = [Network("10.12.0.1", "10.14.14.1", "001122334456")];
        Assert.IsTrue(monitor.Check().Allowed);
    }

    [TestMethod]
    public void RecursionSupportsDeepRulesWithinBoundedParsing()
    {
        static string Nested(int count)
        {
            string rule = "{clientIp: [10.12.0.0/16]}";
            for (int index = 0; index < count; index++)
            {
                rule = $"{{any: [{{clientIp: [203.0.113.0/24]}}, {rule}]}}";
            }

            return "    " + rule;
        }

        var evaluator = new FabricEvaluator(Options(Nested(32)).OnFabric);
        Assert.IsTrue(evaluator.Matches([Network("10.12.1.1")]));
        Assert.IsFalse(evaluator.Matches([Network("10.13.1.1")]));
        Assert.Throws<InvalidDataException>(() => Options(Nested(512)));
    }

    [TestMethod]
    public void SchemaReportsErrorPathsWithoutEchoingUnknownFieldValues()
    {
        const string secret = "private-value-that-must-not-appear";
        var error = Assert.Throws<InvalidDataException>(() => YamlConfiguration.Parse<ClientOptions>(Client + "\nUnknown: " + secret, ConfigurationSchema.Client));
        Assert.DoesNotContain(secret, error.ToString());
        Assert.Contains("Unknown", error.Message);
        Assert.Contains("Invalid client configuration", error.Message);
    }

    [TestMethod]
    public void TransitionsInvalidateInFlightSendsAndClearOldAcknowledgements()
    {
        var options = Options("    clientIp: [10.0.0.0/8]");
        var provider = new NetworkSource { Networks = [Network("10.1.2.3")] };
        var journal = new ClientStatusJournal(["A", "B"]);
        var monitor = new FabricMonitor(options, provider, journal, NullLogger<FabricMonitor>.Instance);
        var before = monitor.Check();
        Assert.IsTrue(before.Allowed);
        journal.Sent("A", 1, "10.1.2.3", DateTimeOffset.UtcNow);
        provider.Networks = [Network("192.0.2.1")];
        Assert.IsFalse(monitor.Check().Allowed);
        Assert.IsFalse(monitor.IsCurrent(before));
        journal.Sent("A", 1, "10.1.2.3", DateTimeOffset.UtcNow);
        var status = journal.Read(1, "DOMAIN\\Alice", DateTimeOffset.UtcNow);
        Assert.IsTrue(status.Receivers.All(value => value.LastSentUtc == null));
        var view = ClientStatusView.Create(status, DateTimeOffset.UtcNow);
        Assert.AreEqual(ClientIndicator.OffFabric, view.Indicator);
        Assert.HasCount(2, view.Text.Split('\n'));
        Assert.DoesNotContain("clientIp", view.Text);
        Assert.DoesNotContain("Connection", view.Text);
        provider.Networks = [Network("10.1.2.3")];
        var after = monitor.Check();
        Assert.IsTrue(after.Allowed && monitor.IsCurrent(after));
        Assert.IsFalse(monitor.IsCurrent(before));
        Assert.IsTrue(journal.Read(1, "DOMAIN\\Alice", DateTimeOffset.UtcNow).Receivers.All(value => value.LastSentUtc == null));
    }

    [TestMethod]
    public void DisabledDetectionSkipsNetworkInspectionAndErrorsSuppressSends()
    {
        var options = YamlConfiguration.Parse<ClientOptions>(Client, ConfigurationSchema.Client);
        var provider = new NetworkSource { Unavailable = true };
        var monitor = new FabricMonitor(options, provider, new(["A", "B"]), NullLogger<FabricMonitor>.Instance);
        Assert.IsTrue(monitor.Check().Allowed);
        Assert.AreEqual(0, provider.Reads);
        var journal = new ClientStatusJournal(["A", "B"]);
        monitor = new(Options("    clientIp: [10.0.0.0/8]"), provider, journal, NullLogger<FabricMonitor>.Instance);
        Assert.IsFalse(monitor.Check().Allowed);
        var status = journal.Read(1, "Alice", DateTimeOffset.UtcNow);
        Assert.AreEqual(FabricState.Unavailable, status.Fabric?.State);
        Assert.AreEqual(ClientIndicator.Error, ClientStatusView.Create(status, DateTimeOffset.UtcNow).Indicator);
    }

    private sealed class NetworkSource : IFabricNetwork
    {
        public IReadOnlyList<FabricNetwork> Networks { get; set; } = [];
        public bool Unavailable
        {
            get; set;
        }
        public int Reads
        {
            get; private set;
        }
        public IReadOnlySet<string> LastTargets { get; private set; } = new HashSet<string>();
        public IReadOnlyList<FabricNetwork> Capture(IReadOnlySet<string> resolveMacFor)
        {
            Reads++;
            LastTargets = resolveMacFor;
            return Unavailable ? throw new IOException() : Networks.Select(adapter => adapter with
            {
                DhcpServers = adapter.DhcpServers.Select(server => resolveMacFor.Contains(server.Ip) ? server : server with { Mac = null }).ToArray()
            }).ToArray();
        }
    }
}
