using System.Net;
using OpenFsso.Core;
using OpenFsso.Logging;

namespace OpenFsso.Receiver;

// Rate-limited, so anonymous requests cannot flood the log.
public sealed class RejectionLog(ILogger logger, TimeProvider clock)
{
    public const int Limit = 10;
    private readonly object gate = new();
    private long window = clock.GetTimestamp();
    private int logged, suppressed;

    public void SocketClaim(string? claimed, IPAddress source)
    {
        if (Allow())
        {
            // Only a parsed address reaches the log, never the client's raw text.
            logger.Write(LogEvents.IpClaimRejected, ("ClaimedIp", IPAddress.TryParse(claimed, out var ip) ? ip.ToString() : "invalid"), ("SourceIp", source.ToString()));
        }
    }

    public void Identity(string? authenticationType)
    {
        if (Allow())
        {
            logger.Write(LogEvents.IdentityRejected, ("AuthenticationType", authenticationType));
        }
    }

    // Authenticated users, so these are not rate limited.
    public void AccountRevoked(AuthenticatedUser user, string detail) =>
        logger.Write(LogEvents.AccountRevoked, ("UserName", user.Name), ("UserSid", user.Sid), ("ErrorCode", detail));

    public void AccountUnknown(AuthenticatedUser user, string detail) =>
        logger.Write(LogEvents.AccountUnknown, ("UserName", user.Name), ("UserSid", user.Sid), ("ErrorCode", detail));

    private bool Allow()
    {
        int dropped = 0;
        bool allowed;
        lock (gate)
        {
            if (clock.GetElapsedTime(window) >= TimeSpan.FromMinutes(1))
            {
                dropped = suppressed;
                window = clock.GetTimestamp();
                logged = suppressed = 0;
            }

            allowed = logged < Limit;
            if (allowed)
            {
                logged++;
            }
            else
            {
                suppressed++;
            }
        }

        if (dropped > 0)
        {
            logger.Write(LogEvents.RejectionsSuppressed, ("Suppressed", dropped));
        }

        return allowed;
    }
}
