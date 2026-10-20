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

        private sealed class BackendSettings : PresetLobbyModSettingsViewModel
        {
            internal readonly BackendValues Backend = new BackendValues();
            protected override IDynamicPresetSettingsProvider DynamicSettingsProvider => Backend;
        }
        private sealed class BackendValues : IDynamicPresetSettingsProvider, IModSettingsApplicationBackend
        {
            internal int FailReplacements;
            internal Dictionary<string, object> Own = new Dictionary<string, object> { ["Value"] = 7, ["Other"] = 8 };
            internal Dictionary<string, object> Desired = new Dictionary<string, object> { ["Value"] = 7, ["Other"] = 8 };
            public IReadOnlyList<DynamicPresetSetting> GetSettings() => Own.Keys.Select(key => new DynamicPresetSetting {
                Key = key, ValueType = typeof(int), DefaultValue = 0, Scope = PresetSettingScope.Host }).ToArray();
            public Dictionary<string, object> ReadValues() => ReadDesiredValues();
            public object ReadValue(string key) => Desired[key];
            public void ValidateValues(Dictionary<string, object> values) { if (values.Count != 2 || values.Values.Any(value => !(value is int))) throw new InvalidDataException(); }
            public void ReplaceValues(Dictionary<string, object> values)
            {
                if (FailReplacements > 0) { FailReplacements--; throw new InvalidOperationException("Injected restore failure"); }
                ValidateValues(values); Desired = new Dictionary<string, object>(values);
            }
            public Dictionary<string, object> ReadDesiredValues() => new Dictionary<string, object>(Desired);
            public void ReplaceDesiredValues(Dictionary<string, object> values) => ReplaceValues(values);
            public Dictionary<string, object> ReadActiveValues() => ReadOwnValues();
            public Dictionary<string, object> ReadOwnValues() => new Dictionary<string, object>(Own);
            public Dictionary<string, object> ReadPendingValues() => null;
            public string ActiveContextId => "";
            public void StageValues(Dictionary<string, object> values, string contextId) => throw new Exception("Must not stage");
            public void ApplyValues(Dictionary<string, object> values, string contextId) => throw new Exception("Must not apply");
            public void ReturnToOwnConfiguration() => Desired = ReadOwnValues();
            public void DiscardPendingConfiguration() => throw new Exception("Must not discard");
        }
    }
}
