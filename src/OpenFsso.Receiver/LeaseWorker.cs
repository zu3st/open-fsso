using OpenFsso.Core;
using OpenFsso.Logging;
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
                logger.Write(LogLevel.Warning, "Collector availability changed", ("CollectorAvailable", pid != 0), ("CollectorPid", pid));
            }

            Volatile.Write(ref collectorAvailable, pid != 0);
            previousPid = pid;
            long now = TimeProvider.System.GetTimestamp();
            bool periodic = options.ReassertSeconds > 0 && TimeProvider.System.GetElapsedTime(lastReassert) >= TimeSpan.FromSeconds(options.ReassertSeconds);
            string? reassert = pid == 0 ? null : restart || !relay.Healthy ? "login" : periodic ? "update" : null;
            long deliveryVersion = relay.Recovery.Version;
            await leases.TickAsync(reassert, ct);
            if (reassert != null)
            {
                relay.Recovery.CompleteReassert(deliveryVersion);
                lastReassert = now;
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
