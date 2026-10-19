using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenFsso.Core;
using OpenFsso.Windows;

namespace OpenFsso.Client;

public sealed class AgentWorker(Ownership ownership, ClientOptions options, ClientStatusJournal status, FabricMonitor fabric, ILogger<AgentWorker> logger) : BackgroundService
{
    private readonly ReceiverSender[] senders = options.Receivers.Select(x => new ReceiverSender(x, ownership, status, fabric, logger)).ToArray();
    private Timer? debounce;
    public void NetworkChanged()
    {
        try
        {
            debounce?.Change(1500, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void WakeAll()
    {
        foreach (var sender in senders)
        {
            sender.Wake();
        }
    }

    private void AddressChanged(object? sender, EventArgs e) => NetworkChanged();
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        debounce = new Timer(_ => WakeAll(), null, Timeout.Infinite, Timeout.Infinite);
        using var routes = new RouteNotifications(NetworkChanged);
        ownership.Changed += WakeAll;
        fabric.Changed += WakeAll;
        NetworkChange.NetworkAddressChanged += AddressChanged;
        try
        {
            await Task.WhenAll(senders.Select(x => x.Run(stopping)));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        finally
        {
            routes.Dispose();
            ownership.Changed -= WakeAll;
            fabric.Changed -= WakeAll;
            NetworkChange.NetworkAddressChanged -= AddressChanged;
            await debounce.DisposeAsync();
        }
    }
}
