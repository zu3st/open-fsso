using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace OpenFsso.Client;

internal sealed class ReceiverRejectedException(int status) : IOException
{
    public int Status { get; } = status;
}

public static class DeliveryError
{
    public static string Describe(Exception error)
    {
        if (error is ReceiverRejectedException rejected)
        {
            return rejected.Status switch
            {
                401 => "Kerberos authentication rejected (401)",
                403 => "Access denied (403)",
                400 => "Presence request rejected (400)",
                _ => $"Receiver returned HTTP {rejected.Status}"
            };
        }

        if (error is OperationCanceledException)
        {
            return "Request timed out";
        }

        // The innermost known cause is the most specific one.
        var chain = new List<Exception>();
        for (Exception? inner = error; inner != null; inner = inner.InnerException)
        {
            chain.Add(inner);
        }

        chain.Reverse();
        foreach (var inner in chain)
        {
            if (inner is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "Connection refused",
                    SocketError.TimedOut => "Connection timed out",
                    SocketError.NetworkUnreachable => "Network unreachable",
                    SocketError.HostUnreachable => "Receiver unreachable",
                    _ => "Network connection failed"
                };
            }

            if (inner is AuthenticationException)
            {
                return "TLS or Kerberos authentication failed";
            }

            if (inner is TimeoutException or OperationCanceledException)
            {
                return "Connection timed out";
            }

            if (inner is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError })
            {
                return "Could not connect to the receiver";
            }
        }

        return error is JsonException ? "Invalid receiver response" : "Receiver communication failed";
    }
}
