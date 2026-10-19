using System.ServiceProcess;

namespace OpenFsso.Receiver;

public sealed record ReceiverRuntime(WebApplication App, Func<Task> Stop);

public sealed class ReceiverService : ServiceBase
{
    private readonly Func<Task<ReceiverRuntime>> start;
    private ReceiverRuntime? runtime;
    private bool stopping;

    public ReceiverService(Func<Task<ReceiverRuntime>> start)
    {
        this.start = start;
        ServiceName = "OpenFssoReceiver";
        CanShutdown = true;
        // A failed start, such as invalid configuration, goes to the Application log.
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        RequestAdditionalTime(60000);
        runtime = start().GetAwaiter().GetResult();
        runtime.App.Lifetime.ApplicationStopping.Register(() =>
        {
            if (!Volatile.Read(ref stopping))
            {
                Environment.Exit(1);
            }
        });
    }

    protected override void OnStop()
    {
        Volatile.Write(ref stopping, true);
        RequestAdditionalTime(30000);
        runtime?.Stop().GetAwaiter().GetResult();
    }

    protected override void OnShutdown()
    {
        Volatile.Write(ref stopping, true);
        runtime?.Stop().GetAwaiter().GetResult();
    }
}
