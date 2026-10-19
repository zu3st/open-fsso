using System.Diagnostics.CodeAnalysis;

namespace OpenFsso.Core;

public sealed record ReceiverStatus(string Name, string? Ip = null, DateTimeOffset? LastAttemptUtc = null, DateTimeOffset? LastSentUtc = null, string? Error = null, bool Pending = false);
public sealed record ClientStatus(int SchemaVersion, DateTimeOffset ObservedUtc, string? User, ReceiverStatus[] Receivers, FabricObservation? Fabric = null)
{
    public const string PipeName = "OpenFsso.Client.Status.v1";
    public const int MaxReceivers = 8;

    public static bool ValidReceiverCount([NotNullWhen(true)] ReceiverStatus[]? receivers) => receivers?.Length is >= 1 and <= MaxReceivers;
}

public sealed class ClientStatusJournal(IEnumerable<string> names)
{
    private readonly object gate = new();
    private FabricObservation? fabric;
    private readonly Dictionary<string, (long Revision, ReceiverStatus Status)> entries = names.ToDictionary(x => x, x => (-1L, new ReceiverStatus(x)));
    public void Attempt(string name, long revision, DateTimeOffset now) => Change(name, revision, s => s with { LastAttemptUtc = now, Pending = true });
    public void Sent(string name, long revision, string ip, DateTimeOffset now) => Change(name, revision, s => s with { Ip = ip, LastSentUtc = now, Error = null, Pending = false });
    public void Socket(string name, long revision, string ip) => Change(name, revision, s => s with { Ip = ip });
    public void Failed(string name, long revision, string reason) => Change(name, revision, s => s with { Error = reason, Pending = false });
    public void Fabric(FabricObservation observation)
    {
        lock (gate)
        {
            if (fabric?.State != observation.State)
            {
                foreach (string name in entries.Keys.ToArray())
                {
                    entries[name] = (-1, new(name));
                }
            }

            fabric = observation;
        }
    }
    private void Change(string name, long revision, Func<ReceiverStatus, ReceiverStatus> change)
    {
        lock (gate)
        {
            if (fabric?.State is FabricState.OffFabric or FabricState.Unavailable)
            {
                return;
            }

            var old = entries[name];
            if (revision < old.Revision)
            {
                return; // An old in-flight send cannot replace a newer owner.
            }

            entries[name] = (revision, change(old.Revision == revision ? old.Status : new(name)));
        }
    }

    public ClientStatus Read(long revision, string? user, DateTimeOffset now)
    {
        lock (gate)
        {
            return new(1, now, user, entries.Select(x => user != null && x.Value.Revision == revision ? x.Value.Status : new ReceiverStatus(x.Key)).ToArray(), fabric);
        }
    }
}

public enum ClientIndicator
{
    Idle,
    Working,
    Connected,
    Error,
    OffFabric
}

public sealed record ClientStatusView(ClientIndicator Indicator, string Title, string Text)
{
    public static ClientStatusView Create(ClientStatus? status, DateTimeOffset now)
    {
        if (status == null || status.SchemaVersion != 1 || !ClientStatus.ValidReceiverCount(status.Receivers) || now - status.ObservedUtc > TimeSpan.FromSeconds(10) || status.ObservedUtc - now > TimeSpan.FromSeconds(10))
        {
            return new(ClientIndicator.Error, "OpenFSSO - Service unavailable", "Client service stopped, restarting or unreachable.");
        }

        if (status.Fabric is { State: not FabricState.Disabled } fabric)
        {
            if (!Enum.IsDefined(fabric.State) || fabric.State == FabricState.Unavailable || now - fabric.ObservedUtc > TimeSpan.FromSeconds(120) || fabric.ObservedUtc > now)
            {
                return new(ClientIndicator.Error, "OpenFSSO - Network check unavailable", "Updates paused until the network can be checked.");
            }

            if (fabric.State == FabricState.OffFabric)
            {
                string user = status.User == null ? "" : $"User: {Clean(status.User)}\n";
                return new(ClientIndicator.OffFabric, "OpenFSSO - Off fabric", user + "Outside configured network. Updates paused.");
            }
        }

        bool Fresh(ReceiverStatus s) => s.LastSentUtc is { } sent && now - sent <= TimeSpan.FromSeconds(120) && sent <= now;
        int good = status.Receivers.Count(x => Fresh(x) && x.Error == null);
        var indicator = status.User == null ? ClientIndicator.Idle : good == status.Receivers.Length ? ClientIndicator.Connected : status.Receivers.All(x => x.Error != null || (x.LastSentUtc != null && !Fresh(x))) ? ClientIndicator.Error : ClientIndicator.Working;
        string title = indicator switch
        {
            ClientIndicator.Idle => "Waiting for logon or unlock",
            ClientIndicator.Connected => "Connected",
            ClientIndicator.Error => "Delivery failed",
            _ => good > 0 ? "Partially connected" : "Connecting"
        };
        if (status.User == null)
        {
            return new(indicator, "OpenFSSO - " + title, "No selected user. Waiting for logon or unlock.");
        }

        string text = $"User: {Clean(status.User)}";
        string[] ips = status.Receivers.Select(x => x.Ip ?? "—").Distinct().ToArray();
        text += "\nIP: " + (ips.Length == 1 ? Clean(ips[0]) : string.Join(" · ", status.Receivers.Select(s => $"{Clean(s.Name)} {Clean(s.Ip ?? "—")}")));
        var latest = status.Receivers.Max(s => s.LastSentUtc);
        text += "\nLast sent: " + (latest is { } sent ? Ago(now - sent) : "never");
        foreach (var s in status.Receivers)
        {
            if (s.Error != null)
            {
                text += $"\n{Clean(s.Name)}: {Clean(s.Error)}";
            }
            else if (s.LastSentUtc != null && !Fresh(s))
            {
                text += $"\n{Clean(s.Name)}: last acknowledgement is stale";
            }
        }

        return new(indicator, "OpenFSSO - " + title, text);
    }

    private static string Clean(string text) => new(text.Take(160).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    private static string Ago(TimeSpan age)
    {
        if (age.TotalSeconds < 1)
        {
            return "just now";
        }

        (int count, string unit) = age.TotalDays >= 1 ? ((int)age.TotalDays, "day") : age.TotalHours >= 1 ? ((int)age.TotalHours, "hour") : age.TotalMinutes >= 1 ? ((int)age.TotalMinutes, "minute") : ((int)age.TotalSeconds, "second");
        return $"{count} {unit}{(count == 1 ? "" : "s")} ago";
    }
}
