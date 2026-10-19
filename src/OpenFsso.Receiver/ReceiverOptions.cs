using System.Net;
using System.Net.Sockets;

namespace OpenFsso.Receiver;

// Settings with defaults use setters. Compile-time JSON assigns every init-only
// property, so an omitted one would lose its default.
public sealed record ReceiverOptions
{
    public required string Host
    {
        get; init;
    }
    public int Port { get; set; } = 8443;
    public string QueueName { get; set; } = "OpenFssoReceiver";
    public string ServiceName { get; set; } = "OpenFssoReceiver";
    public const int LeaseSeconds = 180;
    public int ExpirySeconds { get; set; } = 5;
    public int ReassertSeconds { get; set; } = 3600;
    public int SyslogPort { get; set; } = 514;
    // The collector picks its matching rule by sender address. Another feed
    // already sending from 127.0.0.1 needs a different loopback address here.
    public string SyslogSourceAddress { get; set; } = "127.0.0.1";
    public string CollectorService { get; set; } = "Fortinet_FSAE";
    public string LoginTemplate { get; set; } = "<134>OpenFsso action=login user=\"{user}\" group=\"ignored-by-remote-LDAP\" ip={ip} ";
    public string LogoutTemplate { get; set; } = "<134>OpenFsso action=logout user=\"{user}\" group=\"ignored-by-remote-LDAP\" ip={ip} ";
    public string UpdateTemplate { get; set; } = "<134>OpenFsso action=update user=\"{user}\" group=\"ignored-by-remote-LDAP\" ip={ip} ";
    public required string LogDirectory
    {
        get; init;
    }
    public required string HealthApiKey
    {
        get; init;
    }

    // The embedded JSON Schema checks fields; path rules depend on the OS.
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(LogDirectory))
        {
            throw new InvalidDataException("An absolute protected log directory is required.");
        }

        if (!IPAddress.TryParse(SyslogSourceAddress, out var source) || source.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(source))
        {
            throw new InvalidDataException("SyslogSourceAddress must be an IPv4 loopback address.");
        }
    }
}
