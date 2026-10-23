using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public class SavegameLoadAgreementTests
{
    [TestMethod]
    public void SaveIdentityUsesVanillasCombinedChecksumRatherThanStricterPeerFileEquality()
    {
        Assert.IsTrue(SavegameLoadAgreement.MatchesSaveIdentity("COOP", 17, 29, "coop", 17, 29));
        Assert.IsTrue(SavegameLoadAgreement.MatchesSaveIdentity("coop", 17 ^ 8, 29 ^ 8, "coop", 17, 29));
        Assert.IsFalse(SavegameLoadAgreement.MatchesSaveIdentity("other", 17, 29, "coop", 17, 29));
        Assert.IsFalse(SavegameLoadAgreement.MatchesSaveIdentity("coop", 18, 29, "coop", 17, 29));
    }
    [TestMethod]
    public void SoloHostNeedsNoNetworkEcho()
    {
        var state = new SavegameLoadAgreement("load-1", "verified", Array.Empty<ulong>());
        Assert.IsTrue(state.Ready);
        Assert.IsFalse(state.Accept(42, "load-1", "verified"));
    }

    [TestMethod]
    public void OnlyCurrentPeersWithExactTransactionAndValuesCanReleaseTheLoad()
    {
        var state = new SavegameLoadAgreement("load-2", "saved-values", new ulong[] { 101, 102 });
        Assert.IsFalse(state.Accept(999, "load-2", "saved-values"));
        Assert.IsFalse(state.Accept(101, "old-load", "saved-values"));
        Assert.IsFalse(state.Accept(101, "load-2", "lobby-values"));
        Assert.IsFalse(state.Ready);
        Assert.IsTrue(state.Accept(101, "load-2", "saved-values"));
        Assert.IsTrue(state.Accept(101, "load-2", "saved-values"));
        Assert.IsFalse(state.Ready);
        Assert.IsTrue(state.Accept(102, "load-2", "saved-values"));
        Assert.IsTrue(state.Ready);
    }

    [TestMethod]
    public void AChangedRosterDoesNotInheritReadiness()
    {
        var first = new SavegameLoadAgreement("first", "values", new ulong[] { 101 });
        first.Accept(101, "first", "values");
        var replacement = new SavegameLoadAgreement("second", "values", new ulong[] { 101, 102 });
        Assert.IsTrue(first.Ready);
        Assert.IsFalse(replacement.Accept(101, "first", "values"));
        Assert.IsFalse(replacement.Ready);
    }

    [TestMethod]
    public void FingerprintsIgnoreDictionaryOrderButDetectValuesAndParticipants()
    {
        var a = new Dictionary<string, Dictionary<string, byte[]>> { ["mod"] = new() { ["b"] = [2], ["a"] = [1] } };
        var b = new Dictionary<string, Dictionary<string, byte[]>> { ["mod"] = new() { ["a"] = [1], ["b"] = [2] } };
        Assert.AreEqual(SavegameLoadAgreement.Fingerprint(a), SavegameLoadAgreement.Fingerprint(b));
        b["mod"]["a"] = [3];
        Assert.AreNotEqual(SavegameLoadAgreement.Fingerprint(a), SavegameLoadAgreement.Fingerprint(b));
        b["mod"]["a"] = [1]; b["other"] = new();
        Assert.AreNotEqual(SavegameLoadAgreement.Fingerprint(a), SavegameLoadAgreement.Fingerprint(b));
    }
}
