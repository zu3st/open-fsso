using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenFsso.Receiver;

namespace OpenFsso.Tests;

[TestClass]
public sealed class HealthAuthenticationTests
{
    private const string Key = "test-only-health-key-0123456789abcdef";
    private static WebApplication? app;
    private static HttpClient Client => app!.GetTestClient();

    // The receiver's authentication wiring with stand-in endpoints. A test header
    // stands in for the identity HTTP.sys would attach natively.
    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddReceiverAuthentication(Key);
        app = builder.Build();
        app.Use((context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Test-Kerberos-Claims"))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "DOMAIN\\Alice")], "Kerberos"));
            }

            return next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/health", () => "healthy").RequireHealthKey();
        app.Map("/v1/session", (HttpContext context) => context.User.FindFirstValue(ClaimTypes.PrimarySid));
        app.MapPost("/v1/release", () => "release").AllowAnonymous();
        await app.StartAsync();
    }

    [ClassCleanup]
    public static async Task Stop() => await app!.DisposeAsync();

    private static async Task<HttpResponseMessage> Send(HttpMethod method, string path, params string[] authorization)
    {
        var request = new HttpRequestMessage(method, path);
        foreach (string value in authorization)
        {
            request.Headers.TryAddWithoutValidation("Authorization", value);
        }

        return await Client.SendAsync(request);
    }

    private sealed class CountingLogs : ILoggerProvider, ILogger
    {
        public int Count;
        public ILogger CreateLogger(string category) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) => Interlocked.Increment(ref Count);
        public void Dispose()
        {
        }
    }

    [TestMethod]
    public async Task AnonymousRequestsCannotFloodTheLog()
    {
        var logs = new CountingLogs();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddProvider(logs).AddFilter("Microsoft", LogLevel.Warning);
        builder.Services.AddReceiverAuthentication(Key);
        await using var flooded = builder.Build();
        flooded.UseAuthentication();
        flooded.UseAuthorization();
        flooded.MapGet("/health", () => "healthy").RequireHealthKey();
        flooded.Map("/v1/session", () => "session");
        await flooded.StartAsync();
        logs.Count = 0;
        var client = flooded.GetTestClient();
        for (int request = 0; request < 50; request++)
        {
            var wrong = new HttpRequestMessage(HttpMethod.Get, "/health");
            wrong.Headers.TryAddWithoutValidation("Authorization", "Bearer wrong");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/session")).StatusCode);
        }

        Assert.AreEqual(RejectionLog.Limit, logs.Count);
    }

    [TestMethod]
    [DataRow("Bearer")]
    [DataRow("bearer")]
    public async Task HealthAcceptsOnlyConfiguredBearerKeyWithoutWindowsIdentity(string scheme)
    {
        var response = await Send(HttpMethod.Get, "/health", scheme + " " + Key);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(Key)]
    [DataRow("Basic " + Key)]
    [DataRow("Kerberos token")]
    [DataRow("Bearer")]
    [DataRow("Bearer wrong")]
    [DataRow("Bearer TEST-ONLY-HEALTH-KEY-0123456789ABCDEF")]
    [DataRow("Bearer " + Key + ", " + Key)]
    [DataRow("Bearer " + Key + " extra")]
    public async Task HealthRejectsMissingWrongAndMalformedCredentialsWithBearerChallenge(string? value)
    {
        await Challenged(await Send(HttpMethod.Get, "/health", value == null ? [] : [value]), "Bearer");
    }

    [TestMethod]
    public async Task HealthRejectsDuplicateCredentials() =>
        await Challenged(await Send(HttpMethod.Get, "/health", "Bearer " + Key, "Bearer " + Key), "Bearer");

    [TestMethod]
    public async Task KerberosIdentityAloneCannotAuthorizeHealth()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Test-Kerberos-Claims", "1");
        await Challenged(await Client.SendAsync(request), "Bearer");
    }

    [TestMethod]
    [DataRow("GET", "/v1/session")]
    [DataRow("GET", "/health/anything")]
    public async Task BearerKeyNeverAuthorizesPresenceOrUnmappedPaths(string method, string path) =>
        await Challenged(await Send(new HttpMethod(method), path, "Bearer " + Key), "Kerberos");

    [TestMethod]
    public async Task PresenceRequiresNativeWindowsTokenRatherThanAssertedAuthenticationType()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/session");
        request.Headers.Add("X-Test-Kerberos-Claims", "1");
        await Challenged(await Client.SendAsync(request), "Kerberos");
    }

    [TestMethod]
    public async Task ReleaseIsReachableWithoutCredentials()
    {
        var response = await Send(HttpMethod.Post, "/v1/release");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("release", await response.Content.ReadAsStringAsync());
    }

    private static async Task Challenged(HttpResponseMessage response, string scheme)
    {
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(scheme, response.Headers.WwwAuthenticate.ToString());
        Assert.AreEqual("", await response.Content.ReadAsStringAsync());
    }
}
