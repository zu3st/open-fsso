using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using OpenFsso.Core;

namespace OpenFsso.Client;

public sealed class StatusPipe(Ownership ownership, ClientStatusJournal journal) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reserve the first instance before reporting service startup complete.
        using var reservation = Create(true);
        await Task.WhenAll(Serve(reservation, stoppingToken), Serve(null, stoppingToken));
    }

    private async Task Serve(NamedPipeServerStream? first, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            using var pipe = first ?? Create(false);
            first = null;
            try
            {
                await pipe.WaitForConnectionAsync(stopping);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var owner = ownership.Current;
                var status = journal.Read(owner.Revision, owner.Owner?.Name, DateTimeOffset.UtcNow);
                await JsonSerializer.SerializeAsync(pipe, status, cancellationToken: deadline.Token);
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
