using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenFsso.Client;

public sealed record ClientRuntime(IHost Host, Ownership Ownership, AgentWorker Worker, IDisposable Log);
public sealed class AgentService : ServiceBase
{
    private readonly Func<ClientRuntime> initialize;
    private bool stopping;
    private ClientRuntime? runtime;
    public AgentService(Func<ClientRuntime> initialize)
    {
        this.initialize = initialize;
        ServiceName = "OpenFssoClient";
        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        RequestAdditionalTime(60000);
        runtime = initialize();
        runtime.Host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
        {
            if (!Volatile.Read(ref stopping))
            {
                Environment.Exit(1);
            }
        });
        runtime.Ownership.Restore();
        runtime.Host.StartAsync().GetAwaiter().GetResult();
        runtime.Host.Services.GetRequiredService<ILogger<AgentService>>().LogInformation("Client service ready; runtime {Runtime}", Environment.Version.ToString());
    }

    protected override void OnStop()
    {
        Volatile.Write(ref stopping, true);
        RequestAdditionalTime(30000);
        runtime?.Host.StopAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
    }

    protected override void OnShutdown()
    {
        Volatile.Write(ref stopping, true);
        runtime?.Host.StopAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        if (change.Reason is SessionChangeReason.SessionLogon or SessionChangeReason.SessionUnlock)
        {
            runtime?.Ownership.Select((uint)change.SessionId);
        }
        else if (change.Reason == SessionChangeReason.SessionLogoff)
        {
            runtime?.Ownership.Logoff((uint)change.SessionId);
        }
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus status)
    {
        if (status is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeCritical)
        {
            runtime?.Worker.NetworkChanged();
        }

        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && runtime != null)
        {
            Volatile.Write(ref stopping, true);
            runtime.Host.Dispose();
            runtime.Log.Dispose();
            runtime = null;
        }

        base.Dispose(disposing);
    }
}
