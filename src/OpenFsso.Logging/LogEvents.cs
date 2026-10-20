using Microsoft.Extensions.Logging;

namespace OpenFsso.Logging;

// One logged event: its level, fixed message and ECS classification.
// Category, Type and Outcome hold ECS allowed values for event.category, event.type and event.outcome.
public sealed record LoggedEvent(LogLevel Level, string Message, string Action, string? Category = null, string? Type = null, string? Outcome = null);

public static class LogEvents
{
    public static readonly LoggedEvent ReceiverReady = new(LogLevel.Information, "Receiver ready; Kerberos presence authentication and strict channel binding verified; Bearer health authentication enabled", "receiver-ready", "process", "start", "success");
    public static readonly LoggedEvent ConnectionTimingUnavailable = new(LogLevel.Error, "Native HTTP.sys connection timing is unavailable; requests fail closed", "connection-timing-unavailable", "web", "error", "failure");
    public static readonly LoggedEvent CollectorChanged = new(LogLevel.Warning, "Collector availability changed", "collector-availability-changed", "process", "change");
    public static readonly LoggedEvent IpClaimRejected = new(LogLevel.Warning, "Rejected socket IP claim", "ip-claim-rejected", "network", "denied", "failure");
    public static readonly LoggedEvent IdentityRejected = new(LogLevel.Warning, "Rejected authenticated identity", "identity-rejected", "authentication", "info", "failure");
    public static readonly LoggedEvent AccountRevoked = new(LogLevel.Warning, "Account revoked by the domain; mappings removed", "account-revoked", "authentication", "info", "failure");
    public static readonly LoggedEvent AccountUnknown = new(LogLevel.Warning, "Account status check failed; mapping allowed", "account-check-failed", "authentication", "info", "unknown");
    public static readonly LoggedEvent RejectionsSuppressed = new(LogLevel.Warning, "Suppressed further rejection warnings in the previous minute", "rejection-warnings-suppressed");

    public static readonly LoggedEvent ClientReady = new(LogLevel.Information, "Client service ready", "client-ready", "process", "start", "success");
    public static readonly LoggedEvent StatusPipeCreated = new(LogLevel.Information, "Status pipe created", "status-pipe-created", "process", "info", "success");
    public static readonly LoggedEvent StatusPipeUnavailable = new(LogLevel.Warning, "Status pipe is unavailable; the tray shows the service as unavailable until it is created", "status-pipe-unavailable", "process", "info", "failure");
    public static readonly LoggedEvent TrayStarted = new(LogLevel.Information, "Tray launch requested", "tray-started", "process", "start", "success");
    public static readonly LoggedEvent TrayFailed = new(LogLevel.Warning, "Could not start tray", "tray-start-failed", "process", "start", "failure");
    public static readonly LoggedEvent SessionsUnavailable = new(LogLevel.Warning, "Could not enumerate user sessions for tray startup", "session-enumeration-failed", "session", "info", "failure");
    public static readonly LoggedEvent FabricChanged = new(LogLevel.Information, "Fabric status changed", "fabric-status-changed", "network", "info");
    public static readonly LoggedEvent AddressChanged = new(LogLevel.Information, "FSSO address changed", "address-changed", "network", "info");
    public static readonly LoggedEvent ReceiverRecovered = new(LogLevel.Information, "Receiver recovered", "receiver-recovered", "network", "connection", "success");
    public static readonly LoggedEvent DeliveryFailed = new(LogLevel.Warning, "Receiver delivery failed", "delivery-failed", "network", "connection", "failure");
    public static readonly LoggedEvent LogonUnavailable = new(LogLevel.Warning, "Selected user logon is unavailable", "logon-unavailable", "authentication", "info", "failure");
    public static readonly LoggedEvent WaitingForLogon = new(LogLevel.Information, "Waiting for an interactive logon or unlock", "waiting-for-logon", "session", "info");
    public static readonly LoggedEvent OwnerRestored = new(LogLevel.Information, "Selected interactive owner at service start", "owner-selected", "session", "start", "success");
    public static readonly LoggedEvent OwnerSelected = new(LogLevel.Information, "Selected interactive owner", "owner-selected", "session", "start", "success");
    public static readonly LoggedEvent OwnerRestoreFailed = new(LogLevel.Error, "Owner selection at service start failed closed", "owner-selection-failed", "session", "info", "failure");
    public static readonly LoggedEvent ProtocolUnknown = new(LogLevel.Warning, "Cannot validate session protocol", "session-protocol-unknown", "session", "info", "failure");
    public static readonly LoggedEvent OwnerNotSaved = new(LogLevel.Error, "Owner state could not be saved; renewals stopped", "owner-state-save-failed", "file", "change", "failure");
    public static readonly LoggedEvent TokenUnavailable = new(LogLevel.Warning, "Interactive session token is unavailable", "session-token-unavailable", "session", "info", "failure");
    public static readonly LoggedEvent OwnerLoggedOff = new(LogLevel.Information, "Owner logged off", "owner-logged-off", "session", "end", "success");

    // action is login, update or logout, the mapping's syslog action.
    public static LoggedEvent MappingSent(string action) => new(LogLevel.Information, "Mapping sent to the collector", action, "session", SessionType(action), "success");

    public static LoggedEvent MappingFailed(string action) => new(LogLevel.Error, "Local syslog delivery failed", action, "session", SessionType(action), "failure");

    private static string SessionType(string action) => action switch
    {
        "login" => "start",
        "logout" => "end",
        _ => "info"
    };
}
