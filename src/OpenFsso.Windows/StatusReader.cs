using System.ComponentModel;
using System.IO.Pipes;
using System.Text.Json;
using OpenFsso.Core;
using Windows.Win32;

namespace OpenFsso.Windows;

public static class StatusReader
{
    public static async Task<ClientStatus?> Read(CancellationToken stopping)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            using var pipe = new NamedPipeClientStream(".", ClientStatus.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);
            // The service name is fixed. Verify its running SCM PID before trusting
            // a snapshot: a user-created pipe with the same name is not authoritative.
            if (!PInvoke.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid == 0 || ServiceProcess.RunningPid("OpenFssoClient") != pid)
            {
                return null;
            }

            using var bytes = new MemoryStream();
            byte[] buffer = new byte[2048];
            int count;
            while ((count = await pipe.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (bytes.Length + count > 16384)
                {
                    return null;
                }

                bytes.Write(buffer, 0, count);
            }

            if (ServiceProcess.RunningPid("OpenFssoClient") != pid)
            {
                return null;
            }

            var status = JsonSerializer.Deserialize(bytes.ToArray(), StatusJson.Default.ClientStatus);
            if (status?.SchemaVersion != 1 || !ClientStatus.ValidReceiverCount(status.Receivers) || status.Receivers.Any(x => x == null || x.Name == null))
            {
                return null;
            }

            return status;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException or Win32Exception)
        {
            return null;
        }
    }
}
