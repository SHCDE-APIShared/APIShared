using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;

namespace LobbyModSettingsPresetTests
{
    public partial class PresetTests
    {
        [TestMethod]
        public void MissionSessionPreservesPersonalStorageThroughAuthoringTestAndExit()
        {
            var settings = StartDocuments();
            settings.Number = 23;
            byte[] personal = File.ReadAllBytes(NewFile);
            var participants = new Dictionary<string, IModSettingsPresetEndpoint> { [TargetGuid] = settings };
            var document = new MissionModSettingsDocument();
            document.Mods[TargetGuid] = new MissionModSettingsEntry {
                Overrides = new Dictionary<string, object> { [nameof(settings.Number)] = 91L } };
            var session = new MissionModSettingsSession();
            session.Apply(document, participants, MissionModSettingsApplicationKind.Playable);
            Assert(settings.Number == 91 && !settings.EnableMod, "Playable values did not use the disabled baseline and fixed author value.");
            CollectionAssert.AreEqual(personal, File.ReadAllBytes(NewFile));
            document.Mods[TargetGuid].Overrides.Clear();
            document.Mods[TargetGuid].PlayerSettings = new[] { nameof(settings.Number) };
            session.Apply(document, participants, MissionModSettingsApplicationKind.Authoring);
            Assert(settings.Number == 91, "Authoring lost the current working values across exit.");
            settings.Number = 92;
            session.Apply(document, participants, MissionModSettingsApplicationKind.AuthoringTest);
            Assert(settings.Number == 92 && settings.IsMissionPresetActive, "Test launch did not preserve the authoring draft.");
            session.Exit();
            Assert(settings.Number == 23 && settings.EnableMod && !settings.IsMissionPresetActive, "Exit did not restore personal settings.");
            CollectionAssert.AreEqual(personal, File.ReadAllBytes(NewFile));
            Assert(StartDocuments().Number == 23, "Re-reading personal storage found mission values.");
        }

        [TestMethod]
        public void MissionSessionResolvesBackendPlayerValuesFromOwnConfiguration()
        {
            var settings = new BackendSettings();
            settings.PreparePresets(null, Path.Combine(testRoot, "Backend.dll"), "Backend", "Tests.MissionBackend", new Version(1, 0));
            settings.LoadInitialPresetSettings(); settings.ActivatePresets();
            settings.Backend.Desired = new Dictionary<string, object> { ["Value"] = 40, ["Other"] = 41 };
            var document = new MissionModSettingsDocument();
            document.Mods["backend"] = new MissionModSettingsEntry { PlayerSettings = new[] { "Value" } };
            var session = new MissionModSettingsSession();
            session.Apply(document, new Dictionary<string, IModSettingsPresetEndpoint> { ["backend"] = settings }, MissionModSettingsApplicationKind.Authoring);
            Assert((int)settings.Backend.Desired["Value"] == 7, "Backend Player values did not use refreshed own values.");
            Assert((int)settings.Backend.Desired["Other"] == 41, "Authoring baseline lost the current backend value.");
            Assert((int)settings.Backend.Own["Value"] == 7 && (int)settings.Backend.Own["Other"] == 8, "Preparation mutated personal backend values.");
            session.Exit();
        }

        [TestMethod]
        public void FailedPersonalRestoreKeepsTheEndpointReachableForRetry()
        {
            var settings = new BackendSettings();
            settings.PreparePresets(null, Path.Combine(testRoot, "BackendRetry.dll"), "BackendRetry", "Tests.BackendRetry", new Version(1, 0));
            settings.LoadInitialPresetSettings(); settings.ActivatePresets();
            var document = new MissionModSettingsDocument();
            document.Mods["backend"] = new MissionModSettingsEntry { Overrides = new Dictionary<string, object> { ["Value"] = 44 } };
            var session = new MissionModSettingsSession();
            session.Apply(document, new Dictionary<string, IModSettingsPresetEndpoint> { ["backend"] = settings }, MissionModSettingsApplicationKind.Playable);
            settings.Backend.FailReplacements = 1;
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.ThrowsExactly<AggregateException>(() => session.Exit());
            Assert(settings.IsMissionPresetActive, "Failed restore made the endpoint's retry a no-op.");
            Assert(session.ActiveModGuids.Count == 1, "Failed cleanup owner was lost.");
            session.Exit();
            Assert(!settings.IsMissionPresetActive && session.ActiveModGuids.Count == 0, "Retry did not finish cleanup.");
            Assert((int)settings.Backend.Desired["Value"] == 7, "Retry did not restore the personal value.");
        }


        [TestMethod]
        public void IndependentBackendValuesPersistWithoutCommittingMissionHostAndRollbackFailures()
        {
            var settings = new BackendSettings(); settings.Backend.PersonalOther = settings.Backend.ReturnLiveDesired = true;
            settings.PreparePresets(null, Path.Combine(testRoot, "PersonalBackend.dll"), "PersonalBackend", "Tests.PersonalBackend", new Version(1, 0));
            settings.ActivatePresets();
            var session = new MissionModSettingsSession();
            var doc = new MissionModSettingsDocument();
            doc.Mods["backend"] = new MissionModSettingsEntry { Overrides = new Dictionary<string, object> { ["Value"] = 44, ["Other"] = 999 } };
            session.Apply(doc, new Dictionary<string, IModSettingsPresetEndpoint> { ["backend"] = settings }, MissionModSettingsApplicationKind.Editable);
            Assert(!doc.Mods["backend"].Overrides.ContainsKey("Other"), "Independent backend property remained authorable.");
            var working = settings.System_CreateCurrentWorkingSnapshot();
            working["Other"] = MessagePack.MessagePackSerializer.Serialize(99);
            settings.System_ApplyWorkingSnapshot(working);
            Assert((int)settings.Backend.Own["Value"] == 7 && (int)settings.Backend.Own["Other"] == 99,
                "Personal commit leaked mission host values.");
            settings.Backend.FailPersonalPersistence = true;
            working["Other"] = MessagePack.MessagePackSerializer.Serialize(111);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.ThrowsExactly<IOException>(() => settings.System_ApplyWorkingSnapshot(working));
            Assert((int)settings.Backend.Desired["Value"] == 44 && (int)settings.Backend.Desired["Other"] == 99 && (int)settings.Backend.Own["Other"] == 99,
                "Failed personal commit lost a prior value or changed the mission host.");
            settings.Backend.FailPersonalPersistence = false;
            settings.Backend.RejectIntermediate = true;
            working["Value"] = MessagePack.MessagePackSerializer.Serialize(55);
            working["Other"] = MessagePack.MessagePackSerializer.Serialize(99);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.ThrowsExactly<InvalidDataException>(() => settings.System_ApplyWorkingSnapshot(working));
            Assert((int)settings.Backend.Desired["Value"] == 44 && (int)settings.Backend.Desired["Other"] == 99,
                "Validation mutated a live backend reader dictionary before accepting the complete snapshot.");
            working["Other"] = MessagePack.MessagePackSerializer.Serialize(66);
            settings.System_ApplyWorkingSnapshot(working);
            Assert((int)settings.Backend.Desired["Value"] == 55 && (int)settings.Backend.Desired["Other"] == 66,
                "A valid complete snapshot was split into invalid intermediate configurations.");
            session.Exit();
            Assert((int)settings.Backend.Desired["Value"] == 7 && (int)settings.Backend.Desired["Other"] == 66,
                "Mission exit reverted the personal backend value.");
        }

        [TestMethod]
        public void PersonalBackendCommitRebasesRestartFingerprintWithoutReplacingThePreparedHostSnapshot()
        {
            const string id = "Tests.PersonalJournal";
            var settings = new BackendSettings(); settings.Backend.PersonalOther = true;
            string assembly = Path.Combine(testRoot, "JournalBackend.dll");
            settings.PreparePresets(null, assembly, "JournalBackend", id, new Version(1, 0)); settings.ActivatePresets();
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(ModSettingsApplication);
            var fields = type.GetFields(flags).Where(field => !field.IsInitOnly).ToArray();
            var previous = fields.ToDictionary(field => field, field => field.GetValue(null));
            var endpoints = (Dictionary<string, PresetLobbyModSettingsViewModel>)type.GetField("endpoints", flags).GetValue(null);
            var originalEndpoints = new Dictionary<string, PresetLobbyModSettingsViewModel>(endpoints);
            try
            {
                endpoints.Clear();
                type.GetField("journalPath", flags).SetValue(null, Path.Combine(testRoot, "personal-restart.json"));
                type.GetField("contextId", flags).SetValue(null, "mission|personal-journal");
                type.GetField("resume", flags).SetValue(null, null);
                type.GetField("personalFingerprints", flags).SetValue(null, null);
                ModSettingsApplication.Register(id, settings, assembly);
                var snapshot = settings.System_CreateCurrentWorkingSnapshot(); snapshot["Value"] = MessagePack.MessagePackSerializer.Serialize(44);
                settings.System_EnterMissionPreset(snapshot, "Trail", true);
                type.GetMethod("SavePreparation", flags).Invoke(null, null);
                var prepared = ModSettingsApplication.ResumeSnapshot(id);
                settings.Backend.Own["Value"] = 123;
                settings.Backend.Desired["Other"] = 91;
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.ThrowsExactly<InvalidDataException>(() => settings.NotifyPersonalOther());
                Assert((int)settings.Backend.Own["Other"] == 8, "A direct property notification masked an external Host baseline change.");
                settings.Backend.Own["Value"] = 7;
                settings.Backend.Desired["Other"] = 8;
                snapshot["Other"] = MessagePack.MessagePackSerializer.Serialize(99);
                settings.System_ApplyWorkingSnapshot(snapshot);
                ModSettingsApplication.EnterContext("mission|personal-journal");
                var resumed = ModSettingsApplication.ResumeSnapshot(id);
                Microsoft.VisualStudio.TestTools.UnitTesting.CollectionAssert.AreEqual(prepared["Value"], resumed["Value"]);
                Assert((int)settings.Backend.Own["Value"] == 7 && (int)settings.Backend.Own["Other"] == 99,
                    "Rebasing restart provenance changed personal Host state.");
                type.GetField("resume", flags).SetValue(null, null);
                type.GetField("personalFingerprints", flags).SetValue(null, null);
                ModSettingsApplication.EnterContext("mission|personal-journal");
                Assert(ModSettingsApplication.ResumeSnapshot(id) != null, "Updated journal could not be read after process restart.");
                settings.System_ExitMissionPreset();
            }
            finally
            {
                endpoints.Clear(); foreach (var entry in originalEndpoints) endpoints.Add(entry.Key, entry.Value);
                foreach (var field in previous) field.Key.SetValue(null, field.Value);
            }
        }

        private sealed class LegacyPersonalBackend : IDynamicPresetSettingsProvider, IModSettingsApplicationBackend
        {
            internal int Replacements;
            private readonly BackendValues inner = new BackendValues { PersonalOther = true };
            public IReadOnlyList<DynamicPresetSetting> GetSettings() => inner.GetSettings();
            public Dictionary<string, object> ReadValues() => inner.ReadValues();
            public object ReadValue(string key) => inner.ReadValue(key);
            public void ValidateValues(Dictionary<string, object> values) => inner.ValidateValues(values);
            public void ReplaceValues(Dictionary<string, object> values) { Replacements++; inner.ReplaceValues(values); }
            public Dictionary<string, object> ReadDesiredValues() => inner.ReadDesiredValues();
            public void ReplaceDesiredValues(Dictionary<string, object> values) => ReplaceValues(values);
            public Dictionary<string, object> ReadActiveValues() => inner.ReadActiveValues();
            public Dictionary<string, object> ReadOwnValues() => inner.ReadOwnValues();
            public Dictionary<string, object> ReadPendingValues() => null;
            public string ActiveContextId => "";
            public void StageValues(Dictionary<string, object> values, string contextId) => throw new Exception("Must not stage");
            public void ApplyValues(Dictionary<string, object> values, string contextId) => throw new Exception("Must not apply");
            public void ReturnToOwnConfiguration() => throw new Exception("Must not restore");
            public void DiscardPendingConfiguration() => throw new Exception("Must not discard");
        }
        private sealed class LegacyPersonalBackendSettings : PresetLobbyModSettingsViewModel
        {
            internal readonly LegacyPersonalBackend Backend = new LegacyPersonalBackend();
            protected override IDynamicPresetSettingsProvider DynamicSettingsProvider => Backend;
        }
        [TestMethod]
        public void PersonalBackendWithoutSeparatedPersistenceIsRejectedBeforeAnyReplacement()
        {
            var settings = new LegacyPersonalBackendSettings();
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.ThrowsExactly<InvalidOperationException>(() => settings.PreparePresets(
                null, Path.Combine(testRoot, "LegacyPersonal.dll"), "LegacyPersonal", "Tests.LegacyPersonal", new Version(1, 0)));
            Assert(settings.Backend.Replacements == 0, "An unsupported personal backend was mutated before admission failed.");
        }

        [TestMethod]
        public void PersonalRestartSettingPersistsWithoutPretendingItWasAppliedLive()
        {
            var settings = new BackendSettings(); settings.Backend.PersonalOther = settings.Backend.RestartPersonal = true;
            settings.PreparePresets(null, Path.Combine(testRoot, "RestartPersonal.dll"), "RestartPersonal", "Tests.RestartPersonal", new Version(1, 0));
            settings.ActivatePresets();
            var working = settings.System_CreateCurrentWorkingSnapshot(); working["Other"] = MessagePack.MessagePackSerializer.Serialize(99);
            settings.System_ApplyWorkingSnapshot(working);
            Assert((int)settings.Backend.Own["Other"] == 99 && (int)settings.Backend.ReadActiveValues()["Other"] == 8,
                "A restart-only personal setting was not stored separately from active state.");
            Assert(settings.System_ConfigurationNeedsRestart(), "Personal persistence bypassed RequiresRestart.");
        }

        [TestMethod]
        public void DirectBackendApplicationPersistsCurrentPersonalValuesBeforeApplying()
        {
            var settings = new BackendSettings(); settings.Backend.PersonalOther = settings.Backend.AllowApplication = true;
            settings.PreparePresets(null, Path.Combine(testRoot, "DirectPersonal.dll"), "DirectPersonal", "Tests.DirectPersonal", new Version(1, 0));
            settings.ActivatePresets();
            settings.Backend.Desired["Other"] = 99;
            Assert(!settings.System_ApplyConfiguration("mission"), "A live-only personal change requested a restart.");
            Assert((int)settings.Backend.Own["Other"] == 99 && (int)settings.Backend.ReadActiveValues()["Other"] == 99 && (int)settings.Backend.Own["Value"] == 7,
                "Direct backend application skipped separated personal persistence.");
        }

        private sealed class BackendSettings : PresetLobbyModSettingsViewModel
        {
            internal readonly BackendValues Backend = new BackendValues();
            internal void NotifyPersonalOther() => OnPropertyChanged("Other");
            protected override IDynamicPresetSettingsProvider DynamicSettingsProvider => Backend;
        }
        private sealed class BackendValues : IDynamicPresetSettingsProvider, IModSettingsApplicationBackend, IModSettingsPersonalPersistenceBackend
        {
            internal int FailReplacements;
            internal bool PersonalOther, FailPersonalPersistence, RejectIntermediate, RestartPersonal, AllowApplication, ReturnLiveDesired;
            private readonly Dictionary<string, object> active = new Dictionary<string, object> { ["Value"] = 7, ["Other"] = 8 };
            public void PersistPersonalValues(Dictionary<string, object> values)
            {
                if (FailPersonalPersistence) throw new IOException("Injected personal storage failure");
                if (values.Keys.Any(key => key != "Other")) throw new Exception("Host value leaked into personal persistence");
                foreach (var value in values) Own[value.Key] = value.Value;
            }
            internal Dictionary<string, object> Own = new Dictionary<string, object> { ["Value"] = 7, ["Other"] = 8 };
            internal Dictionary<string, object> Desired = new Dictionary<string, object> { ["Value"] = 7, ["Other"] = 8 };
            public IReadOnlyList<DynamicPresetSetting> GetSettings() => Own.Keys.Select(key => new DynamicPresetSetting {
                Key = key, ValueType = typeof(int), DefaultValue = 0, Scope = PresetSettingScope.Host, MissionIndependent = PersonalOther && key == "Other", RequiresRestart = RestartPersonal && key == "Other" }).ToArray();
            public Dictionary<string, object> ReadValues() => ReadDesiredValues();
            public object ReadValue(string key) => Desired[key];
            public void ValidateValues(Dictionary<string, object> values) { if (values.Count != 2 || values.Values.Any(value => !(value is int)) ||
                RejectIntermediate && (int)values["Value"] == 55 && (int)values["Other"] != 66) throw new InvalidDataException(); }
            public void ReplaceValues(Dictionary<string, object> values)
            {
                if (FailReplacements > 0) { FailReplacements--; throw new InvalidOperationException("Injected restore failure"); }
                ValidateValues(values); Desired = new Dictionary<string, object>(values);
            }
            public Dictionary<string, object> ReadDesiredValues() => ReturnLiveDesired ? Desired : new Dictionary<string, object>(Desired);
            public void ReplaceDesiredValues(Dictionary<string, object> values) => ReplaceValues(values);
            public Dictionary<string, object> ReadActiveValues() => new Dictionary<string, object>(active);
            public Dictionary<string, object> ReadOwnValues() => new Dictionary<string, object>(Own);
            public Dictionary<string, object> ReadPendingValues() => null;
            public string ActiveContextId => "";
            public void StageValues(Dictionary<string, object> values, string contextId) => throw new Exception("Must not stage");
            public void ApplyValues(Dictionary<string, object> values, string contextId)
            {
                if (!AllowApplication) throw new Exception("Must not apply");
                active.Clear(); foreach (var value in values) active.Add(value.Key, value.Value);
            }
            public void ReturnToOwnConfiguration() => Desired = ReadOwnValues();
            public void DiscardPendingConfiguration() => throw new Exception("Must not discard");
        }
    }
}
