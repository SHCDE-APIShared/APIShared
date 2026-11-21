using APIShared.Multiplayer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Tests;

[TestClass]
public sealed class SimulationAgreementTests
{
    [TestMethod]
    public void MissingRequiredReportWaitsButAnAlreadyKnownConflictRejects()
    {
        const string local = "required=r1";
        Assert.IsFalse(SimulationAgreementPolicy.TryDecide(local, new[] { local, null },
            out _, out _, out bool waiting));
        Assert.IsTrue(waiting);
        Assert.IsFalse(SimulationAgreementPolicy.TryDecide(local, new[] { local, "required=r0", null },
            out _, out _, out waiting));
        Assert.IsFalse(waiting);
        Assert.IsTrue(SimulationAgreementPolicy.TryDecide(local, new[] { local, local },
            out string enabled, out _, out waiting));
        Assert.AreEqual("required", enabled);
        Assert.IsFalse(waiting);
    }

    [DataTestMethod]
    [DataRow("world=r1;shift=o1", "shift;world")]
    [DataRow("world=r1;shift=o0", "world")]
    public void ReadinessDecidesOneResultForBothPlayers(string remote, string expected)
    {
        const string local = "world=r1;shift=o1";
        Assert.IsTrue(SimulationAgreementPolicy.TryDecide(local, new[] { local, remote }, out var decision, out _));
        Assert.AreEqual(expected, decision);
        Assert.IsTrue(SimulationAgreementPolicy.Accepts(local, decision));
        Assert.IsTrue(SimulationAgreementPolicy.Accepts(remote, decision));
    }

    [TestMethod]
    public void MissingOptionalReportDisablesShiftAndAllowsEitherLocalReadiness()
    {
        Assert.IsTrue(SimulationAgreementPolicy.TryDecide("shift=o1", new[] { "shift=o1", null }, out var decision, out _));
        Assert.AreEqual("", decision);
        Assert.IsTrue(SimulationAgreementPolicy.Accepts("shift=o1", decision));
        Assert.IsTrue(SimulationAgreementPolicy.Accepts("shift=o0", decision));
    }

    [DataTestMethod]
    [DataRow("world=r0;shift=o1")]
    [DataRow("world=o1;shift=o1")]
    [DataRow("world=r1;world=r1")]
    [DataRow("world=r1")]
    [DataRow(null)]
    public void RequiredFeatureRejectsMissingOrInconsistentReports(string remote)
    {
        const string local = "world=r1;shift=o1";
        Assert.IsFalse(SimulationAgreementPolicy.TryDecide(local, new[] { local, remote }, out _, out _));
    }

    [TestMethod]
    public void UnsupportedWorldFixOnBothPeersRemainsDisabled()
    {
        Assert.IsTrue(SimulationAgreementPolicy.TryDecide("world=r0",
            new[] { "world=r0", "world=r0" }, out var decision, out _));
        Assert.AreEqual("", decision);
        Assert.IsTrue(SimulationAgreementPolicy.Accepts("world=r0", decision));
        Assert.IsFalse(SimulationAgreementPolicy.Accepts("world=r1", decision));
        Assert.IsFalse(SimulationAgreementPolicy.Accepts("world=r0", "world"));
    }
}
