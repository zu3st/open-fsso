using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Serialization;

namespace OpenFsso.Core;

public sealed record FabricRule
{
    [JsonPropertyName("not")]
    public FabricRule? Not
    {
        get; init;
    }
    [JsonPropertyName("any")]
    public FabricRule[]? Any
    {
        get; init;
    }
    [JsonPropertyName("all")]
    public FabricRule[]? All
    {
        get; init;
    }
    [JsonPropertyName("clientIp")]
    public string[]? ClientIp
    {
        get; init;
    }
    [JsonPropertyName("dhcpServer")]
    public DhcpServerCondition? DhcpServer
    {
        get; init;
    }
}

public sealed record DhcpServerCondition
{
    [JsonPropertyName("ip")]
    public required string Ip
    {
        get; init;
    }
    [JsonPropertyName("mac")]
    public string? Mac
    {
        get; init;
    }
}

public sealed record DhcpServerObservation(string Ip, string? Mac);
public sealed record FabricNetwork(int InterfaceIndex, string[] ClientIps, DhcpServerObservation[] DhcpServers);

public interface IFabricNetwork
{
    IReadOnlyList<FabricNetwork> Capture(IReadOnlySet<string> resolveMacFor);

    // The local address Windows routes to a destination, or null without a route.
    string? RouteSource(string address, int port);
}

// Each complete rule sees one adapter; unrelated adapters cannot complete an AND.
public sealed class FabricEvaluator
{
    private readonly Func<FabricNetwork, bool> matches;
    public bool Enabled
    {
        get;
    }
    public IReadOnlySet<string> MacTargets
    {
        get;
    }

    public FabricEvaluator(FabricRule? rule)
    {
        Enabled = rule != null;
        var targets = new HashSet<string>(StringComparer.Ordinal);
        matches = rule == null ? _ => true : Compile(rule, targets);
        MacTargets = targets;
    }

    public bool Matches(IReadOnlyList<FabricNetwork> networks) => !Enabled || networks.Any(network => matches(network));

    private static Func<FabricNetwork, bool> Compile(FabricRule condition, HashSet<string> targets)
    {
        if (condition.Not is { } negated)
        {
            var child = Compile(negated, targets);
            return adapter => !child(adapter);
        }

        if (condition.All is { } all)
        {
            var children = all.Select(child => Compile(child, targets)).ToArray();
            return adapter => children.All(test => test(adapter));
        }

        if (condition.Any is { } any)
        {
            var children = any.Select(child => Compile(child, targets)).ToArray();
            return adapter => children.Any(test => test(adapter));
        }

        if (condition.ClientIp is { } ranges)
        {
            var networks = ranges.Select(IPNetwork.Parse).ToArray();
            return adapter => adapter.ClientIps.Any(text => IPAddress.TryParse(text, out var address) && networks.Any(network => network.Contains(address)));
        }

        var expected = condition.DhcpServer!;
        var ip = IPAddress.Parse(expected.Ip);
        var mac = expected.Mac == null ? null : PhysicalAddress.Parse(expected.Mac);
        if (mac != null)
        {
            targets.Add(ip.ToString());
        }

        return adapter => adapter.DhcpServers.Any(server => IPAddress.TryParse(server.Ip, out var address) && ip.Equals(address) &&
            (mac == null || server.Mac != null && PhysicalAddress.TryParse(server.Mac, out var actual) && mac.Equals(actual)));
    }
}

public enum FabricState
{
    Disabled,
    OnFabric,
    OffFabric,
    Unavailable
}

public sealed record FabricObservation(FabricState State, DateTimeOffset ObservedUtc);
