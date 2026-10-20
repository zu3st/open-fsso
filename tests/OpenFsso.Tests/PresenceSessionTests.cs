using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenFsso.Core;
using OpenFsso.Receiver;

namespace OpenFsso.Tests;

// The receiver's session loop, with the authenticated upgrade and peer address
// that HTTP.sys would supply set by a test middleware.
[TestClass]
public sealed class PresenceSessionTests
{
    private static readonly Guid Generation = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static async Task<(WebApplication App, List<MappingChange> Changes)> Start(CancellationToken stopping = default, TimeSpan? lifetime = null, Func<AuthenticatedUser, AccountCheck>? check = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        var changes = new List<MappingChange>();
        var leases = new SerializedLeases(new LeaseStore(new FakeTimeProvider()), change =>
        {
            changes.Add(change);
            return Task.CompletedTask;
        });
        app.UseWebSockets();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "DOMAIN\\Alice"), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], "Windows"));
            return next(context);
        });
        app.Map("/v1/session", (HttpContext context) => PresenceSession.Run(context, leases, new RejectionLog(NullLogger.Instance, TimeProvider.System), check, stopping, lifetime));
        await app.StartAsync();
        return (app, changes);
    }

    private static async Task<SessionReply> Exchange(WebSocket socket, string message)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, default);
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, default);
        return JsonSerializer.Deserialize(buffer.AsSpan(0, result.Count), WireJson.Default.SessionReply)!;
    }

    private static string Presence(string ip, Guid? reference = null) => JsonSerializer.Serialize(new PresenceRequest(1, Generation, ip, reference), WireJson.Default.PresenceRequest);

    [TestMethod]
    public async Task OneSessionCarriesLoginAndRenewalsWithoutNewLogins()
    {
        var (app, changes) = await Start();
        await using var _ = app;
        using var socket = await app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/v1/session"), default);
        var first = await Exchange(socket, Presence("192.0.2.10"));
        Assert.AreEqual(200, first.Status);
        for (int renewal = 0; renewal < 3; renewal++)
        {
            Assert.AreEqual(200, (await Exchange(socket, Presence("192.0.2.10", first.Reply.LeaseReference))).Status);
        }

        Assert.AreEqual("login", changes.Single().Action);
        Assert.AreEqual("DOMAIN\\Alice", changes.Single().User.Name);
    }

    [TestMethod]
    public async Task ARevokedAccountIsLoggedOutAndRefusedWithoutAskingTheDomainAgain()
    {
        var states = new Queue<AccountState>([AccountState.Valid, AccountState.Revoked]);
        int checks = 0;
        var (app, changes) = await Start(check: _ =>
        {
            checks++;
            return new(states.Dequeue(), "test");
        });
        await using var _ = app;
        var client = app.GetTestServer().CreateWebSocketClient();
        using (var socket = await client.ConnectAsync(new Uri("ws://localhost/v1/session"), default))
        {
            Assert.AreEqual(200, (await Exchange(socket, Presence("192.0.2.10"))).Status);
        }

        // The next authentication finds the account revoked: logout, then 403.
        var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.ConnectAsync(new Uri("ws://localhost/v1/session"), default));
        StringAssert.Contains(refused.Message, "403");
        Assert.AreEqual("logout", changes.Last().Action);
        Assert.AreEqual("account-revoked", changes.Last().Reason);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.ConnectAsync(new Uri("ws://localhost/v1/session"), default));
        Assert.AreEqual(2, checks);
    }

    [TestMethod]
    public async Task StoppingTheReceiverClosesSessionsAsGoingAway()
    {
        using var stopping = new CancellationTokenSource();
        var (app, _) = await Start(stopping.Token);
        await using var _app = app;
        using var socket = await app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/v1/session"), default);
        Assert.AreEqual(200, (await Exchange(socket, Presence("192.0.2.10"))).Status);
        stopping.Cancel();
        var result = await socket.ReceiveAsync(new byte[16], new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.AreEqual(WebSocketMessageType.Close, result.MessageType);
        Assert.AreEqual(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);
    }

    [TestMethod]
    public async Task SessionsCloseAfterTheirLifetimeAndKeepTheLease()
    {
        var (app, changes) = await Start(lifetime: TimeSpan.FromMilliseconds(500));
        await using var _app = app;
        using var socket = await app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/v1/session"), default);
        Assert.AreEqual(200, (await Exchange(socket, Presence("192.0.2.10"))).Status);
        var result = await socket.ReceiveAsync(new byte[16], new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.AreEqual(WebSocketMessageType.Close, result.MessageType);
        Assert.AreEqual(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
        Assert.AreEqual("login", changes.Single().Action);
    }

    [TestMethod]
    public async Task CamelCaseMessagesAreAccepted()
    {
        var (app, _) = await Start();
        await using var _app = app;
        using var socket = await app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/v1/session"), default);
        Assert.AreEqual(200, (await Exchange(socket, "{\"version\":1,\"generation\":\"" + Generation + "\",\"connectionLocalIp\":\"192.0.2.10\"}")).Status);
    }

    [TestMethod]
    public async Task SessionRejectsForgedSocketClaimsAndMalformedMessagesButStaysOpen()
    {
        var (app, changes) = await Start();
        await using var _ = app;
        using var socket = await app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/v1/session"), default);
        Assert.AreEqual(400, (await Exchange(socket, Presence("192.0.2.99"))).Status);
        Assert.AreEqual("invalid-json", (await Exchange(socket, "{not json")).Reply.Error);
        Assert.AreEqual(200, (await Exchange(socket, Presence("192.0.2.10"))).Status);
        Assert.HasCount(1, changes);
    }
}
