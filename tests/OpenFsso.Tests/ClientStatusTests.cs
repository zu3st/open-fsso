using OpenFsso.Core;

namespace OpenFsso.Tests;

[TestClass]
public sealed class ClientStatusTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [TestMethod]
    public void StatusNeverTreatsAnAttemptOrReconciliationAsAuthentication()
    {
        var journal = new ClientStatusJournal(["A", "B"]);
        journal.Attempt("A", 1, Now);
        var status = journal.Read(1, "DOMAIN\\Alice", Now);
        Assert.IsTrue(status.Receivers.All(x => x.LastSentUtc == null));
        Assert.AreEqual(ClientIndicator.Working, ClientStatusView.Create(status, Now).Indicator);
    }

    [TestMethod]
    public void StatusFiltersOldOwnerAcknowledgementsAndLateFailures()
    {
        var journal = new ClientStatusJournal(["A", "B"]);
        journal.Sent("A", 1, "192.0.2.1", Now);
        Assert.IsNull(journal.Read(2, "DOMAIN\\Bob", Now).Receivers[0].Ip);
        journal.Sent("A", 2, "192.0.2.2", Now);
        journal.Failed("A", 1, "Old failure");
        var status = journal.Read(2, "DOMAIN\\Bob", Now);
        Assert.AreEqual("192.0.2.2", status.Receivers[0].Ip);
        Assert.IsNull(status.Receivers[0].Error);
        Assert.IsTrue(journal.Read(3, null, Now).Receivers.All(x => x.Ip == null && x.LastSentUtc == null));
    }

    [TestMethod]
    public void StatusDistinguishesHealthyDegradedFailedAndRecoveredReceivers()
    {
        var journal = new ClientStatusJournal(["A", "B"]);
        journal.Sent("A", 1, "192.0.2.1", Now);
        journal.Sent("B", 1, "192.0.2.1", Now);
        ClientStatusView View() => ClientStatusView.Create(journal.Read(1, "DOMAIN\\Alice", Now), Now);
        Assert.AreEqual(ClientIndicator.Connected, View().Indicator);
        journal.Failed("A", 1, "Connection refused");
        Assert.AreEqual(ClientIndicator.Working, View().Indicator);
        journal.Failed("B", 1, "Connection refused");
        Assert.AreEqual(ClientIndicator.Error, View().Indicator);
        Assert.Contains("Connection refused", View().Text);
        Assert.Contains("192.0.2.1", View().Text);
        journal.Sent("A", 1, "192.0.2.2", Now);
        journal.Sent("B", 1, "192.0.2.2", Now);
        Assert.AreEqual(ClientIndicator.Connected, View().Indicator);
        Assert.DoesNotContain("Connection refused", View().Text);
    }

    [TestMethod]
    public void SingleReceiverIsGreenWhenHealthyAndRedWhenFailed()
    {
        var journal = new ClientStatusJournal(["A"]);
        ClientStatusView View() => ClientStatusView.Create(journal.Read(1, "DOMAIN\\Alice", Now), Now);
        journal.Attempt("A", 1, Now);
        Assert.AreEqual("OpenFSSO - Connecting", View().Title);
        journal.Sent("A", 1, "192.0.2.1", Now);
        Assert.AreEqual(ClientIndicator.Connected, View().Indicator);
        journal.Failed("A", 1, "Connection refused");
        Assert.AreEqual(ClientIndicator.Error, View().Indicator);
    }

    [TestMethod]
    public void GreenNeedsEveryConfiguredReceiver()
    {
        var journal = new ClientStatusJournal(["A", "B", "C"]);
        journal.Sent("A", 1, "192.0.2.1", Now);
        journal.Sent("B", 1, "192.0.2.1", Now);
        var view = ClientStatusView.Create(journal.Read(1, "DOMAIN\\Alice", Now), Now);
        Assert.AreEqual(ClientIndicator.Working, view.Indicator);
        Assert.AreEqual("OpenFSSO - Partially connected", view.Title);
        journal.Sent("C", 1, "192.0.2.1", Now);
        Assert.AreEqual(ClientIndicator.Connected, ClientStatusView.Create(journal.Read(1, "DOMAIN\\Alice", Now), Now).Indicator);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(9)]
    public void StatusOutsideTheReceiverRangeIsUnavailable(int receivers)
    {
        var status = new ClientStatus(1, Now, "Alice", [.. Enumerable.Range(0, receivers).Select(i => new ReceiverStatus("R" + i, "192.0.2.1", LastSentUtc: Now))]);
        Assert.AreEqual("OpenFSSO - Service unavailable", ClientStatusView.Create(status, Now).Title);
    }

    [TestMethod]
    public void StaleStatusAndStaleAcknowledgementsCannotStayGreen()
    {
        var journal = new ClientStatusJournal(["A", "B"]);
        journal.Sent("A", 1, "192.0.2.1", Now);
        journal.Sent("B", 1, "192.0.2.1", Now);
        Assert.AreEqual(ClientIndicator.Error, ClientStatusView.Create(journal.Read(1, "Alice", Now), Now.AddSeconds(11)).Indicator);
        Assert.AreEqual(ClientIndicator.Error, ClientStatusView.Create(journal.Read(1, "Alice", Now.AddSeconds(121)), Now.AddSeconds(121)).Indicator);
        Assert.Contains("service", ClientStatusView.Create(null, Now).Text, StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(ClientIndicator.Idle, ClientStatusView.Create(journal.Read(2, null, Now), Now).Indicator);
    }

    [TestMethod]
    public void TooltipSanitizesControlCharactersAndBoundsPresentationStrings()
    {
        var view = ClientStatusView.Create(new(1, Now, "Alice\nInjected", [new("A", Error: new string('x', 500)), new("B")]), Now);
        Assert.Contains("Alice Injected", view.Text);
        Assert.DoesNotContain(new string('x', 161), view.Text);
    }

    [TestMethod]
    public void HealthyTooltipShowsOneRelativeTimeForLatestAcknowledgement()
    {
        var view = ClientStatusView.Create(new(1, Now, "Alice", [new("A", "192.0.2.1", LastSentUtc: Now.AddSeconds(-50)), new("B", "192.0.2.1", LastSentUtc: Now.AddSeconds(-12))]), Now);
        Assert.AreEqual("User: Alice\nIP: 192.0.2.1\nLast sent: 12 seconds ago", view.Text);
        Assert.AreEqual("OpenFSSO - Connected", view.Title);
    }
}
