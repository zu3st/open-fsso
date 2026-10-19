using Microsoft.Extensions.Logging;

namespace OpenFsso.Logging;

public static class LogFields
{
    // Logs a fixed message. The values go to ECS fields or under openfsso.*, not into the message text.
    public static void Write(this ILogger logger, LogLevel level, string message, params (string Name, object? Value)[] fields) =>
        Write(logger, level, null, message, fields);

    public static void Write(this ILogger logger, LogLevel level, Exception? error, string message, params (string Name, object? Value)[] fields)
    {
        if (!logger.IsEnabled(level))
        {
            return;
        }

        using (logger.BeginScope(fields.Where(x => x.Value != null).Select(x => new KeyValuePair<string, object?>(x.Name, x.Value)).ToList()))
        {
            logger.Log(level, error, message);
        }
    }
}
