using OpenFsso.Core;
using OpenFsso.Windows;

namespace OpenFsso.Receiver;

public sealed class LeaseWorker(SerializedLeases leases, SyslogRelay relay, ReceiverOptions options, ILogger<LeaseWorker> logger) : BackgroundService
{
    private uint previousPid;
    private long lastReassert;
    private bool collectorAvailable;
    public bool CollectorAvailable => Volatile.Read(ref collectorAvailable);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.ExpirySeconds));
        do
        {
            uint pid = 0;
            try
            {
                pid = ServiceProcess.RunningPid(options.CollectorService);
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            bool restart = pid != 0 && pid != previousPid;
            if ((pid != 0) != collectorAvailable)
            {
                logger.LogWarning("Collector availability changed to {Available}; PID {CollectorPid}", pid != 0, pid);
            }

            Volatile.Write(ref collectorAvailable, pid != 0);
            previousPid = pid;
            long now = TimeProvider.System.GetTimestamp();
            bool periodic = options.ReassertSeconds > 0 && TimeProvider.System.GetElapsedTime(lastReassert) >= TimeSpan.FromSeconds(options.ReassertSeconds);
            bool reassert = pid != 0 && (restart || periodic || !relay.Healthy);
            long deliveryVersion = relay.Recovery.Version;
            await leases.TickAsync(reassert, ct);
            if (reassert)
            {
                relay.Recovery.CompleteReassert(deliveryVersion);
                lastReassert = now;
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
