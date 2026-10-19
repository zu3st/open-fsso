using System.Text.Json;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;

namespace OpenFsso.Tests;

[TestClass]
public sealed class LoggingTests
{
    private const string Secret = "test-only-health-key+0123456789/abcdef=";

    private static JsonElement Format(LogEvent logEvent)
    {
        using var output = new StringWriter();
        OpenFsso.Logging.Logging.Formatter("open-fsso-test", Secret).Format(logEvent, output);
        Assert.DoesNotContain(Secret, output.ToString());
        Assert.DoesNotContain(JsonEncodedText.Encode(Secret).ToString(), output.ToString());
        return JsonDocument.Parse(output.ToString()).RootElement;
    }

    private static LogEvent Event(string template, Exception? error, params LogEventProperty[] properties) =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Warning, error, new MessageTemplateParser().Parse(template), properties);

    [TestMethod]
    public void EcsDocumentsMapServiceUserAndSourceFields()
    {
        var log = Format(Event("Mapping {UserName} ({UserSid}) at {SourceIp}", null,
            new("UserName", new ScalarValue("DOMAIN\\Alice")), new("UserSid", new ScalarValue("S-1-5-21-1")), new("SourceIp", new ScalarValue("192.0.2.10"))));
        Assert.AreEqual("open-fsso-test", log.GetProperty("service").GetProperty("name").GetString());
        Assert.IsNotNull(log.GetProperty("service").GetProperty("version").GetString());
        Assert.IsFalse(log.TryGetProperty("agent", out _));
        Assert.IsFalse(log.TryGetProperty("event", out var details) && (details.TryGetProperty("created", out _) || details.TryGetProperty("timezone", out _)));
        Assert.AreEqual("S-1-5-21-1", log.GetProperty("user").GetProperty("id").GetString());
        Assert.AreEqual("DOMAIN\\Alice", log.GetProperty("user").GetProperty("name").GetString());
        Assert.AreEqual("192.0.2.10", log.GetProperty("source").GetProperty("ip").GetString());
        Assert.AreEqual("warning", log.GetProperty("log.level").GetString(), ignoreCase: true);
    }

    [TestMethod]
    public void ConfiguredSecretsAreMaskedInMessagesPropertiesAndExceptions()
    {
        var log = Format(Event("Rejected {Header}", new InvalidOperationException("Bearer " + Secret), new LogEventProperty("Header", new ScalarValue("Bearer " + Secret))));
        Assert.Contains("[redacted]", log.GetRawText());
        Assert.AreEqual(nameof(InvalidOperationException), log.GetProperty("error").GetProperty("type").GetString()!.Split('.')[^1]);
    }
}
