using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using OpenFsso.Core;

namespace OpenFsso.Receiver;

// A WebSocket accepted from a Kerberos-authenticated upgrade request. HTTP.sys
// bound that proof to the TLS connection, so every message on it is the user's.
public static class PresenceSession
{
    private const int MaximumMessage = 4096;
    // Clients renew every 60 seconds; a silent socket is closed after this.
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);

    // Kerberos is checked once per session. Ending each session after about an
    // hour makes the client authenticate again, so a disabled account stops
    // renewing. The spread keeps clients from reconnecting all at once.
    private static TimeSpan Lifetime() => TimeSpan.FromMinutes(50 + Random.Shared.NextDouble() * 20);

    // A Kerberos ticket outlives its account, so a revoked account is refused
    // for this long before the domain is asked again.
    public static readonly TimeSpan RevocationBlock = TimeSpan.FromHours(1);

    // stopping ends every session when the receiver shuts down, so a restart
    // need not wait for clients and they reconnect to the new process.
    // check asks the domain about the account at each authentication, which
    // happens once per session, so about every hour.
    public static async Task Run(HttpContext context, SerializedLeases leases, RejectionLog rejections, Func<AuthenticatedUser, AccountCheck>? check, CancellationToken stopping, TimeSpan? lifetime = null)
    {
        if (!context.WebSockets.IsWebSocketRequest || context.Connection.RemoteIpAddress is not { } peer)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var user = new AuthenticatedUser(context.User.FindFirstValue(ClaimTypes.PrimarySid)!, context.User.Identity!.Name!);
        if (await leases.IsRevokedAsync(user.Sid, context.RequestAborted))
        {
            context.Response.StatusCode = 403;
            return;
        }

        if (check != null)
        {
            var account = check(user);
            if (account.State == AccountState.Revoked)
            {
                rejections.AccountRevoked(user, account.Detail);
                await leases.RevokeAsync(user.Sid, RevocationBlock, context.RequestAborted);
                context.Response.StatusCode = 403;
                return;
            }

            if (account.State == AccountState.Unknown)
            {
                rejections.AccountUnknown(user, account.Detail);
            }
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        // Cancelling a pending receive would abort the socket without a close
        // frame, so send the close beside it and let the client's reply end the loop.
        using var shutdown = stopping.Register(() => _ = CloseOutput(socket, WebSocketCloseStatus.EndpointUnavailable));
        using var expiry = new CancellationTokenSource(lifetime ?? Lifetime());
        using var expired = expiry.Token.Register(() => _ = CloseOutput(socket, WebSocketCloseStatus.NormalClosure));
        try
        {
            await Serve(socket, user, peer, leases, rejections, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            await Close(socket, WebSocketCloseStatus.NormalClosure);
        }
        catch (Exception e) when (e is WebSocketException or InvalidOperationException)
        {
        }
    }

    private static async Task Serve(WebSocket socket, AuthenticatedUser user, IPAddress peer, SerializedLeases leases, RejectionLog rejections, CancellationToken session)
    {
        var buffer = new byte[MaximumMessage];
        while (socket.State == WebSocketState.Open)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(session);
            idle.CancelAfter(Idle);
            int length = await Receive(socket, buffer, idle.Token);
            if (length < 0)
            {
                return;
            }

            SessionReply reply;
            try
            {
                var request = JsonSerializer.Deserialize(buffer.AsSpan(0, length), WireJson.Default.PresenceRequest);
                var decision = request == null ? null : await leases.PresenceAsync(user, peer, request, idle.Token);
                if (decision?.Status == 400)
                {
                    rejections.SocketClaim(request!.ConnectionLocalIp, peer);
                }

                reply = decision == null ? new(400, new(null, ReceiverOptions.LeaseSeconds, "invalid-json")) : new(decision.Status, decision.Reply);
            }
            catch (JsonException)
            {
                reply = new(400, new(null, ReceiverOptions.LeaseSeconds, "invalid-json"));
            }

            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(reply, WireJson.Default.SessionReply), WebSocketMessageType.Text, true, idle.Token);
        }
    }

    // The message length, or -1 when the client closed or sent an oversized or binary message.
    private static async Task<int> Receive(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        int length = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
            if (result.MessageType != WebSocketMessageType.Text)
            {
                await Close(socket, result.MessageType == WebSocketMessageType.Close ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.InvalidMessageType);
                return -1;
            }

            length += result.Count;
            if (result.EndOfMessage)
            {
                return length;
            }

            if (length == buffer.Length)
            {
                await Close(socket, WebSocketCloseStatus.MessageTooBig);
                return -1;
            }
        }
    }

    private static async Task CloseOutput(WebSocket socket, WebSocketCloseStatus status)
    {
        try
        {
            await socket.CloseOutputAsync(status, null, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static async Task Close(WebSocket socket, WebSocketCloseStatus status)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await socket.CloseAsync(status, null, deadline.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidOperationException)
        {
        }
    }
}
