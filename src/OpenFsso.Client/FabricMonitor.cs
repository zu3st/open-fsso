using System.ComponentModel;
using Microsoft.Extensions.Logging;
using OpenFsso.Core;
using OpenFsso.Logging;
using OpenFsso.Windows;

namespace OpenFsso.Client;

// SourceIp is the FSSO address: the local address routed to the first receiver.
// Every receiver connection uses it, so all receivers map the same address.
public readonly record struct FabricDecision(long Version, bool Allowed, string? SourceIp);

public sealed class FabricMonitor
{
    private readonly object gate = new();
    private readonly FabricEvaluator evaluator;
    private readonly IFabricNetwork network;
    private readonly ClientStatusJournal status;
    private readonly ILogger<FabricMonitor> logger;
    private readonly ReceiverEndpoint first;
    private FabricState state;
    private string? source;
    private long version;
    public event Action? Changed;

    public FabricMonitor(ClientOptions options, IFabricNetwork network, ClientStatusJournal status, ILogger<FabricMonitor> logger)
    {
        evaluator = new(options.OnFabric);
        this.network = network;
        this.status = status;
        this.logger = logger;
        first = options.Receivers[0];
        state = evaluator.Enabled ? FabricState.Unavailable : FabricState.Disabled;
        status.Fabric(new(state, DateTimeOffset.UtcNow));
    }

    public FabricDecision Check()
    {
        bool changed;
        FabricDecision decision;
        lock (gate)
        {
            FabricState next;
            try
            {
                if (!evaluator.Enabled)
                {
                    next = FabricState.Disabled;
                }
                else
                {
                    next = evaluator.Matches(network.Capture(evaluator.MacTargets)) ? FabricState.OnFabric : FabricState.OffFabric;
                }
            }
            catch (Exception error) when (error is Win32Exception or IOException)
            {
                next = FabricState.Unavailable;
            }

            string? routed = network.RouteSource(first.Address, first.Port);
            changed = state != next;
            if (changed)
            {
                version++;
                state = next;
                logger.Write(LogEvents.FabricChanged, ("FabricState", state.ToString()), ("SourceIp", routed));
            }
            else if (routed != source)
            {
                logger.Write(LogEvents.AddressChanged, ("SourceIp", routed));
            }

            source = routed;
            status.Fabric(new(state, DateTimeOffset.UtcNow));
            decision = new(version, state is FabricState.Disabled or FabricState.OnFabric, routed);
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return decision;
    }

    public bool IsCurrent(FabricDecision decision)
    {
        lock (gate)
        {
            return decision.Allowed && decision.Version == version && state is FabricState.Disabled or FabricState.OnFabric;
        }
    }
}
