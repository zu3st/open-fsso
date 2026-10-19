using OpenFsso.Windows;

namespace OpenFsso.Tests;

[TestClass]
public sealed class SessionTests
{
    [TestMethod]
    [DataRow((ushort)0, false, true)]
    [DataRow((ushort)0, true, true)]
    [DataRow((ushort)2, false, false)]
    [DataRow((ushort)2, true, true)]
    [DataRow((ushort)1, true, false)]
    public void ConsoleSessionsAreEligibleAndRdpOnlyWhenAllowed(ushort protocol, bool allowRdp, bool eligible) =>
        Assert.AreEqual(eligible, Sessions.Eligible(protocol, allowRdp));
}
