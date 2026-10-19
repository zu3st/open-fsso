using OpenFsso.Client;
using OpenFsso.Configuration;
using OpenFsso.Receiver;

namespace OpenFsso.Tests;

[TestClass]
public sealed class YamlConfigurationTests
{
    private const string Client = """
        # A direct connection to each receiver.
        Receivers:
          - Name: A
            Host: receiver-a.example.test
            Address: 192.0.2.20
            Port: 8443
          - Name: B
            Host: receiver-b.example.test
            Address: 192.0.2.21
            Port: 8444
        StateDirectory: '/protected/State'
        LogDirectory: '/protected/Logs'
        """;

    private const string ReceiverKey = "test-only-health-key-0123456789abcdef";
    private const string Receiver = "Host: receiver.example.test\nLogDirectory: /protected/Logs\nHealthApiKey: '" + ReceiverKey + "'\n";

    private static ClientOptions ParseClient(string text) => YamlConfiguration.Parse<ClientOptions>(text, ConfigurationSchema.Client);
    private static ReceiverOptions ParseReceiver(string text) => YamlConfiguration.Parse<ReceiverOptions>(text, ConfigurationSchema.Receiver);

    [TestMethod]
    public void YamlLoadsBothServiceExamplesAndPreservesPathsAndTemplateSpaces()
    {
        var client = ParseClient(File.ReadAllText("config/client.example.yaml"));
        // The example deliberately ships without a key; each receiver needs its own.
        var receiver = ParseReceiver(File.ReadAllText("config/receiver.example.yaml").Replace("HealthApiKey: ''", "HealthApiKey: '" + ReceiverKey + "'"));
        Assert.AreEqual(@"C:\ProgramData\OpenFsso\OpenFssoClient\State", client.StateDirectory);
        Assert.AreEqual(8443, client.Receivers[1].Port);
        Assert.IsTrue(client.Receivers.All(x => x.CheckRevocation));
        Assert.EndsWith("ip={ip} ", receiver.LoginTemplate);
        Assert.EndsWith("ip={ip} ", receiver.LogoutTemplate);
        Assert.EndsWith("ip={ip} ", receiver.UpdateTemplate);
        Assert.Contains("action=update", receiver.UpdateTemplate);
        Assert.Contains("user=\"{user}\"", receiver.LoginTemplate);
    }

    [TestMethod]
    public void YamlAppliesDefaultsAndValidatesNestedClientEndpoints()
    {
        var options = ParseClient(Client);
        options.Validate();
        Assert.IsFalse(options.AllowRdp);
        Assert.IsTrue(options.Receivers[0].CheckRevocation);
        var receiver = ParseReceiver(Receiver);
        receiver.Validate();
        Assert.AreEqual(8443, receiver.Port);
        Assert.AreEqual(5, receiver.ExpirySeconds);
        Assert.AreEqual(3600, receiver.ReassertSeconds);
    }

    private static string ClientWith(int receivers) =>
        "Receivers:\n" + string.Concat(Enumerable.Range(1, receivers).Select(i => $"  - {{Name: R{i}, Host: receiver-{i}.example.test, Address: 192.0.2.{i}, Port: 8443}}\n")) +
        "StateDirectory: '/protected/State'\nLogDirectory: '/protected/Logs'\n";

    [TestMethod]
    [DataRow(1)]
    [DataRow(8)]
    public void ClientAcceptsOneToEightReceivers(int receivers)
    {
        var options = ParseClient(ClientWith(receivers));
        options.Validate();
        Assert.HasCount(receivers, options.Receivers);
    }

    [TestMethod]
    public void ClientRejectsMoreThanEightReceivers() => Reject(ClientWith(9));

    [TestMethod]
    public void YamlRejectsDuplicateUnknownAndIncorrectlyCasedFields()
    {
        foreach (string text in new[]
        {
            Client + "\nAllowRdp: false\nAllowRdp: true",
            Client.Replace("Host: receiver-a.example.test", "Host: receiver-a.example.test\n    Host: other.example.test"),
            Client + "\nRenewlSeconds: 60",
            Client.Replace("StateDirectory:", "stateDirectory:"),
            Client.Replace("Address: 192.0.2.20", "Adress: 192.0.2.20")
        })
        {
            Reject(text);
        }
    }

    [TestMethod]
    public void YamlRejectsMissingRequiredValuesNullsAndInvalidScalarTypes()
    {
        foreach (string text in new[]
        {
            "", "null", "[]", "Receivers: []",
            Client.Replace("    Port: 8443\n", ""),
            Client.Replace("StateDirectory: '/protected/State'", "StateDirectory: null"),
            Client.Replace("Port: 8443", "Port: invalid"),
            Client + "\nAllowRdp: invalid"
        })
        {
            Reject(text);
        }

        Reject("Receivers: [null, null]\nStateDirectory: /State\nLogDirectory: /Logs");
    }

    [TestMethod]
    public void YamlRejectsMultipleDocumentsTagsAndMalformedInputWithoutExposingValues()
    {
        Reject(Client + "\n---\n" + Client);
        Reject("!unregistered\n" + Client);
        const string secret = "private-api-key-must-never-appear-in-errors";
        var error = Assert.Throws<InvalidDataException>(() => ParseReceiver("Host: receiver.example.test\nLogDirectory: /Logs\nHealthApiKey: [" + secret));
        Assert.DoesNotContain(secret, error.ToString());
        Assert.IsNull(error.InnerException);
        Assert.Contains("line", error.Message);
    }

    [TestMethod]
    [DataRow(".yaml")]
    [DataRow(".yml")]
    public void YamlLoaderAcceptsYamlExtensionsAndBoundsInputSize(string extension)
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "config" + extension);
            File.WriteAllText(path, Client);
            YamlConfiguration.Load<ClientOptions>(path, ConfigurationSchema.Client).Validate();
            File.WriteAllText(path, new string(' ', 65537));
            Assert.Throws<InvalidDataException>(() => YamlConfiguration.Load<ClientOptions>(path, ConfigurationSchema.Client));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void ClientRejectsDuplicateReceiversAndRelativePaths()
    {
        Assert.Throws<InvalidDataException>(ParseClient(Client.Replace("Name: B", "Name: a")).Validate);
        Assert.Throws<InvalidDataException>(ParseClient(Client.Replace("receiver-b.example.test", "RECEIVER-A.example.test")).Validate);
        Assert.Throws<InvalidDataException>(ParseClient(Client.Replace("'/protected/State'", "'State'")).Validate);
        Reject(Client.Replace("Name: B", "Name: ' '"));
        Reject(Client.Replace("Name: B", "Name: " + new string('B', 65)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    [DataRow("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx ")]
    [DataRow("éééééééééééééééééééééééééééééééé")]
    public void ReceiverSchemaRejectsAbsentWeakOrInvalidHealthKeys(string key)
    {
        Assert.Throws<InvalidDataException>(() => ParseReceiver(Receiver.Replace(ReceiverKey, key)));
        Assert.Throws<InvalidDataException>(() => ParseReceiver(Receiver.Replace("HealthApiKey: '" + ReceiverKey + "'\n", "")));
        Assert.Throws<InvalidDataException>(() => ParseReceiver(Receiver.Replace(ReceiverKey, new string('x', 257))));
    }

    [TestMethod]
    [DataRow("LoginTemplate: 'action=login ip={ip}'")]
    [DataRow("LogoutTemplate: 'action=logout user={user}'")]
    [DataRow("LoginTemplate: \"user={user} ip={ip}\\nforged\"")]
    [DataRow("ExpirySeconds: 6")]
    [DataRow("QueueName: 'bad name'")]
    [DataRow("LeaseSeconds: 180")]
    public void ReceiverSchemaRejectsInvalidSettings(string setting) =>
        Assert.Throws<InvalidDataException>(() => ParseReceiver(Receiver + setting));

    private static void Reject(string text) => Assert.Throws<InvalidDataException>(() => ParseClient(text));
}
