using System.ComponentModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenFsso.Logging;
using OpenFsso.Windows;

namespace OpenFsso.Client;

// A normal service start also covers MSI install, upgrade and repair, including
// SYSTEM deployments. Future user logons retain the machine-wide Run entry.
public sealed class TrayStartup(ILogger<TrayStartup> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "OpenFsso.Tray.exe");
        if (!File.Exists(executable))
        {
            return Task.CompletedTask;
        }

        try
        {
            foreach (var session in Sessions.Eligible(allowRdp: true))
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    uint pid = SessionProcess.Start(session.SessionId, executable);
                    logger.Write(LogLevel.Information, "Tray launch requested", ("SessionId", session.SessionId), ("TrayPid", pid));
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                {
                    // A session can disappear while installation is finishing.
                    // Tray availability must not prevent authentication service startup.
                    logger.Write(LogLevel.Warning, error, "Could not start tray", ("SessionId", session.SessionId));
                }
            }
        }
        catch (Win32Exception error)
        {
            logger.LogWarning(error, "Could not enumerate user sessions for tray startup");
        }

        return Task.CompletedTask;
    }
}
