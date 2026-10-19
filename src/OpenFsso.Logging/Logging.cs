using System.Text.Encodings.Web;
using System.Text.Json;
using Elastic.CommonSchema;
using Elastic.CommonSchema.Serilog;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;

namespace OpenFsso.Logging;

public static class Logging
{
    public static Serilog.Core.Logger Create(string directory, string service, long limit, params string[] secrets) =>
        new LoggerConfiguration().MinimumLevel.Information().MinimumLevel.Override("Microsoft", LogEventLevel.Warning).Enrich.FromLogContext()
            .WriteTo.File(Formatter(service, secrets), Path.Combine(directory, "events-.jsonl"), rollingInterval: RollingInterval.Day, fileSizeLimitBytes: limit, rollOnFileSizeLimit: true, retainedFileCountLimit: 14, buffered: false)
            .CreateLogger();

    public static ITextFormatter Formatter(string service, params string[] secrets) => new MaskingFormatter(new EcsTextFormatter(new EcsTextFormatterConfiguration
    {
        MapCustom = (document, e) =>
        {
            document.Service = new Service { Name = service };
            if (e.Properties.TryGetValue("UserSid", out var sid))
            {
                document.User = new User { Id = Text(sid), Name = e.Properties.TryGetValue("UserName", out var name) ? Text(name) : null };
            }

            if (e.Properties.TryGetValue("SourceIp", out var ip))
            {
                document.Source = new Source { Ip = Text(ip) };
            }

            return document;
        }
    }), secrets);

    private static string? Text(LogEventPropertyValue value) => value is ScalarValue scalar ? scalar.Value?.ToString() : value.ToString();
}

internal sealed class MaskingFormatter(ITextFormatter inner, string[] secrets) : ITextFormatter
{
    private readonly string[] forms = secrets.Where(x => !string.IsNullOrEmpty(x))
        .SelectMany(x => new[] { x, JsonEncodedText.Encode(x).ToString(), JsonEncodedText.Encode(x, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString() })
        .Distinct().OrderByDescending(x => x.Length).ToArray();

    public void Format(LogEvent logEvent, TextWriter output)
    {
        if (forms.Length == 0)
        {
            inner.Format(logEvent, output);
            return;
        }

        using var buffer = new StringWriter();
        inner.Format(logEvent, buffer);
        string text = buffer.ToString();
        foreach (string secret in forms)
        {
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        output.Write(text);
    }
}
