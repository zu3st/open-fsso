using System.ComponentModel;
using Microsoft.Extensions.Logging;
using OpenFsso.Core;
using OpenFsso.Logging;

namespace OpenFsso.Client;

public readonly record struct FabricDecision(long Version, bool Allowed);

public sealed class FabricMonitor
{
    private readonly object gate = new();
    private readonly FabricEvaluator evaluator;
    private readonly IFabricNetwork network;
    private readonly ClientStatusJournal status;
    private readonly ILogger<FabricMonitor> logger;
    private FabricState state;
    private long version;
    public event Action? Changed;

    public FabricMonitor(ClientOptions options, IFabricNetwork network, ClientStatusJournal status, ILogger<FabricMonitor> logger)
    {
        evaluator = new(options.OnFabric);
        this.network = network;
        this.status = status;
        this.logger = logger;
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

            changed = state != next;
            if (changed)
            {
                version++;
                state = next;
                logger.Write(LogLevel.Information, "Fabric status changed", ("FabricState", state.ToString()));
            }

            status.Fabric(new(state, DateTimeOffset.UtcNow));
            decision = new(version, state is FabricState.Disabled or FabricState.OnFabric);
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
