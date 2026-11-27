using APIShared.ScriptExtenderFixes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace APISharedTests
{
    // Contract: scripted Lords that Script Extender does not guarantee for all players (client-side or
    // unregistered package, locally disabled scripts) are named in a warning; correctly networked Lords only
    // appear in the comparable summary line, and matches without scripted Lords log nothing.
    [TestClass]
    public class LordScriptReportTests
    {
        private static LordScriptSlot Slot(int player, string name, string guid, bool? networked) =>
            new LordScriptSlot(player, name, guid, "1.2.0", networked, "ABCDEF0123456789");

        [TestMethod]
        public void NetworkedScriptedLordIsOnlySummarized()
        {
            var slots = new[] { Slot(3, "Lord Nox", "nox", true) };
            Assert.AreEqual(0, LordScriptReport.Warnings(slots, true).Count);
            string summary = LordScriptReport.Summary(slots, true);
            StringAssert.Contains(summary, "player 3 'Lord Nox' package=nox v1.2.0 networkMode=Networked initLuaSha256=ABCDEF0123456789");
            StringAssert.Contains(summary, "AllowScriptedLords=True");
        }

        [TestMethod]
        public void ClientSideAndUnregisteredScriptedLordsAreWarnedOncePerPackage()
        {
            var slots = new[] { Slot(3, "Lord Nox", "nox", false), Slot(5, "Lord Nox", "nox", false), Slot(6, "Loose", null, null) };
            var warnings = LordScriptReport.Warnings(slots, true);
            Assert.AreEqual(2, warnings.Count, string.Join("\n", warnings));
            Assert.IsTrue(warnings.Any(text => text.Contains("'Lord Nox' (player 3, 5)") && text.Contains("NetworkMode 0") &&
                text.Contains("nox v1.2.0")));
            Assert.IsTrue(warnings.Any(text => text.Contains("'Loose' (player 6)") && text.Contains("not a registered")));
        }

        [TestMethod]
        public void DisabledScriptsAreWarnedEvenForNetworkedLords()
        {
            var warnings = LordScriptReport.Warnings(new[] { Slot(4, "Lord Nox", "nox", true) }, false);
            Assert.AreEqual(1, warnings.Count);
            StringAssert.Contains(warnings[0], "AllowScriptedLords");
            StringAssert.Contains(warnings[0], "'Lord Nox' (player 4)");
        }

        [TestMethod]
        public void MatchWithoutScriptedLordsLogsNothing()
        {
            Assert.IsNull(LordScriptReport.Summary(new LordScriptSlot[0], false));
            Assert.AreEqual(0, LordScriptReport.Warnings(new LordScriptSlot[0], false).Count);
        }
    }
}
