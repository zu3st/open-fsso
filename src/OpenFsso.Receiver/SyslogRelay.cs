using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenFsso.Core;
using OpenFsso.Logging;

namespace OpenFsso.Receiver;

public sealed class SyslogRelay(ReceiverOptions options, ILogger<SyslogRelay> logger) : IDisposable
{
    private readonly UdpClient socket = new(new IPEndPoint(IPAddress.Parse(options.SyslogSourceAddress), 0));
    public DeliveryRecovery Recovery { get; } = new();
    public bool Healthy => Recovery.Healthy;

    public async Task Emit(MappingChange change)
    {
        try
        {
            string format = change.Action switch
            {
                "login" => options.LoginTemplate,
                "update" => options.UpdateTemplate,
                _ => options.LogoutTemplate
            };
            string line = SyslogFormat.Render(format, change);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.SendAsync(Encoding.UTF8.GetBytes(line), new IPEndPoint(IPAddress.Loopback, options.SyslogPort), deadline.Token);
            logger.Write(LogEvents.MappingSent(change.Action), ("UserName", change.User.Name), ("UserSid", change.User.Sid), ("SourceIp", change.Ip), ("Reason", change.Reason), ("Generation", change.Generation));
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
        {
            Recovery.Failed();
            logger.Write(LogEvents.MappingFailed(change.Action), ("UserName", change.User.Name), ("UserSid", change.User.Sid), ("SourceIp", change.Ip), ("Reason", change.Reason), ("Generation", change.Generation), ("ErrorType", e.GetType().Name));
        }
    }

    public void Dispose() => socket.Dispose();
}
