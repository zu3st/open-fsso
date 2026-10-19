using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenFsso.Core;
using OpenFsso.Logging;

namespace OpenFsso.Client;

public sealed class StatusPipe(Ownership ownership, ClientStatusJournal journal, ILogger<StatusPipe> logger) : BackgroundService
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);

    // The tray status is optional. If another process holds the pipe name, the
    // service keeps sending presence and retries until the name is free.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool warned = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream reservation;
            try
            {
                reservation = Create(true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (!warned)
                {
                    logger.Write(LogLevel.Warning, "Status pipe is unavailable; the tray shows the service as unavailable until it is created", ("ErrorType", e.GetType().Name));
                    warned = true;
                }

                await Wait(stoppingToken);
                continue;
            }

            if (warned)
            {
                logger.LogInformation("Status pipe created");
            }

            using (reservation)
            {
                await Task.WhenAll(Serve(reservation, stoppingToken), Serve(null, stoppingToken));
            }

            return;
        }
    }

    private static async Task Wait(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(Retry, stopping);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Serve(NamedPipeServerStream? first, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = first ?? Create(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Wait(stopping);
                continue;
            }

            first = null;
            using var _ = pipe;
            try
            {
                await pipe.WaitForConnectionAsync(stopping);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var owner = ownership.Current;
                var status = journal.Read(owner.Revision, owner.Owner?.Name, DateTimeOffset.UtcNow);
                await JsonSerializer.SerializeAsync(pipe, status, StatusJson.Default.ClientStatus, deadline.Token);
                await pipe.FlushAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            { /* Disconnected/slow reader. */
            }
        }
    }

    private static NamedPipeServerStream Create(bool first)
    {
        // Read-only for interactive users (IU); SYSTEM and administrators keep full
        // control. Every remote client token carries NETWORK (NU), which is denied.
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm("D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;IU)");
        return NamedPipeServerStreamAcl.Create(ClientStatus.PipeName, PipeDirection.Out, 4, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 16384, 0, security);
    }
}
