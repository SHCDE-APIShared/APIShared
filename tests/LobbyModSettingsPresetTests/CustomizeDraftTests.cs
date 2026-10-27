using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API.Components.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LobbyModSettingsPresetTests
{
    public partial class PresetTests
    {
        private sealed class DraftSettings : PresetLobbyModSettingsViewModel
        {
            private bool enabled = true;
            private int first = 5, second = 7, local = 9;
            private int[] values = new[] { 1, 2 };
            [SyncHostOnly] public bool EnableMod { get => enabled; set { enabled = value; OnPropertyChanged(nameof(EnableMod)); } }
            [SyncHostOnly] public int First { get => first; set { first = value; OnPropertyChanged(nameof(First)); } }
            [SyncHostOnly] public int Second { get => second; set { second = value; OnPropertyChanged(nameof(Second)); } }
            [PresetLocal] public int Local { get => local; set { local = value; OnPropertyChanged(nameof(Local)); } }
            [PresetLocal] public int[] Values { get => values; set { values = value; OnPropertyChanged(nameof(Values)); } }
        }

        [TestMethod]
        public void PersonalResetKeepsMapAndTrailContextFilesAndArrayOwnership()
        {
            foreach (string label in new[] { "Map", "Trail" })
            {
                var model = new DraftSettings();
                string assembly = Path.Combine(testRoot, label + "Personal.dll");
                string guid = "Foreign.Personal." + label;
                model.PreparePresets(null, assembly, label, guid, new Version(1, 0));
                model.ActivatePresets();
                model.First = 23; model.Second = 42; model.Local = 11;
                model.Values = new[] { 17, 18 };
                byte[] file = File.ReadAllBytes(new PersonalSettingsPaths(assembly, guid).SettingsFile);
                var source = model.System_CreatePlayerMissionPresetSnapshot();
                source[nameof(model.First)] = MessagePackSerializer.Serialize(91);
                source[nameof(model.Values)] = MessagePackSerializer.Serialize(new[] { 31, 32 });
                model.System_EnterMissionPreset(source, label, true);
                model.System_ImportPresetJson(ModSettingsPresetJson.Serialize(guid, "different", "Different", "", "", "",
                    new Dictionary<string, PublishedPresetSetting> {
                        [nameof(model.First)] = new PublishedPresetSetting { Mode = PublishedPresetValueMode.Fixed, Value = 77L } }));
                model.System_TestLoadPreset(model.System_TestPublishedPresets.Single(p => p.Id == "different").StableId);
                Assert(model.First == 77, "Preset did not replace the source value.");
                var personal = model.System_CreatePlayerMissionPresetSnapshot();
                model.System_ApplyMissionPresetSnapshot(personal, "Personal Settings");
                Assert(model.First == 23 && model.Second == 42 && model.Local == 11 && model.MissionPresetEditable && model.IsMissionPresetActive,
                    "Personal reset read the current mission/preset or exited the editable context.");
                CollectionAssert.AreEqual(new[] { 17, 18 }, model.Values);
                personal[nameof(model.Values)][0] ^= 1;
                model.Values[0] = 999;
                CollectionAssert.AreEqual(new[] { 17, 18 }, MessagePackSerializer.Deserialize<int[]>(model.System_CreatePlayerMissionPresetSnapshot()[nameof(model.Values)]));
                model.System_ApplyMissionPresetSnapshot(source, label);
                Assert(model.First == 91 && model.System_TestPresetStatusText == label, "Personal reset destroyed the original mission source.");
                CollectionAssert.AreEqual(file, File.ReadAllBytes(new PersonalSettingsPaths(assembly, guid).SettingsFile));
                model.System_ExitMissionPreset();
                Assert(model.First == 23 && model.Local == 11, "Exit did not restore personal settings.");
                CollectionAssert.AreEqual(new[] { 17, 18 }, model.Values);
                CollectionAssert.AreEqual(file, File.ReadAllBytes(new PersonalSettingsPaths(assembly, guid).SettingsFile));
            }
        }

        [TestMethod]
        public void CustomizeAndDirectResolveTheSameValuesAndMaterializedLaunchKeepsAllDraftScopes()
        {
            var model = new DraftSettings();
            string assembly = Path.Combine(testRoot, "Draft.dll");
            const string guid = "Foreign.Author.Draft";
            model.PreparePresets(null, assembly, "Draft", guid, new Version(1, 0));
            model.ActivatePresets();
            model.First = 23; model.Second = 42; model.Local = 11;
            string path = new PersonalSettingsPaths(assembly, guid).SettingsFile;
            byte[] personal = File.ReadAllBytes(path);
            var document = new MissionModSettingsDocument();
            document.Mods[guid] = new MissionModSettingsEntry {
                PlayerSettings = new[] { nameof(model.First) },
                Overrides = new Dictionary<string, object> { [nameof(model.EnableMod)] = true, [nameof(model.Second)] = 91 }
            };
            var endpoints = new Dictionary<string, IModSettingsPresetEndpoint> { [guid] = model };
            var session = new MissionModSettingsSession();
            session.Apply(document, endpoints, MissionModSettingsApplicationKind.Playable);
            Assert(model.EnableMod && model.First == 23 && model.Second == 91 && model.Local == 11, "Direct resolution changed.");
            session.Apply(document, endpoints, MissionModSettingsApplicationKind.Editable);
            Assert(model.EnableMod && model.First == 23 && model.Second == 91 && model.Local == 11, "Customize differs from direct start.");
            model.First = 77; model.Second = 88; model.Local = 99;
            session.Apply(document, endpoints, MissionModSettingsApplicationKind.Materialized);
            Assert(model.First == 77 && model.Second == 88 && model.Local == 99, "Start resolved the original rules again or lost the local draft.");
            Assert(!model.MissionPresetEditable, "Started draft remained editable.");
            CollectionAssert.AreEqual(personal, File.ReadAllBytes(path));
            session.Exit();
            Assert(model.EnableMod && model.First == 23 && model.Second == 42 && model.Local == 11, "Personal values were not restored.");
            CollectionAssert.AreEqual(personal, File.ReadAllBytes(path));
        }

        [TestMethod]
        public void ClientLaunchRetainsLateHostValuesAndItsPersonalFile()
        {
            var model = new DraftSettings();
            string assembly = Path.Combine(testRoot, "ClientDraft.dll");
            const string guid = "Foreign.ClientDraft";
            model.PreparePresets(null, assembly, "ClientDraft", guid, new Version(1, 0));
            model.ActivatePresets();
            model.First = 23; model.Second = 42; model.Local = 11;
            byte[] personal = File.ReadAllBytes(new PersonalSettingsPaths(assembly, guid).SettingsFile);
            typeof(PresetLobbyModSettingsViewModel).GetField("isLocalHost", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(model, false);
            model.System_TestPersonalHostRestoreAllowed = () => false;
            var document = new MissionModSettingsDocument();
            document.Mods[guid] = new MissionModSettingsEntry { PlayerSettings = new[] { nameof(model.First) } };
            var endpoints = new Dictionary<string, IModSettingsPresetEndpoint> { [guid] = model };
            var session = new MissionModSettingsSession();
            session.Apply(document, endpoints, MissionModSettingsApplicationKind.Editable);
            SetNetworkSyncInProgress(true);
            try { model.First = 771; model.Second = 882; }
            finally { SetNetworkSyncInProgress(false); }
            session.Apply(document, endpoints, MissionModSettingsApplicationKind.Materialized);
            Assert(model.First == 771 && model.Second == 882, "Client start replaced late authenticated host values with its own configuration.");
            session.Exit();
            Assert(model.First == 771 && model.Second == 882 && model.Local == 11, "An active client lobby lost its host state on exit.");
            CollectionAssert.AreEqual(personal, File.ReadAllBytes(new PersonalSettingsPaths(assembly, guid).SettingsFile));
        }

        [TestMethod]
        public void EmptyTrailDisablesGameplayIdenticallyForDirectAndCustomize()
        {
            var model = new DraftSettings();
            model.PreparePresets(null, Path.Combine(testRoot, "EmptyDraft.dll"), "EmptyDraft", "Foreign.Empty", new Version(1, 0));
            model.ActivatePresets();
            model.First = 23;
            var session = new MissionModSettingsSession();
            var document = MissionModSettingsDocument.CreateModDefaults();
            var endpoints = new Dictionary<string, IModSettingsPresetEndpoint> { ["Foreign.Empty"] = model };
            foreach (var kind in new[] { MissionModSettingsApplicationKind.Playable, MissionModSettingsApplicationKind.Editable })
            {
                session.Apply(document, endpoints, kind);
                Assert(!model.EnableMod && model.First == 5 && model.Second == 7 && model.Local == 9, "Omitted values leaked personal host settings.");
            }
            session.Exit();
            Assert(model.EnableMod && model.First == 23, "Empty Trail replaced the personal state.");
        }
    }
}
