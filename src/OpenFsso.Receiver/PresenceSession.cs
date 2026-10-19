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

    // stopping ends every session when the receiver shuts down, so a restart
    // need not wait for clients and they reconnect to the new process.
    public static async Task Run(HttpContext context, SerializedLeases leases, RejectionLog rejections, CancellationToken stopping)
    {
        if (!context.WebSockets.IsWebSocketRequest || context.Connection.RemoteIpAddress is not { } peer)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var user = new AuthenticatedUser(context.User.FindFirstValue(ClaimTypes.PrimarySid)!, context.User.Identity!.Name!);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        // Cancelling a pending receive would abort the socket without a close
        // frame, so send the close beside it and let the client's reply end the loop.
        using var shutdown = stopping.Register(() => _ = GoingAway(socket));
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
                var request = JsonSerializer.Deserialize<PresenceRequest>(buffer.AsSpan(0, length), JsonSerializerOptions.Web);
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

            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(reply, JsonSerializerOptions.Web), WebSocketMessageType.Text, true, idle.Token);
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

    private static async Task GoingAway(WebSocket socket)
    {
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, null, CancellationToken.None);
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
