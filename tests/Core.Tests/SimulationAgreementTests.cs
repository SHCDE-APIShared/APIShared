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

    [TestMethod]
    public void ResyncCohortRequiresTheSameReadyManifestOnEveryPeer()
    {
        const string legacy = "world=r1";
        string cohortKey = SimulationAgreementPolicy.ResyncCohortKeyPrefix + new string('a', 64);
        string ready = legacy + ";" + cohortKey + "=r1";
        string unavailable = legacy + ";" + cohortKey + "=r0";
        string different = legacy + ";" + SimulationAgreementPolicy.ResyncCohortKeyPrefix + new string('b', 64) + "=r1";

        Assert.IsFalse(SimulationAgreementPolicy.TryDecide(ready, new[] { ready, null },
            out _, out _, out bool waiting));
        Assert.IsTrue(waiting);
        Assert.IsFalse(SimulationAgreementPolicy.TryDecide(ready, new[] { ready, legacy }, out _, out _));
        Assert.IsFalse(SimulationAgreementPolicy.TryDecide(ready, new[] { ready, different }, out _, out _));
        Assert.IsFalse(SimulationAgreementPolicy.TryDecide(unavailable, new[] { unavailable, unavailable }, out _, out _));
        Assert.IsFalse(SimulationAgreementPolicy.Accepts(unavailable, "world"));

        Assert.IsTrue(SimulationAgreementPolicy.TryDecide(ready, new[] { ready, ready }, out string enabled, out _));
        Assert.AreEqual(cohortKey + ";world", enabled);
        Assert.IsTrue(SimulationAgreementPolicy.Accepts(ready, enabled));
        Assert.IsFalse(SimulationAgreementPolicy.Accepts(legacy, enabled));
        Assert.IsFalse(SimulationAgreementPolicy.Accepts(ready, "world"));
    }
}
