using APIShared.Internal;
using APIShared.ScriptExtenderFixes;
using CrusaderDE;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.API;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public class LobbyMismatchTests
    {
        private static LobbyMismatchMod Mod(string id, string version = "1", bool clientside = false, string name = null) =>
            new LobbyMismatchMod(id, name ?? id, version, clientside);
        private static string Host(params LobbyMismatchMod[] mods) => DependencyFreeJson.SerializeCompact(mods.Select(m =>
            new Dictionary<string, object> { { "g", m.Guid }, { "n", m.Name }, { "v", m.Version }, { "c", m.Clientside } }).ToArray());
        private static LobbyMismatchReport Report(string json, params LobbyMismatchMod[] local) =>
            LobbyMismatchReport.Create(json, local, Array.Empty<LobbyMismatchMod>());

        [TestMethod]
        public void VersionDifferenceNamesTheModAndBothVersions()
        {
            var report = Report(Host(Mod("fixes", "1.29.0", name: "Fixes")), Mod("fixes", "1.26.4", name: "Fixes"));
            Assert.IsNull(report.MetadataIssue);
            Assert.AreEqual(1, report.Differences.Count);
            StringAssert.Contains(report.PopupText("reason", true), "Fixes: lokal 1.26.4 / Host 1.29.0");
            StringAssert.Contains(report.Differences[0].LogText, "[fixes]: local=1.26.4, host=1.29.0");
        }

        [TestMethod]
        public void MissingModsIdentifyWhichSideNeedsTheInstallation()
        {
            var report = Report(Host(Mod("only-host", "2")), Mod("only-local", "3"));
            Assert.AreEqual(2, report.Differences.Count);
            string text = report.PopupText("reason", true);
            StringAssert.Contains(text, "only-local: lokal 3 / Host nicht installiert");
            StringAssert.Contains(text, "only-host: lokal nicht installiert / Host 2");
        }

        [TestMethod]
        public void ClientOnlyDifferencesAreIgnoredExactlyWhenEveryInvolvedModIsClientside()
        {
            Assert.AreEqual(0, Report(Host(Mod("host-only", clientside: true), Mod("both", "2", true)),
                Mod("local-only", clientside: true), Mod("both", "1", true)).Differences.Count);
            Assert.AreEqual(1, Report(Host(Mod("both", "2", true)), Mod("both", "1", false)).Differences.Count);
            Assert.AreEqual(1, Report(Host(Mod("both", "2", false)), Mod("both", "1", true)).Differences.Count);
            // SE accepts equal versions even if NetworkMode differs.
            Assert.AreEqual(0, Report(Host(Mod("both", "1", true)), Mod("both", "1", false)).Differences.Count);
        }

        [TestMethod]
        public void AssetMetadataOverridesBarePluginsAndGuidComparisonIgnoresCase()
        {
            var report = LobbyMismatchReport.Create(Host(Mod("case", "1")),
                new[] { Mod("CASE", "1"), Mod("optional", clientside: true) },
                new[] { Mod("case", "99"), Mod("optional", clientside: false) });
            Assert.AreEqual(0, report.Differences.Count);
        }

        [TestMethod]
        public void BarePluginWithoutAssetMetadataIsGameplayRelevant()
        {
            var report = LobbyMismatchReport.Create("[]", Array.Empty<LobbyMismatchMod>(), new[] { Mod("bare") });
            Assert.AreEqual(1, report.Differences.Count);
            Assert.IsNull(report.Differences[0].Host);
        }

        [TestMethod]
        public void ReportKeepsAllDifferencesWhilePopupShowsTwoAndAlwaysKeepsTheLogHint()
        {
            var report = Report(Host(Mod("a", "2"), Mod("b", "2"), Mod("c", "2")), Mod("c"), Mod("b"), Mod("a"));
            Assert.AreEqual(3, report.Differences.Count);
            foreach (bool german in new[] { false, true })
            {
                string text = report.PopupText("reason", german);
                Assert.IsTrue(text.IndexOf("a:", StringComparison.Ordinal) < text.IndexOf("b:", StringComparison.Ordinal));
                Assert.IsFalse(text.Contains("c:"));
                StringAssert.Contains(text, german ? "Weitere Unterschiede vorhanden." : "More differences exist.");
                Assert.IsTrue(text.EndsWith(LobbyMismatchReport.LogHint(german), StringComparison.Ordinal));
            }
            StringAssert.Contains(report.Differences[2].LogText, "c [c]");
        }

        [TestMethod]
        public void LongFieldsAreBoundedOnlyForPresentationAndNeverHideTheLogHint()
        {
            string name = new string('N', 320), local = new string('L', 160), remote = new string('H', 160);
            var report = Report(Host(Mod("long", remote, name: name)), Mod("long", local, name: name));
            string text = report.PopupText("reason", true);
            Assert.IsFalse(text.Contains(name));
            Assert.IsFalse(text.Contains(local));
            StringAssert.Contains(text, "\u2026");
            Assert.IsTrue(text.EndsWith(LobbyMismatchReport.LogHint(true), StringComparison.Ordinal));
            StringAssert.Contains(report.Differences[0].LogText, name);
            StringAssert.Contains(report.Differences[0].LogText, local);
            StringAssert.Contains(report.Differences[0].LogText, remote);
        }

        [TestMethod]
        public void EveryRejectionWritesAllDifferencesWithTimestampsWithoutDiagnosticOptIn()
        {
            var report = Report(Host(Mod("a", "2"), Mod("b", "2"), Mod("c", "2")), Mod("a"), Mod("b"), Mod("c"));
            var messages = new List<string>();
            using (var log = new BepInEx.Logging.ManualLogSource("Lobby mismatch test"))
            {
                log.LogEvent += (_, args) => messages.Add(args.Data.ToString());
                LobbyMismatchRuntime.WriteReport(log, 123, "original reason", report);
                LobbyMismatchRuntime.WriteReport(log, 123, "original reason", report);
            }
            Assert.AreEqual(8, messages.Count, "Immediate retries must not suppress details via a limiter.");
            foreach (var difference in report.Differences)
                Assert.AreEqual(2, messages.Count(m => m.Contains(difference.LogText)));
            foreach (string message in messages)
                StringAssert.Matches(message, new System.Text.RegularExpressions.Regex(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] SE_LOBBY_MISMATCH:"));
        }

        [TestMethod]
        public void MissingMalformedOrDuplicateMetadataCannotClaimACompleteComparison()
        {
            foreach (string json in new[] { null, "", "{}", "[null]", "broken", "[{\"g\":\"a\",\"c\":1}]",
                "[{\"g\":\"a\",\"n\":1}]", "[{\"g\":\"a\"},{\"g\":\"A\"}]", "[{\"g\":\" \"}]" })
            {
                var report = Report(json, Mod("local"));
                Assert.IsNotNull(report.MetadataIssue, json);
                Assert.AreEqual(0, report.Differences.Count, "Do not present a partial parse as a complete inventory.");
                string text = report.PopupText("legacy mod hash differs", false);
                StringAssert.Contains(text, "unavailable");
                StringAssert.Contains(text, "legacy mod hash differs");
                Assert.IsTrue(text.EndsWith(LobbyMismatchReport.LogHint(false), StringComparison.Ordinal));
            }
        }

        [TestMethod]
        public void MetadataDefaultsAndCleaningFollowTheExtenderDto()
        {
            Assert.IsTrue(LobbyMismatchReport.TryReadHost("[{\"g\":\" id \",\"n\":null,\"v\":null}]", out var mods, out _));
            Assert.AreEqual("id", mods["id"].Name);
            Assert.AreEqual("0.0.0", mods["id"].Version);
            Assert.IsFalse(mods["id"].Clientside);
            Assert.AreEqual("a b", Mod("id", name: " a\nb ").Name);
        }

        [TestMethod]
        public void UnexpectedEmptyComparisonStillExplainsTheOriginalRejection()
        {
            var report = Report("[]");
            StringAssert.Contains(report.PopupText("SE reason", false), "SE reason");
            StringAssert.Contains(report.PopupText("SE reason", false), "does not fully explain");
        }

        [TestMethod]
        public void InstalledExtenderBodyAcceptsTheProductionEditAndCompiles()
        {
            var target = typeof(GameNetworkAPI).GetMethod("RejectLobby", BindingFlags.NonPublic | BindingFlags.Static);
            LobbyMismatchIl.ValidateTarget(target);
            using (var definition = new DynamicMethodDefinition(target))
            using (var context = new ILContext(definition.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance })
            {
                context.Invoke(il => LobbyMismatchIl.ReplacePopup(il, (t, a, m, s, l, r) => { }));
                Assert.IsNotNull(definition.Generate());
            }
        }

        [TestMethod]
        public void EditedRejectionRetainsFalseResultOtherSideEffectsAndOriginalPopupArguments()
        {
            Fixture.Before = Fixture.After = Fixture.Caught = 0;
            int popupCalls = 0, acceptCalls = 0;
            Fixture.Accept = () => acceptCalls++;
            WithEditedFixture("Reject", (title, accept, multiplayer, sands, lobby, reason) => {
                popupCalls++;
                Assert.AreEqual("missing mods!", title);
                Assert.IsTrue(multiplayer);
                Assert.IsFalse(sands);
                Assert.AreEqual(123UL, lobby.m_SteamID);
                Assert.AreEqual("original reason", reason);
                accept();
            }, reject => Assert.IsFalse(reject(new CSteamID(123), "original reason")));
            Assert.AreEqual(1, popupCalls, Fixture.LastError?.ToString());
            Assert.AreEqual(1, acceptCalls);
            Assert.AreEqual(1, Fixture.Before);
            Assert.AreEqual(1, Fixture.After);
            Assert.AreEqual(0, Fixture.Caught);
        }

        [TestMethod]
        public void OriginalExceptionBoundaryStillHandlesPopupFailureAndRejects()
        {
            Fixture.Before = Fixture.After = Fixture.Caught = 0;
            WithEditedFixture("Reject", (t, a, m, s, l, r) => throw new ApplicationException("popup failed"),
                reject => Assert.IsFalse(reject(default, "reason")));
            Assert.AreEqual(1, Fixture.Caught);
            Assert.AreEqual(1, Fixture.After);
            Assert.AreEqual("popup failed", Fixture.LastError.Message);
        }

        [TestMethod]
        public void ChangedSignaturesAndPopupBodiesAreRefusedBeforePublication()
        {
            Assert.ThrowsExactly<MissingMethodException>(() => LobbyMismatchIl.ValidateTarget(null));
            Assert.ThrowsExactly<InvalidOperationException>(() => LobbyMismatchIl.ValidateTarget(
                typeof(Fixture).GetMethod("WrongSignature", BindingFlags.NonPublic | BindingFlags.Static)));
            foreach (string name in new[] { "ChangedMessage", "TwoPopups", "NoPopup" })
            {
                var target = typeof(Fixture).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
                using (var definition = new DynamicMethodDefinition(target))
                using (var context = new ILContext(definition.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance })
                {
                    int before = context.Body.Instructions.Count;
                    Assert.ThrowsExactly<InvalidOperationException>(() => context.Invoke(il =>
                        LobbyMismatchIl.ReplacePopup(il, (t, a, m, s, l, r) => { })), name);
                    Assert.AreEqual(before, context.Body.Instructions.Count);
                }
            }
        }

        private static void WithEditedFixture(string name, LobbyMismatchIl.Popup show, Action<Func<CSteamID, string, bool>> run)
        {
            var target = typeof(Fixture).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            LobbyMismatchIl.ValidateTarget(target);
            using (var definition = new DynamicMethodDefinition(target))
            using (var context = new ILContext(definition.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance })
            {
                context.Invoke(il => LobbyMismatchIl.ReplacePopup(il, show));
                // Match ILHook's ordinary DynamicMethod backend. This managed body has no calli
                // and does not need the separate ImGui MethodBuilder compiler workaround.
                var generated = definition.Generate();
                run((Func<CSteamID, string, bool>)generated.CreateDelegate(typeof(Func<CSteamID, string, bool>)));
            }
        }

        private static class Fixture
        {
            internal static int Before, After, Caught;
            internal static Action Accept;
            internal static Exception LastError;
            private static bool Reject(CSteamID lobby, string reason)
            {
                Before++;
                try { HUD_ConfirmationPopup.ShowOK("missing mods!", Accept, true, false); }
                catch (Exception error) { Caught++; LastError = error; }
                After++;
                return false;
            }
            private static int WrongSignature(CSteamID lobby, string reason) => 0;
            private static bool ChangedMessage(CSteamID lobby, string reason)
            { HUD_ConfirmationPopup.ShowOK("a future different dialog", null, true, false); return false; }
            private static bool TwoPopups(CSteamID lobby, string reason)
            {
                HUD_ConfirmationPopup.ShowOK("missing mods!", null, true, false);
                HUD_ConfirmationPopup.ShowOK("another", null, true, false);
                return false;
            }
            private static bool NoPopup(CSteamID lobby, string reason) => false;
        }
    }
}
