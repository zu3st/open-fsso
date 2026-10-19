using System.ComponentModel;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenFsso.Core;
using OpenFsso.Logging;
using OpenFsso.Windows;

namespace OpenFsso.Client;

public sealed class ReceiverSender(ReceiverEndpoint endpoint, Ownership ownership, ClientStatusJournal status, FabricMonitor fabric, ILogger logger)
{
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly object gate = new();
    private CancellationTokenSource? active;
    private Guid? reference;
    private OwnerRecord? previousOwner;
    private string? previousIp;
    private bool failed;
    private DirectKerberosTransport? presence;
    private Guid presenceGeneration;
    private volatile bool reconnect;
    public void Wake()
    {
        reconnect = true; // Owner, fabric, network and resume changes all start a new connection.
        lock (gate)
        {
            active?.Cancel();
        }

        wake.Writer.TryWrite(true);
    }

    public async Task Run(CancellationToken stopping)
    {
        int failures = 0;
        while (!stopping.IsCancellationRequested)
        {
            while (wake.Reader.TryRead(out _))
            {
            }

            var state = ownership.Current;
            double delay = ClientOptions.RenewalSeconds * (0.9 + Random.Shared.NextDouble() * 0.2);
            var location = fabric.Check();
            if (!location.Allowed)
            {
                // Off-fabric sends no presence or release. The receiver's lease
                // expires normally; an on-fabric return reconciles current state.
                reference = null;
                previousOwner = null;
                await Wait(delay, stopping);
                continue;
            }

            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            lock (gate)
            {
                active = cycle;
            }

            try
            {
                var owner = state.Owner;
                string operation = "presence";
                if (owner == null)
                {
                    owner = previousOwner;
                    operation = "release";
                    previousOwner = null; // At most one best-effort removal; no old-event backlog.
                    if (owner == null || reference == null)
                    {
                        await Wait(delay, stopping);
                        continue;
                    }
                }

                using var token = operation == "presence" ? OpenToken(owner) : null;
                if (operation == "presence")
                {
                    previousOwner = owner;
                    status.Attempt(endpoint.Name, state.Revision, DateTimeOffset.UtcNow);
                }

                if (reconnect || operation == "release" || presenceGeneration != owner.Generation || presence?.UsesCurrentRoute() == false)
                {
                    Disconnect();
                }

                reconnect = false;
                // Release uses its own connection without a Kerberos proof.
                using var release = operation == "release" ? new DirectKerberosTransport(endpoint, package: null) : null;
                if (release == null)
                {
                    presence ??= new DirectKerberosTransport(endpoint)
                    {
                        SocketConnected = ip => status.Socket(endpoint.Name, ownership.Current.Revision, ip),
                        Closed = Wake
                    };
                }
                presenceGeneration = owner.Generation;
                if (release == null)
                {
                    // Shown before connecting, so a failing attempt still names its address.
                    status.Socket(endpoint.Name, state.Revision, presence!.RouteSource());
                }

                PresenceRequest Body(string ip)
                {
                    if (ownership.Current.Revision != state.Revision || !fabric.IsCurrent(location))
                    {
                        throw new OperationCanceledException();
                    }

                    return new PresenceRequest(1, owner.Generation, ip, reference);
                }

                var reply = release == null
                    ? await presence!.SendAsync(token, Body, cycle.Token)
                    : await release.PostAsync(null, "release", Body, cycle.Token);
                cycle.Token.ThrowIfCancellationRequested();
                if (!fabric.IsCurrent(location))
                {
                    continue;
                }
                if (reply.Status != 200 && !(reply.Status == 409 && operation == "presence"))
                {
                    throw new ReceiverRejectedException(reply.Status);
                }

                var result = JsonSerializer.Deserialize(reply.Body, WireJson.Default.PresenceReply);
                if (result == null)
                {
                    throw new IOException("Receiver response was empty.");
                }

                if (reply.Status == 409 && operation == "presence")
                {
                    reference = result.LeaseReference;
                    delay = 0.1; // Next iteration re-reads the latest Windows state and opens a fresh socket.
                }
                else if (reply.Status == 200)
                {
                    reference = result.LeaseReference;
                    if (operation == "presence")
                    {
                        status.Sent(endpoint.Name, state.Revision, reply.LocalIp, DateTimeOffset.UtcNow);
                    }

                    if (previousIp != reply.LocalIp)
                    {
                        logger.Write(LogLevel.Information, "Receiver path uses a new socket IP", ("Receiver", endpoint.Name), ("SourceIp", reply.LocalIp));
                        previousIp = reply.LocalIp;
                    }

                    if (failed)
                    {
                        logger.Write(LogLevel.Information, "Receiver recovered", ("Receiver", endpoint.Name));
                    }

                    failed = false;
                    failures = 0;
                }
                else
                {
                    throw new ReceiverRejectedException(reply.Status);
                }
            }
            // A wake or a new owner ends the send; the next cycle starts fresh.
            catch (OperationCanceledException) when (cycle.IsCancellationRequested || ownership.Current.Revision != state.Revision)
            {
                delay = 0.1;
            }
            catch (OwnerUnavailableException e)
            {
                if (state.Owner != null && ownership.Current == state)
                {
                    ownership.Logoff(state.Owner.SessionId);
                }

                if (!failed)
                {
                    logger.Write(LogLevel.Warning, "Selected user logon is unavailable", ("Receiver", endpoint.Name), ("ErrorType", e.InnerException!.GetType().Name));
                }

                failed = true;
                delay = 1;
                Disconnect();
            }
            catch (Exception e) when (e is HttpRequestException or IOException or SocketException or WebSocketException or AuthenticationException or JsonException or OperationCanceledException or InvalidOperationException)
            {
                Disconnect(); // Includes a 401 after the receiver forgot this connection.
                failures = Math.Min(failures + 1, 6);
                delay = Math.Min(30, Math.Pow(2, failures - 1) * (0.8 + Random.Shared.NextDouble() * 0.4));
                if (!failed)
                {
                    logger.Write(LogLevel.Warning, "Receiver delivery failed", ("Receiver", endpoint.Name), ("ErrorType", e.GetType().Name), ("HttpStatus", (e as ReceiverRejectedException)?.Status));
                }

                failed = true;
                if (state.Owner != null)
                {
                    status.Failed(endpoint.Name, state.Revision, DeliveryError.Describe(e));
                }
            }
            finally
            {
                lock (gate)
                {
                    active = null;
                }
            }

            await Wait(delay, stopping);
        }

        Disconnect();
    }

    private void Disconnect()
    {
        presence?.Dispose();
        presence = null;
    }

    private Microsoft.Win32.SafeHandles.SafeAccessTokenHandle OpenToken(OwnerRecord owner)
    {
        try
        {
            return ownership.Open(owner);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            throw new OwnerUnavailableException(e);
        }
    }

    private sealed class OwnerUnavailableException(Exception inner) : Exception(null, inner)
    {
    }

    private async Task Wait(double seconds, CancellationToken stopping)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        if (!double.IsPositiveInfinity(seconds))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        try
        {
            await wake.Reader.ReadAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
        }
    }
}
