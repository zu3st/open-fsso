using Microsoft.Extensions.Logging;

namespace OpenFsso.Logging;

public static class LogFields
{
    // Logs an event's fixed message. The values go to ECS fields or under openfsso.*, not into the message text.
    public static void Write(this ILogger logger, LoggedEvent logged, params (string Name, object? Value)[] fields) =>
        Write(logger, null, logged, fields);

    public static void Write(this ILogger logger, Exception? error, LoggedEvent logged, params (string Name, object? Value)[] fields)
    {
        if (!logger.IsEnabled(logged.Level))
        {
            return;
        }

        (string Name, object? Value)[] classification = [("Action", logged.Action), ("Category", logged.Category), ("Type", logged.Type), ("Outcome", logged.Outcome)];
        using (logger.BeginScope(classification.Concat(fields).Where(x => x.Value != null).Select(x => new KeyValuePair<string, object?>(x.Name, x.Value)).ToList()))
        {
            logger.Log(logged.Level, error, logged.Message);
        }
    }
}
