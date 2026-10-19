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
    private static readonly string? Version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);

    public static Serilog.Core.Logger Create(string directory, string service, long limit, params string[] secrets) =>
        new LoggerConfiguration().MinimumLevel.Information().MinimumLevel.Override("Microsoft", LogEventLevel.Warning).Enrich.FromLogContext()
            .WriteTo.File(Formatter(service, secrets), Path.Combine(directory, "events-.jsonl"), rollingInterval: RollingInterval.Day, fileSizeLimitBytes: limit, rollOnFileSizeLimit: true, retainedFileCountLimit: 14, buffered: false)
            .CreateLogger();

    // Properties with an ECS field go there; every other one goes under openfsso.*.
    private static readonly HashSet<string> Mapped = ["UserSid", "UserName", "SourceIp", "ErrorType", "ErrorCode", "Action", "Reason", "HttpStatus", "MessageTemplate"];

    public static ITextFormatter Formatter(string service, params string[] secrets) => new MaskingFormatter(new NamespaceFormatter(new EcsTextFormatter(new EcsTextFormatterConfiguration
    {
        MapCustom = (document, e) =>
        {
            // The application's version replaces the formatter's own agent block.
            // event.created and event.timezone only repeat @timestamp.
            document.Agent = null;
            document.Service = new Service { Name = service, Version = Version };
            document.Event ??= new Event();
            document.Event.Created = null;
            document.Event.Timezone = null;
            document.Event.Action = Value(e, "Action");
            document.Event.Reason = Value(e, "Reason");

            // user describes the user the event is about, never the service account.
            document.User = null;
            if (Value(e, "UserSid") is { } sid)
            {
                string? account = Value(e, "UserName");
                int slash = account?.IndexOf('\\') ?? -1;
                document.User = new User { Id = sid, Name = slash < 0 ? account : account![(slash + 1)..], Domain = slash < 0 ? null : account![..slash] };
            }

            if (Value(e, "SourceIp") is { } ip)
            {
                document.Source = new Source { Ip = ip };
            }

            if (Value(e, "ErrorType") is { } type)
            {
                document.Error ??= new Error();
                document.Error.Type = type;
            }

            if (Value(e, "ErrorCode") is { } code)
            {
                document.Error ??= new Error();
                document.Error.Code = code;
            }

            if (e.Properties.TryGetValue("HttpStatus", out var status) && status is ScalarValue { Value: int statusCode })
            {
                document.Http = new Http { ResponseStatusCode = statusCode };
            }

            var custom = new MetadataDictionary();
            foreach (var property in e.Properties.Where(x => !Mapped.Contains(x.Key) && x.Key != "SourceContext"))
            {
                custom[Snake(property.Key)] = property.Value is ScalarValue { Value: int or long or uint or bool } scalar ? scalar.Value : Text(property.Value);
            }

            document.Labels = null;
            document.Metadata = custom.Count == 0 ? null : custom;
            return document;
        }
    })), secrets);

    private static string? Value(LogEvent e, string name) => e.Properties.TryGetValue(name, out var value) ? Text(value) : null;

    private static string Text(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: bool flag } => flag ? "true" : "false",
        ScalarValue scalar => Convert.ToString(scalar.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString()
    };

    private static string Snake(string name) => string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));
}

// Renames the formatter's top-level metadata object to the openfsso namespace.
internal sealed class NamespaceFormatter(ITextFormatter inner) : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter();
        inner.Format(logEvent, buffer);
        using var document = JsonDocument.Parse(buffer.ToString());
        var bytes = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(property.NameEquals("metadata") ? "openfsso" : property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        output.WriteLine(System.Text.Encoding.UTF8.GetString(bytes.WrittenSpan));
    }
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
