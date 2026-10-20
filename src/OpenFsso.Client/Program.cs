using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenFsso.Client;
using OpenFsso.Configuration;
using OpenFsso.Core;
using OpenFsso.Windows;
using Serilog;

// Prints the running service's status as JSON, for scripts and monitoring.
if (args is ["--status"])
{
    var status = await StatusReader.Read(default);
    Console.WriteLine(JsonSerializer.Serialize(new StatusSnapshot(status, ClientStatusView.Create(status, DateTimeOffset.UtcNow)), StatusJson.Default.StatusSnapshot));
    Environment.ExitCode = status == null ? 1 : 0;
    return;
}

if (args.Length != 2 || args[0] is not ("--config" or "--validate-config") || !Path.IsPathFullyQualified(args[1]))
{
    throw new ArgumentException("Usage: OpenFsso.Client --status | --config|--validate-config <absolute protected YAML path>");
}

ClientOptions ReadOptions()
{
    var options = YamlConfiguration.Load(args[1], ConfigurationSchema.Client, ClientJson.Default.ClientOptions);
    options.Validate();
    return options;
}

if (args[0] == "--validate-config")
{
    try
    {
        var options = ReadOptions();
        Console.WriteLine(JsonSerializer.Serialize(new ClientPaths(options.LogDirectory, options.StateDirectory), ClientJson.Default.ClientPaths));
    }
    catch (InvalidDataException error)
    {
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }

    return;
}

using (var identity = WindowsIdentity.GetCurrent())
{
    if (!identity.IsSystem)
    {
        throw new InvalidOperationException("The agent service must run as LocalSystem.");
    }
}

// Connect to the SCM before building the host so a slow cold start can request
// more time; WindowsServiceLifetime would connect only after the host is built.
using var service = new AgentService(() =>
{
    var options = ReadOptions();
    var log = OpenFsso.Logging.Logging.Create(options.LogDirectory, "open-fsso-client", null, 20L * 1024 * 1024);
    try
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(log, dispose: false);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<Ownership>();
        builder.Services.AddSingleton(new OpenFsso.Core.ClientStatusJournal(options.Receivers.Select(x => x.Name)));
        builder.Services.AddSingleton<IFabricNetwork, WindowsFabricNetwork>();
        builder.Services.AddSingleton<FabricMonitor>();
        builder.Services.AddHostedService<StatusPipe>();
        builder.Services.AddHostedService<TrayStartup>();
        builder.Services.AddSingleton<AgentWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentWorker>());
        var host = builder.Build();
        return new ClientRuntime(host, host.Services.GetRequiredService<Ownership>(), host.Services.GetRequiredService<AgentWorker>(), log);
    }
    catch
    {
        log.Dispose();
        throw;
    }
});
ServiceBase.Run(service);
