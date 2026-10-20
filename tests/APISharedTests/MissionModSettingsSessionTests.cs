using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using APIShared;
using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public class MissionModSettingsSessionTests
    {
        private sealed class SyncHostOnlyAttribute : Attribute { }
        private sealed class Host : IModSettingsPresetEndpoint, IModSettingsMissionSourceEndpoint
        {
            [SyncHostOnly] public int Value { get; set; } = 7;
            internal bool FailEnter, FailExit, Explicit, Editable;
            internal int Exits, Entries;
            public bool IsMissionPresetActive { get; private set; }
            public Dictionary<string, byte[]> System_CreateDisabledMissionPresetSnapshot() => new Dictionary<string, byte[]> {
                ["Value"] = MessagePackSerializer.Serialize(0), ["Personal"] = MessagePackSerializer.Serialize(99) };
            public void System_SetExplicitMissionSettings(bool value) => Explicit = value;
            public void System_EnterMissionPreset(Dictionary<string, byte[]> snapshot, string label, bool editable)
            {
                Entries++; Editable = editable; IsMissionPresetActive = true;
                Value = MessagePackSerializer.Deserialize<int>(snapshot["Value"]);
                if (FailEnter) throw new InvalidOperationException("Partial enter");
            }
            public void System_ExitMissionPreset()
            {
                Exits++;
                if (FailExit) throw new InvalidOperationException("Exit failed");
                IsMissionPresetActive = false; Value = 7;
            }
        }
        private static Dictionary<string, IModSettingsPresetEndpoint> Participants(params Host[] hosts) =>
            hosts.Select((host, i) => new { host, i }).ToDictionary(item => "mod" + item.i, item => (IModSettingsPresetEndpoint)item.host);
        private static MissionModSettingsDocument Document(object value = null, bool player = false)
        {
            var doc = new MissionModSettingsDocument();
            if (value != null || player) doc.Mods["mod0"] = new MissionModSettingsEntry {
                PlayerSettings = player ? new[] { "Value" } : Array.Empty<string>(),
                Overrides = value == null ? new Dictionary<string, object>() : new Dictionary<string, object> { ["Value"] = value } };
            return doc;
        }

        [TestMethod]
        public void PlayableDefaultsPlayerAndFixedKeepTheirPrecedence()
        {
            var host = new Host(); var session = new MissionModSettingsSession(); var participants = Participants(host);
            session.Apply(Document(), participants, MissionModSettingsApplicationKind.Playable);
            Assert.AreEqual(0, host.Value); Assert.IsFalse(host.Explicit); Assert.IsNull(session.MissionSessionId);
            session.Apply(Document(player: true), participants, MissionModSettingsApplicationKind.Playable);
            Assert.AreEqual(7, host.Value);
            session.Apply(Document(11), participants, MissionModSettingsApplicationKind.Playable);
            Assert.AreEqual(11, host.Value); Assert.IsTrue(host.Explicit); Assert.IsFalse(host.Editable);
            session.ConfirmStarted(12); Assert.AreEqual(12L, session.MissionSessionId);
            session.ConfirmStarted(13); Assert.AreEqual(13L, session.MissionSessionId);
            session.Exit(); Assert.AreEqual(7, host.Value); Assert.IsFalse(host.Explicit); Assert.IsNull(session.MissionSessionId);
        }

        [TestMethod]
        public void PreviewSkipsAbsentAndPrunedParticipants()
        {
            var a = new Host(); var b = new Host(); var doc = Document();
            doc.Mods["mod0"] = new MissionModSettingsEntry { PlayerSettings = new[] { "obsolete" } };
            var session = new MissionModSettingsSession();
            var removed = session.Apply(doc, Participants(a, b), MissionModSettingsApplicationKind.Preview);
            CollectionAssert.AreEqual(new[] { "obsolete" }, removed["mod0"].ToArray());
            Assert.AreEqual(0, doc.Mods.Count); Assert.AreEqual(0, a.Entries + b.Entries);
            Assert.ThrowsExactly<NotSupportedException>(() => ((IDictionary<string, IReadOnlyList<string>>)removed).Clear());
        }

        [TestMethod]
        public void AuthoringCapturesBeforeExitAndMaterializedDoesNotResolveAgain()
        {
            var host = new Host(); var session = new MissionModSettingsSession(); var participants = Participants(host);
            session.Apply(Document(30), participants, MissionModSettingsApplicationKind.Playable);
            session.Apply(Document(player: true), participants, MissionModSettingsApplicationKind.Authoring);
            Assert.AreEqual(30, host.Value); Assert.IsTrue(host.Editable);
            var starting = new Dictionary<string, Dictionary<string, byte[]>> { ["mod0"] = new Dictionary<string, byte[]> { ["Value"] = MessagePackSerializer.Serialize(40) } };
            session.Apply(Document(player: true), participants, MissionModSettingsApplicationKind.AuthoringTest, startingSnapshots: starting);
            Assert.AreEqual(40, host.Value); Assert.IsFalse(host.Editable);
            session.Apply(Document("intentionally ignored"), participants, MissionModSettingsApplicationKind.Materialized);
            Assert.AreEqual(40, host.Value);
            session.Exit();
        }

        [TestMethod]
        public void MissingProviderAndInvalidFixedValuePreservePreviousContext()
        {
            var host = new Host(); var session = new MissionModSettingsSession(); var participants = Participants(host);
            session.Apply(Document(30), participants, MissionModSettingsApplicationKind.Playable);
            var doc = Document(); doc.Mods["missing"] = new MissionModSettingsEntry();
            Assert.ThrowsExactly<InvalidDataException>(() => session.Apply(doc, participants, MissionModSettingsApplicationKind.Playable));
            Assert.ThrowsExactly<FormatException>(() => session.Apply(Document("invalid"), participants, MissionModSettingsApplicationKind.Playable));
            Assert.AreEqual(30, host.Value); Assert.AreEqual(0, host.Exits);
            session.Exit();
        }

        [TestMethod]
        public void ParticipantComparerCannotSilentlyIgnoreAnOrdinallyMissingProvider()
        {
            var host = new Host(); var session = new MissionModSettingsSession();
            var participants = new Dictionary<string, IModSettingsPresetEndpoint>(Participants(host), StringComparer.OrdinalIgnoreCase);
            session.Apply(Document(30), participants, MissionModSettingsApplicationKind.Playable);
            var document = new MissionModSettingsDocument();
            document.Mods["MOD0"] = new MissionModSettingsEntry { Overrides = new Dictionary<string, object> { ["Value"] = 11 } };
            Assert.ThrowsExactly<InvalidDataException>(() => session.Apply(document, participants, MissionModSettingsApplicationKind.Playable));
            Assert.AreEqual(30, host.Value); Assert.AreEqual(0, host.Exits);
            session.Exit();
        }

        [TestMethod]
        public void DocumentComparerCannotApplyOneGuidsRulesToAnotherParticipant()
        {
            var lower = new Host(); var upper = new Host(); var session = new MissionModSettingsSession();
            var participants = new Dictionary<string, IModSettingsPresetEndpoint>(StringComparer.Ordinal) {
                ["mod0"] = lower, ["MOD0"] = upper };
            var document = new MissionModSettingsDocument { Mods = new Dictionary<string, MissionModSettingsEntry>(StringComparer.OrdinalIgnoreCase) {
                ["MOD0"] = new MissionModSettingsEntry { Overrides = new Dictionary<string, object> { ["Value"] = 11 } } } };
            session.Apply(document, participants, MissionModSettingsApplicationKind.Playable);
            Assert.AreEqual(0, lower.Value); Assert.IsFalse(lower.Explicit);
            Assert.AreEqual(11, upper.Value); Assert.IsTrue(upper.Explicit);
            session.Exit();
        }

        [TestMethod]
        public void StartingSnapshotComparerCannotSelectAnotherGuidsValues()
        {
            var lower = new Host(); var upper = new Host(); var session = new MissionModSettingsSession();
            var participants = new Dictionary<string, IModSettingsPresetEndpoint>(StringComparer.Ordinal) {
                ["mod0"] = lower, ["MOD0"] = upper };
            var starting = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase) {
                ["MOD0"] = new Dictionary<string, byte[]> { ["Value"] = MessagePackSerializer.Serialize(11) } };
            session.Apply(Document(), participants, MissionModSettingsApplicationKind.Authoring, startingSnapshots: starting);
            Assert.AreEqual(7, lower.Value); Assert.AreEqual(11, upper.Value);
            session.Exit();
        }

        [TestMethod]
        public void PartialEnterIsRolledBackAndEveryCleanupIsAttempted()
        {
            var a = new Host { FailExit = true }; var b = new Host { FailEnter = true };
            var session = new MissionModSettingsSession();
            var failure = Assert.ThrowsExactly<AggregateException>(() => session.Apply(Document(4), Participants(a, b), MissionModSettingsApplicationKind.Playable));
            StringAssert.Contains(failure.Flatten().ToString(), "Partial enter");
            Assert.AreEqual(1, a.Exits); Assert.AreEqual(1, b.Exits);
            Assert.IsFalse(b.IsMissionPresetActive); Assert.IsFalse(b.Explicit);
            CollectionAssert.AreEqual(new[] { "mod0" }, session.ActiveModGuids.ToArray());
            Assert.ThrowsExactly<InvalidOperationException>(() => session.Apply(Document(), Participants(a), MissionModSettingsApplicationKind.Authoring));
            Assert.AreEqual(1, a.Entries);
            a.FailExit = false; session.Exit(); Assert.AreEqual(0, session.ActiveModGuids.Count);
        }

        [TestMethod]
        public void RequiredPreparationVetoAndReentrantCallCannotEnterEndpoints()
        {
            var host = new Host(); var session = new MissionModSettingsSession(); var participants = Participants(host); int calls = 0;
            Assert.ThrowsExactly<InvalidOperationException>(() => session.Apply(Document(), participants, MissionModSettingsApplicationKind.Playable,
                beforeApply: _ => { calls++; session.Exit(); }));
            Assert.AreEqual(1, calls); Assert.AreEqual(0, host.Entries); Assert.AreEqual(0, session.ActiveModGuids.Count);
        }

        [TestMethod]
        public void ExitUsesOwnedEndpointAfterTheCallerReplacesDiscovery()
        {
            var host = new Host(); var participants = Participants(host); var session = new MissionModSettingsSession();
            session.Apply(Document(8), participants, MissionModSettingsApplicationKind.Editable);
            Assert.IsTrue(host.Editable); participants["mod0"] = new Host();
            session.Exit(); Assert.AreEqual(1, host.Exits); Assert.IsFalse(host.IsMissionPresetActive);
            session.Exit(); Assert.AreEqual(1, host.Exits);
        }

        [TestMethod]
        public void CreatorRulesIgnoreUnknownValuesAndContainFailures()
        {
            var host = new Host(); var entry = new MissionModSettingsEntry { PlayerSettings = new[] { "Value" } };
            entry.Overrides["Value"] = "invalid"; entry.Overrides["unknown"] = null;
            int errors = 0;
            var rules = MissionModSettingsCreatorRules.Capture(host, entry, (_, error) => { errors++; throw new Exception("Diagnostic failure"); });
            Assert.AreEqual(1, errors); Assert.AreEqual(1, rules["Value"].Mode); Assert.AreEqual(1, rules.Count);
            entry.Overrides["Value"] = 8L;
            rules = MissionModSettingsCreatorRules.Capture(host, entry);
            Assert.AreEqual(2, rules["Value"].Mode); Assert.AreEqual(8, MessagePackSerializer.Deserialize<int>(rules["Value"].FixedValue));
        }

        [TestMethod]
        public void RestartSnapshotWinsExceptWhenValuesAreAlreadyMaterialized()
        {
            var names = new[] { "resume", "resumeContext", "contextId", "consumed", "preparedSources" };
            var fields = names.Select(name => typeof(ModSettingsApplication).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)).ToArray();
            var saved = fields.Select(field => field.GetValue(null)).ToArray();
            try
            {
                var bytes = MessagePackSerializer.Serialize(99);
                fields[0].SetValue(null, new Dictionary<string, Dictionary<string, byte[]>> { ["mod0"] = new Dictionary<string, byte[]> { ["Value"] = bytes } });
                fields[1].SetValue(null, "test|same"); fields[2].SetValue(null, "test|same");
                fields[3].SetValue(null, false); fields[4].SetValue(null, null);
                var session = new MissionModSettingsSession(); var host = new Host();
                session.Apply(Document(11), Participants(host), MissionModSettingsApplicationKind.Playable);
                Assert.AreEqual(99, host.Value);
                host.Value = 33;
                session.Apply(Document(11), Participants(host), MissionModSettingsApplicationKind.Materialized);
                Assert.AreEqual(33, host.Value); Assert.AreEqual(99, MessagePackSerializer.Deserialize<int>(bytes));
                session.Exit();
            }
            finally { for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, saved[i]); }
        }

        [TestMethod]
        public void SessionAdoptionRequiresSuccessfulPreparation()
        {
            var session = new MissionModSettingsSession();
            Assert.ThrowsExactly<InvalidOperationException>(() => session.ConfirmStarted(1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => session.ConfirmStarted(0));
        }
    }
}
