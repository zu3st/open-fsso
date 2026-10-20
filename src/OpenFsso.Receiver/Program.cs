using System.Net;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.Extensions.Hosting.WindowsServices;
using OpenFsso.Configuration;
using OpenFsso.Core;
using OpenFsso.Logging;
using OpenFsso.Receiver;
using OpenFsso.Windows;
using Serilog;

if (args.Length != 2 || args[0] is not ("--config" or "--validate-config"))
{
    throw new ArgumentException("Usage: OpenFsso.Receiver --config|--validate-config <absolute protected YAML path>");
}

if (!Path.IsPathFullyQualified(args[1]))
{
    throw new ArgumentException("An absolute configuration path is required.");
}

ReceiverOptions ReadOptions()
{
    var options = YamlConfiguration.Load(args[1], ConfigurationSchema.Receiver, ReceiverJson.Default.ReceiverOptions);
    options.Validate();
    return options;
}

if (args[0] == "--validate-config")
{
    try
    {
        var settings = ReadOptions();
        Console.WriteLine(JsonSerializer.Serialize(new ReceiverPaths(settings.LogDirectory, settings.ServiceName), ReceiverJson.Default.ReceiverPaths));
    }
    catch (InvalidDataException error)
    {
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }

    return;
}

// Connect to the SCM before building the host, so a slow first start can
// request more time instead of failing the installation.
if (WindowsServiceHelpers.IsWindowsService())
{
    using var service = new ReceiverService(Start);
    try
    {
        ServiceBase.Run(service);
    }
    catch (Exception)
    {
        // Run rethrows a failed start, which ServiceBase has already logged.
        Environment.ExitCode = 1;
    }

    return;
}

var console = await Start();
await console.App.WaitForShutdownAsync();
await console.Stop();

async Task<ReceiverRuntime> Start()
{
    var options = ReadOptions();
    var log = OpenFsso.Logging.Logging.Create(options.LogDirectory, "open-fsso-receiver", options.ServiceName, 100L * 1024 * 1024, options.HealthApiKey);
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
    // Only the YAML file configures the receiver. Forwarded headers stay off,
    // since the client's IP address must come from the TCP connection.
    builder.Configuration.Sources.Clear();
    builder.Configuration.AddInMemoryCollection([new("FORWARDEDHEADERS_ENABLED", "false")]);
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(log, dispose: false);
    string prefix = $"https://{options.Host}:{options.Port}/";
    builder.WebHost.UseHttpSys(http =>
    {
        http.RequestQueueName = options.QueueName;
        http.RequestQueueMode = RequestQueueMode.Create;
        http.UrlPrefixes.Add(prefix);
        // Kerberos and strict channel binding are set on the URL group; see StrictChannelBinding.
        http.Authentication.Schemes = Microsoft.AspNetCore.Server.HttpSys.AuthenticationSchemes.None;
        http.Authentication.AllowAnonymous = true;
        http.Authentication.AutomaticAuthentication = true;
        http.Authentication.EnableKerberosCredentialCaching = false;
        http.MaxRequestBodySize = 4096;
        // Each client holds one session per receiver.
        http.MaxConnections = 100_000;
    });
    builder.Services.AddSingleton(options);
    builder.Services.AddReceiverAuthentication(options.HealthApiKey);
    builder.Services.AddSingleton(new LeaseStore(TimeProvider.System, ReceiverOptions.LeaseSeconds));
    builder.Services.AddSingleton<SyslogRelay>();
    builder.Services.AddSingleton(sp => new SerializedLeases(sp.GetRequiredService<LeaseStore>(), sp.GetRequiredService<SyslogRelay>().Emit));
    builder.Services.AddSingleton<LsaAccountStatus>();
    builder.Services.AddSingleton<LeaseWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<LeaseWorker>());
    var app = builder.Build();
    int ready = 0;
    var startup = new StartupBarrier();
    int timingWarning = 0;
    app.UseRouting();
    app.Use(async (context, next) =>
    {
        if (Volatile.Read(ref ready) == 0)
        {
            context.Response.StatusCode = 503;
            return;
        }

        // HTTP.sys starts listening before its URL group can be queried/configured.
        // Kernel QPC timestamps exclude connections accepted in that startup gap,
        // even if their authenticated requests reach middleware after ready is set.
        var timing = context.Features.Get<IHttpSysRequestTimingFeature>();
        if (timing == null || !timing.TryGetTimestamp(HttpSysRequestTimingType.ConnectionStart, out long connected) || !startup.Accepts(connected))
        {
            context.Response.StatusCode = 503;
            context.Response.Headers.Connection = "close";
            if (timing == null && Interlocked.Exchange(ref timingWarning, 1) == 0)
            {
                app.Logger.Write(LogEvents.ConnectionTimingUnavailable);
            }

            return;
        }

        // Every log line of a request names the client's address, including the framework's authentication events.
        using (app.Logger.BeginScope(new List<KeyValuePair<string, object?>> { new("SourceIp", context.Connection.RemoteIpAddress?.ToString()) }))
        {
            await next(context);
        }
    });
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30), KeepAliveTimeout = TimeSpan.FromSeconds(30) });
    var rejections = app.Services.GetRequiredService<RejectionLog>();
    async Task<IResult> Mapping(HttpContext context, Func<IPAddress, PresenceRequest, CancellationToken, Task<Decision>> apply)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        PresenceRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(WireJson.Default.PresenceRequest, deadline.Token);
        }
        catch (JsonException)
        {
            return Results.Json(new PresenceReply(null, ReceiverOptions.LeaseSeconds, "invalid-json"), WireJson.Default.PresenceReply, statusCode: 400);
        }

        if (request == null || context.Connection.RemoteIpAddress == null)
        {
            return Results.BadRequest();
        }

        var decision = await apply(context.Connection.RemoteIpAddress, request, deadline.Token);
        if (decision.Status == 400)
        {
            rejections.SocketClaim(request.ConnectionLocalIp, context.Connection.RemoteIpAddress);
        }

        return Results.Json(decision.Reply, WireJson.Default.PresenceReply, statusCode: decision.Status);
    }

    var accounts = app.Services.GetRequiredService<LsaAccountStatus>();
    app.Map("/v1/session", (HttpContext context, SerializedLeases leases) => PresenceSession.Run(context, leases, rejections, accounts.Check, app.Lifetime.ApplicationStopping));
    // Release follows logoff, when the user's Kerberos credentials no longer exist.
    // The lease reference returned with each presence authorizes it instead.
    app.MapPost("/v1/release", (HttpContext context, SerializedLeases leases) => Mapping(context, leases.ReleaseAsync)).AllowAnonymous();
    app.MapGet("/health", (HttpContext context, SerializedLeases leases, SyslogRelay relay, LeaseWorker worker) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        var report = new HealthReport(Volatile.Read(ref ready) != 0, leases.Count, worker.CollectorAvailable, relay.Healthy, "not-confirmed-by-udp");
        return Results.Json(report, HealthJson.Default.HealthReport, statusCode: worker.CollectorAvailable && relay.Healthy ? 200 : 503);
    }).RequireHealthKey();
    await app.StartAsync();
    try
    {
        StrictChannelBinding.Enable(options.QueueName, prefix);
    }
    catch
    {
        await app.StopAsync();
        await app.DisposeAsync();
        log.Dispose();
        throw;
    }

    startup.Activate(System.Diagnostics.Stopwatch.GetTimestamp());
    Volatile.Write(ref ready, 1);
    app.Logger.Write(LogEvents.ReceiverReady, ("Runtime", Environment.Version.ToString()));
    return new ReceiverRuntime(app, async () =>
    {
        Volatile.Write(ref ready, 0);
        await app.StopAsync();
        await app.DisposeAsync();
        log.Dispose();
    });
}
