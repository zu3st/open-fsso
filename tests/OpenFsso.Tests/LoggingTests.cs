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
        OpenFsso.Logging.Logging.Formatter("open-fsso-test", "OpenFssoTest", Secret).Format(logEvent, output);
        Assert.DoesNotContain(Secret, output.ToString());
        Assert.DoesNotContain(JsonEncodedText.Encode(Secret).ToString(), output.ToString());
        return JsonDocument.Parse(output.ToString()).RootElement;
    }

    private static LogEvent Event(string template, Exception? error, params LogEventProperty[] properties) =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Warning, error, new MessageTemplateParser().Parse(template), properties);

    [TestMethod]
    public void EcsDocumentsMapFieldsAndKeepTheMessageFree()
    {
        var log = Format(Event("Mapping sent to the collector", null,
            new("UserName", new ScalarValue("DOMAIN\\Alice")), new("UserSid", new ScalarValue("S-1-5-21-1")), new("SourceIp", new ScalarValue("192.0.2.10")),
            new("Action", new ScalarValue("login")), new("Category", new ScalarValue("session")), new("Type", new ScalarValue("start")), new("Outcome", new ScalarValue("success")), new("Reason", new ScalarValue("presence")), new("SessionId", new ScalarValue(2)), new("HttpStatus", new ScalarValue(503)),
            new("RequestPath", new ScalarValue("/v1/session"))));
        Assert.AreEqual("Mapping sent to the collector", log.GetProperty("message").GetString());
        Assert.AreEqual("open-fsso-test", log.GetProperty("service").GetProperty("name").GetString());
        Assert.IsNotNull(log.GetProperty("service").GetProperty("version").GetString());
        Assert.IsFalse(log.TryGetProperty("agent", out _));
        Assert.IsFalse(log.TryGetProperty("process", out _));
        Assert.IsFalse(log.TryGetProperty("metadata", out _));
        var details = log.GetProperty("event");
        Assert.IsFalse(details.TryGetProperty("created", out _) || details.TryGetProperty("timezone", out _));
        Assert.AreEqual("login", details.GetProperty("action").GetString());
        Assert.AreEqual("event", details.GetProperty("kind").GetString());
        Assert.AreEqual("session", details.GetProperty("category")[0].GetString());
        Assert.AreEqual("start", details.GetProperty("type")[0].GetString());
        Assert.AreEqual("success", details.GetProperty("outcome").GetString());
        Assert.AreEqual("OpenFssoTest", log.GetProperty("service").GetProperty("node.name").GetString());
        Assert.AreEqual("Alice", log.GetProperty("related").GetProperty("user")[0].GetString());
        Assert.AreEqual("192.0.2.10", log.GetProperty("related").GetProperty("ip")[0].GetString());
        Assert.IsFalse(log.TryGetProperty("host", out _));
        Assert.AreEqual("presence", details.GetProperty("reason").GetString());
        Assert.AreEqual("S-1-5-21-1", log.GetProperty("user").GetProperty("id").GetString());
        Assert.AreEqual("Alice", log.GetProperty("user").GetProperty("name").GetString());
        Assert.AreEqual("DOMAIN", log.GetProperty("user").GetProperty("domain").GetString());
        Assert.AreEqual("192.0.2.10", log.GetProperty("source").GetProperty("ip").GetString());
        Assert.AreEqual(503, log.GetProperty("http").GetProperty("response.status_code").GetInt32());
        Assert.AreEqual(2, log.GetProperty("openfsso").GetProperty("session_id").GetInt32());
        Assert.IsFalse(log.TryGetProperty("labels", out _));
        Assert.IsFalse(log.GetProperty("openfsso").TryGetProperty("message_template", out _));
        Assert.IsFalse(log.GetProperty("openfsso").TryGetProperty("request_path", out _));
        Assert.AreEqual("warning", log.GetProperty("log.level").GetString(), ignoreCase: true);
    }

    [TestMethod]
    public void EventsWithoutAUserCarryNoUser()
    {
        var log = Format(Event("Receiver recovered", null, new LogEventProperty("Receiver", new ScalarValue("A"))));
        Assert.IsFalse(log.TryGetProperty("user", out _));
        Assert.AreEqual("A", log.GetProperty("openfsso").GetProperty("receiver").GetString());
    }

    [TestMethod]
    public void ReceiverEndpointsMapToServerFields()
    {
        var server = Format(Event("Receiver recovered", null, new LogEventProperty("ServerAddress", new ScalarValue("receiver-a.example.test")),
            new LogEventProperty("ServerIp", new ScalarValue("192.0.2.20")), new LogEventProperty("ServerPort", new ScalarValue(8443)))).GetProperty("server");
        Assert.AreEqual("receiver-a.example.test", server.GetProperty("address").GetString());
        Assert.AreEqual("192.0.2.20", server.GetProperty("ip").GetString());
        Assert.AreEqual(8443, server.GetProperty("port").GetInt32());
    }

    [TestMethod]
    public void FrameworkEventIdsBecomeActions()
    {
        var id = new StructureValue([new LogEventProperty("Id", new ScalarValue(13)), new LogEventProperty("Name", new ScalarValue("AuthenticationSchemeForbidden"))]);
        var details = Format(Event("AuthenticationScheme: Windows was forbidden.", null, new LogEventProperty("EventId", id))).GetProperty("event");
        Assert.AreEqual("authentication-scheme-forbidden", details.GetProperty("action").GetString());
        Assert.AreEqual("authentication", details.GetProperty("category")[0].GetString());
        Assert.AreEqual("failure", details.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void ConfiguredSecretsAreMaskedInMessagesPropertiesAndExceptions()
    {
        var log = Format(Event("Rejected {Header}", new InvalidOperationException("Bearer " + Secret), new LogEventProperty("Header", new ScalarValue("Bearer " + Secret))));
        Assert.Contains("[redacted]", log.GetRawText());
        Assert.AreEqual(nameof(InvalidOperationException), log.GetProperty("error").GetProperty("type").GetString()!.Split('.')[^1]);
    }
}
