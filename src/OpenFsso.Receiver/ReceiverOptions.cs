namespace OpenFsso.Receiver;

public sealed record ReceiverOptions
{
    public required string Host
    {
        get; init;
    }
    public int Port { get; init; } = 8443;
    public string QueueName { get; init; } = "OpenFssoReceiver";
    public string ServiceName { get; init; } = "OpenFssoReceiver";
    public const int LeaseSeconds = 180;
    public int ExpirySeconds { get; init; } = 5;
    public int ReassertSeconds { get; init; } = 3600;
    public int SyslogPort { get; init; } = 514;
    public string CollectorService { get; init; } = "Fortinet_FSAE";
    public string LoginTemplate { get; init; } = "<134>OpenFsso action=login user=\"{user}\" group=\"ignored-by-remote-LDAP\" ip={ip} ";
    public string LogoutTemplate { get; init; } = "<134>OpenFsso action=logout user=\"{user}\" group=\"ignored-by-remote-LDAP\" ip={ip} ";
    public string UpdateTemplate { get; init; } = "<134>OpenFsso action=update user=\"{user}\" group=\"ignored-by-remote-LDAP\" ip={ip} ";
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
    }
}
