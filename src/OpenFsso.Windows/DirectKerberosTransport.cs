using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Authentication.ExtendedProtection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using OpenFsso.Core;

namespace OpenFsso.Windows;

public sealed record TransportReply(int Status, string Body, string LocalIp);

// One presence session (a WebSocket) to one receiver. Kerberos runs once, on the
// upgrade request, bound to the TLS channel; later messages carry no new proof.
// Dispose to force a new session.
public sealed class DirectKerberosTransport : IDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15);
    private readonly ReceiverEndpoint endpoint;
    private readonly IPAddress address;
    private readonly string? package;
    private readonly SocketsHttpHandler handler;
    private readonly HttpClient client;
    private SafeAccessTokenHandle? token;
    private volatile string? localIp;
    private IPAddress? source;
    private ClientWebSocket? socket;
    private Channel<byte[]>? replies;
    private volatile bool disposed;

    // Raised when an open session ends without Dispose, such as a missed pong.
    public Action? Closed
    {
        get; init;
    }

    // package null sends no Kerberos proof, for release by lease reference.
    public DirectKerberosTransport(ReceiverEndpoint endpoint, string? package = "Kerberos")
    {
        if (Uri.CheckHostName(endpoint.Host) != UriHostNameType.Dns || endpoint.Host.Contains('*') || endpoint.Port is < 1 or > 65535)
        {
            throw new ArgumentException("An explicit receiver TLS hostname, address and port are required.");
        }

        this.endpoint = endpoint;
        address = IPAddress.Parse(endpoint.Address);
        this.package = package;
        handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            Credentials = null,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            MaxConnectionsPerServer = 1,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = endpoint.CheckRevocation ? System.Security.Cryptography.X509Certificates.X509RevocationMode.Online : System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            },
            ConnectCallback = Connect,
            PlaintextStreamFilter = Authenticate
        };
        client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 16384
        };
    }

    // True while there is no session or it uses the given local address.
    public bool ConnectedFrom(string address) => localIp is null || localIp == address;

    // Presence over the session, opening it first when needed.
    public async Task<TransportReply> SendAsync(SafeAccessTokenHandle? user, IPAddress source, Func<string, PresenceRequest> body, CancellationToken cancellationToken)
    {
        this.source = source;
        if (socket is not { State: WebSocketState.Open })
        {
            int status = await Open(user, cancellationToken);
            if (status != 101)
            {
                return new TransportReply(status, "", localIp ?? "");
            }
        }

        string ip = localIp ?? throw new IOException("The socket address was lost.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReplyTimeout);
        await socket!.SendAsync(JsonSerializer.SerializeToUtf8Bytes(body(ip), WireJson.Default.PresenceRequest), WebSocketMessageType.Text, true, deadline.Token);
        var reply = JsonSerializer.Deserialize(await replies!.Reader.ReadAsync(deadline.Token), WireJson.Default.SessionReply) ?? throw new IOException("Receiver reply was empty.");
        return new TransportReply(reply.Status, JsonSerializer.Serialize(reply.Reply, WireJson.Default.PresenceReply), ip);
    }

    // One HTTP request on its own connection, used for release.
    public async Task<TransportReply> PostAsync(SafeAccessTokenHandle? user, string operation, IPAddress source, Func<string, PresenceRequest> body, CancellationToken cancellationToken)
    {
        this.source = source;
        RequireUser(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, new UriBuilder("https", endpoint.Host, endpoint.Port, "/v1/" + operation).Uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new SocketContent(() => localIp is { } ip ? JsonSerializer.SerializeToUtf8Bytes(body(ip), WireJson.Default.PresenceRequest) : throw new IOException("No socket is available for serialization."))
        };
        request.Headers.Host = new UriBuilder("https", endpoint.Host, endpoint.Port).Uri.Authority;
        request.Headers.ConnectionClose = true;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        return new TransportReply((int)response.StatusCode, text, localIp ?? throw new IOException("The socket address was lost."));
    }

    private void RequireUser(SafeAccessTokenHandle? user)
    {
        if (user != null)
        {
            SessionToken.RequireUser(user);
        }

        token = user;
    }

    // The upgrade's HTTP status: 101 when the session is open.
    private async Task<int> Open(SafeAccessTokenHandle? user, CancellationToken cancellationToken)
    {
        RequireUser(user);
        var next = new ClientWebSocket();
        next.Options.HttpVersion = HttpVersion.Version11;
        next.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        next.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        next.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        next.Options.CollectHttpResponseDetails = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReplyTimeout);
        try
        {
            await next.ConnectAsync(new UriBuilder("wss", endpoint.Host, endpoint.Port, "/v1/session").Uri, new HttpMessageInvoker(handler, disposeHandler: false), deadline.Token);
        }
        catch (WebSocketException) when (next.HttpStatusCode != 0)
        {
            int status = (int)next.HttpStatusCode;
            next.Dispose();
            return status;
        }
        catch
        {
            next.Dispose();
            throw;
        }

        var inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        socket = next;
        replies = inbox;
        _ = Read(next, inbox);
        return 101;
    }

    // Keeps a receive pending so pings are answered and a dead session is noticed.
    private async Task Read(ClientWebSocket session, Channel<byte[]> inbox)
    {
        var buffer = new byte[16384];
        try
        {
            while (true)
            {
                int length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await session.ReceiveAsync(buffer.AsMemory(length), CancellationToken.None);
                    length += result.Count;
                }
                while (!result.EndOfMessage && length < buffer.Length);

                if (result.MessageType == WebSocketMessageType.Close || !result.EndOfMessage)
                {
                    break;
                }

                inbox.Writer.TryWrite(buffer[..length]);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            // Wake the sender first, so a send waiting for its reply ends as a
            // reconnect rather than a delivery failure.
            if (!disposed && socket == session)
            {
                Closed?.Invoke();
            }

            inbox.Writer.TryComplete(new IOException("The presence session closed."));
        }
    }

    private async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        if (!string.Equals(context.DnsEndPoint.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != endpoint.Port)
        {
            throw new IOException("Unexpected transport destination.");
        }

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            if (source != null)
            {
                socket.Bind(new IPEndPoint(source, 0));
            }

            await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
            localIp = ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // Runs once per new connection, for the request that opened it.
    private async ValueTask<Stream> Authenticate(SocketsHttpPlaintextStreamFilterContext context, CancellationToken ct)
    {
        if (context.PlaintextStream is not SslStream tls)
        {
            throw new IOException("Cannot prove the TLS/socket relationship.");
        }

        if (package == null)
        {
            return context.PlaintextStream;
        }

        using var binding = tls.TransportContext?.GetChannelBinding(ChannelBindingKind.Endpoint) ?? throw new AuthenticationException("TLS endpoint channel binding is unavailable.");
        string Proof()
        {
            using var auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions { Package = package, Credential = CredentialCache.DefaultNetworkCredentials, TargetName = "HTTP/" + endpoint.Host, Binding = binding, RequiredProtectionLevel = ProtectionLevel.None, RequireMutualAuthentication = false });
            string? blob = auth.GetOutgoingBlob((string?)null, out var status);
            if (blob == null || status is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded))
            {
                throw new AuthenticationException("Kerberos token acquisition failed: " + status);
            }

            return blob;
        }

        // Only credential acquisition runs as the selected user.
        string blob = token == null ? Proof() : await WindowsIdentity.RunImpersonatedAsync(token, () => Task.FromResult(Proof()));
        ct.ThrowIfCancellationRequested();
        context.InitialRequestMessage.Headers.Authorization = new AuthenticationHeaderValue(package, blob);
        return context.PlaintextStream;
    }

    public void Dispose()
    {
        disposed = true;
        socket?.Abort();
        socket?.Dispose();
        client.Dispose();
        handler.Dispose();
    }

    private sealed class SocketContent : HttpContent
    {
        private readonly Func<byte[]> serialize;
        public SocketContent(Func<byte[]> serialize)
        {
            this.serialize = serialize;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(serialize()).AsTask();
    }
}
