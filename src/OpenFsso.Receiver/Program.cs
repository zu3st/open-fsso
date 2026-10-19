using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Server.HttpSys;
using OpenFsso.Configuration;
using OpenFsso.Core;
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
    var options = YamlConfiguration.Load<ReceiverOptions>(args[1], ConfigurationSchema.Receiver);
    options.Validate();
    return options;
}

if (args[0] == "--validate-config")
{
    try
    {
        var settings = ReadOptions();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            settings.LogDirectory,
            settings.ServiceName
        }));
    }
    catch (InvalidDataException error)
    {
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }

    return;
}

var options = ReadOptions();
using var log = OpenFsso.Logging.Logging.Create(options.LogDirectory, "open-fsso-receiver", 100L * 1024 * 1024, options.HealthApiKey);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.Sources.Clear();
builder.Configuration.AddInMemoryCollection();
builder.Configuration["FORWARDEDHEADERS_ENABLED"] = "false";
builder.WebHost.UseSetting("forwardedHeaders_enabled", "false");
builder.Logging.ClearProviders();
builder.Services.AddSerilog(log, dispose: false);
builder.Services.AddWindowsService(service => service.ServiceName = options.ServiceName);
string prefix = $"https://{options.Host}:{options.Port}/";
builder.WebHost.UseHttpSys(http =>
{
    http.RequestQueueName = options.QueueName;
    http.RequestQueueMode = RequestQueueMode.Create;
    http.UrlPrefixes.Add(prefix);
    // Native Kerberos and strict CBT are applied/queried on the URL group below.
    // None here suppresses the framework's automatic Kerberos challenge on
    // every 401; the managed gate selects Bearer or Kerberos for each endpoint.
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
            app.Logger.LogError("Native HTTP.sys connection timing is unavailable; requests fail closed");
        }

        return;
    }

    await next(context);
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
        request = await context.Request.ReadFromJsonAsync<PresenceRequest>(cancellationToken: deadline.Token);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new
        {
            error = "invalid-json"
        });
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

    return Results.Json(decision.Reply, statusCode: decision.Status);
}

app.Map("/v1/session", (HttpContext context, SerializedLeases leases) => PresenceSession.Run(context, leases, rejections, app.Lifetime.ApplicationStopping));
// Release follows logoff, when the user's Kerberos credentials no longer exist.
// The lease reference returned with each presence authorizes it instead.
app.MapPost("/v1/release", (HttpContext context, SerializedLeases leases) => Mapping(context, leases.ReleaseAsync)).AllowAnonymous();
app.MapGet("/health", (HttpContext context, SerializedLeases leases, SyslogRelay relay, LeaseWorker worker) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(new
    {
        ready = Volatile.Read(ref ready) != 0,
        activeLeases = leases.Count,
        collectorRunning = worker.CollectorAvailable,
        localSocketDelivery = relay.Healthy,
        collectorAcceptance = "not-confirmed-by-udp"
    }, statusCode: worker.CollectorAvailable && relay.Healthy ? 200 : 503);
}).RequireHealthKey();
await app.StartAsync();
try
{
    StrictChannelBinding.Enable(options.QueueName, prefix);
    startup.Activate(System.Diagnostics.Stopwatch.GetTimestamp());
    Volatile.Write(ref ready, 1);
    app.Logger.LogInformation("Receiver ready; Kerberos presence authentication and strict channel binding verified; Bearer health authentication enabled; runtime {Runtime}", Environment.Version.ToString());
    await app.WaitForShutdownAsync();
}
finally
{
    Volatile.Write(ref ready, 0);
    await app.StopAsync();
}
