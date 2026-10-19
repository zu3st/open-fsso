using System.Net;

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
            logger.LogWarning("Rejected socket IP claim {ClaimedIp} from {SourceIp}", IPAddress.TryParse(claimed, out var ip) ? ip.ToString() : "invalid", source.ToString());
        }
    }

    public void Identity(string? authenticationType)
    {
        if (Allow())
        {
            logger.LogWarning("Rejected authenticated identity type {AuthenticationType}", authenticationType);
        }
    }

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
            logger.LogWarning("Suppressed {Count} further rejection warnings in the previous minute", dropped);
        }

        return allowed;
    }
}
