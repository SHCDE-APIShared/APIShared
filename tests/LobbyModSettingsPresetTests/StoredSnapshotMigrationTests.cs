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
    public partial class LobbyPresetControllerTests
    {
        // Exercises the actual controller with a legacy packed setting split into two fields.
        // BuildingCosts separately tests its production codec; no Unity/native state is needed here.
        private sealed class MigratingSettings : IModSettingsStoredSnapshotMigration, IModSettingsSnapshotMigration
        {
            private string baseCosts = "modern", humanCosts = "0";
            internal Action<string> Changed;
            internal int Calls, MissionCalls, Setters, ThrowOnCall;
            internal bool ThrowAlways, MutateInput, RemoveInput, ReturnNull, AddUnknown;
            [SyncHostOnly, ModSettingRenamedFrom("OldCosts")] public string BuildingCosts { get => baseCosts; set { Setters++; baseCosts = value; Changed?.Invoke(nameof(BuildingCosts)); } }
            [SyncHostOnly] public string HumanExtraBuildingCosts { get => humanCosts; set { Setters++; humanCosts = value; Changed?.Invoke(nameof(HumanExtraBuildingCosts)); } }
            public Dictionary<string, byte[]> System_MigrateStoredSnapshot(Dictionary<string, byte[]> raw)
            {
                Calls++;
                if (MutateInput && raw.TryGetValue(nameof(BuildingCosts), out var mutable) && mutable != null) mutable[0] ^= 1;
                if (ThrowAlways || Calls == ThrowOnCall) throw new InvalidOperationException("injected migration failure");
                if (ReturnNull) return null;
                if (RemoveInput) { raw.Remove(nameof(BuildingCosts)); return raw; }
                if (AddUnknown) raw["UnknownProperty"] = MessagePackSerializer.Serialize(1);
                if (!raw.ContainsKey(nameof(HumanExtraBuildingCosts)) && raw.TryGetValue(nameof(BuildingCosts), out var bytes) && bytes != null)
                {
                    string value = MessagePackSerializer.Deserialize<string>(bytes);
                    if (value?.StartsWith("legacy:", StringComparison.Ordinal) == true)
                        raw[nameof(HumanExtraBuildingCosts)] = MessagePackSerializer.Serialize(value.Substring(7));
                }
                return raw;
            }
            public Dictionary<string, byte[]> System_MigrateMissionSnapshot(Dictionary<string, byte[]> raw)
            { MissionCalls++; return raw; }
        }

        private LobbyPresetController PrepareMigration(MigratingSettings values, out Host host)
        {
            host = new Host { SettingsTargetOverride = values };
            var controller = new LobbyPresetController(host, null, Path.Combine(directory, "Example.dll"),
                Name, "Foreign.Author.Example", new Version(1, 0), false);
            values.Changed = controller.AfterPropertyChanged;
            controller.CaptureDefaults();
            return controller;
        }

        private static Dictionary<string, byte[]> LegacyCosts(string amount) => new Dictionary<string, byte[]> {
            [nameof(MigratingSettings.BuildingCosts)] = MessagePackSerializer.Serialize("legacy:" + amount)
        };

        [TestMethod]
        [DataRow(1)] [DataRow(2)] [DataRow(3)]
        public void StoredMigrationRunsBeforeDefaultsAndLegacyExports(int schema)
        {
            var values = new MigratingSettings();
            var controller = PrepareMigration(values, out _);
            var payload = LegacyCosts("5"); // preliminary top-level load differs from selected nested state
            payload[LobbyPresetController.SchemaVersionKey] = MessagePackSerializer.Serialize(schema);
            payload[LobbyPresetController.LegacyPresetImportCompletedKey] = MessagePackSerializer.Serialize(true);
            if (schema == 3) payload[LobbyPresetController.CurrentSettingsKey] = MessagePackSerializer.Serialize(LegacyCosts("123"));
            else
            {
                payload[LobbyPresetController.ActivePresetKey] = MessagePackSerializer.Serialize(schema == 2 ? 1 : 0);
                payload[LobbyPresetController.Preset1Key] = MessagePackSerializer.Serialize(LegacyCosts(schema == 2 ? "7" : "123"));
                payload[LobbyPresetController.Preset2Key] = MessagePackSerializer.Serialize(LegacyCosts("123"));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllBytes(SettingsPath, MessagePackSerializer.Serialize(payload));
            controller.LoadInitialSettings();
            Assert.AreEqual("5", values.HumanExtraBuildingCosts);
            controller.Activate();
            Assert.AreEqual("123", values.HumanExtraBuildingCosts);
            var stored = MessagePackSerializer.Deserialize<Dictionary<string, byte[]>>(Read()[LobbyPresetController.CurrentSettingsKey]);
            Assert.AreEqual("123", MessagePackSerializer.Deserialize<string>(stored[nameof(MigratingSettings.HumanExtraBuildingCosts)]));
            Assert.AreEqual(0, values.MissionCalls, "The pre-default hook invoked the existing mission converter.");
            if (schema < 3)
                Assert.IsTrue(controller.PublishedPresets.Where(p => p.Id.StartsWith("legacy-preset-", StringComparison.Ordinal))
                    .All(p => p.Settings.ContainsKey(nameof(MigratingSettings.HumanExtraBuildingCosts))), "Old slots were exported before conversion.");
        }

        [TestMethod]
        public void RawMigrationKeepsExplicitNullEmptyAndSelectedDefaultAndDetachesBuffers()
        {
            var values = new MigratingSettings();
            foreach (byte[] explicitValue in new[] { null, MessagePackSerializer.Serialize<string>(null), MessagePackSerializer.Serialize(""), MessagePackSerializer.Serialize("0") })
            {
                var input = LegacyCosts("123"); input[nameof(MigratingSettings.HumanExtraBuildingCosts)] = explicitValue;
                var output = ModSettingsMigration.MigrateRawStoredSnapshot(values, input);
                Assert.IsTrue(output.ContainsKey(nameof(MigratingSettings.HumanExtraBuildingCosts)));
                if (explicitValue == null) Assert.IsNull(output[nameof(MigratingSettings.HumanExtraBuildingCosts)]);
                else CollectionAssert.AreEqual(explicitValue, output[nameof(MigratingSettings.HumanExtraBuildingCosts)]);
                output[nameof(MigratingSettings.BuildingCosts)][0] ^= 1;
                Assert.AreEqual("legacy:123", MessagePackSerializer.Deserialize<string>(input[nameof(MigratingSettings.BuildingCosts)]));
            }
            var first = ModSettingsMigration.MigrateRawStoredSnapshot(values, LegacyCosts("123"));
            var twice = ModSettingsMigration.MigrateRawStoredSnapshot(values, first);
            CollectionAssert.AreEqual(first[nameof(MigratingSettings.HumanExtraBuildingCosts)], twice[nameof(MigratingSettings.HumanExtraBuildingCosts)]);
            Assert.AreEqual(0, values.MissionCalls);
            ModSettingsMigration.MigrateStoredSnapshot(values, LegacyCosts("123"));
            Assert.AreEqual(1, values.MissionCalls, "The older helper lost its existing conversion contract.");
            var untouched = LegacyCosts("8");
            Assert.AreSame(untouched, ModSettingsMigration.MigrateRawStoredSnapshot(new object(), untouched));
            var former = new Dictionary<string, byte[]> { ["OldCosts"] = MessagePackSerializer.Serialize("legacy:9") };
            var renamed = ModSettingsMigration.MigrateRawStoredSnapshot(values, former);
            Assert.IsFalse(renamed.ContainsKey("OldCosts"));
            Assert.AreEqual("9", MessagePackSerializer.Deserialize<string>(renamed[nameof(MigratingSettings.HumanExtraBuildingCosts)]));
        }

        [TestMethod]
        public void ConverterFailureDoesNotMutateCallerOrRemoveSelectedKeys()
        {
            var input = LegacyCosts("123"); byte[] before = (byte[])input[nameof(MigratingSettings.BuildingCosts)].Clone();
            Assert.ThrowsExactly<InvalidOperationException>(() => ModSettingsMigration.MigrateRawStoredSnapshot(
                new MigratingSettings { MutateInput = true, ThrowAlways = true }, input));
            CollectionAssert.AreEqual(before, input[nameof(MigratingSettings.BuildingCosts)]);
            Assert.ThrowsExactly<InvalidDataException>(() => ModSettingsMigration.MigrateRawStoredSnapshot(new MigratingSettings { RemoveInput = true }, input));
            Assert.ThrowsExactly<InvalidDataException>(() => ModSettingsMigration.MigrateRawStoredSnapshot(new MigratingSettings { ReturnNull = true }, input));
        }

        [TestMethod]
        public void FailedActivateSnapshotKeepsEarlierInitialSnapshotAndValidFile()
        {
            var values = new MigratingSettings { ThrowOnCall = 2 };
            var controller = PrepareMigration(values, out _);
            var payload = LegacyCosts("7");
            payload[LobbyPresetController.SchemaVersionKey] = MessagePackSerializer.Serialize(3);
            payload[LobbyPresetController.CurrentSettingsKey] = MessagePackSerializer.Serialize(LegacyCosts("123"));
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            byte[] original = MessagePackSerializer.Serialize(payload); File.WriteAllBytes(SettingsPath, original);
            controller.LoadInitialSettings();
            int setters = values.Setters;
            Assert.AreEqual("7", values.HumanExtraBuildingCosts);
            Assert.ThrowsExactly<InvalidOperationException>(() => controller.Activate());
            Assert.AreEqual(setters, values.Setters, "Rejected Activate snapshot reached a setter.");
            Assert.AreEqual("7", values.HumanExtraBuildingCosts, "An earlier successful snapshot was rolled back.");
            CollectionAssert.AreEqual(original, File.ReadAllBytes(SettingsPath));
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(SettingsPath), "*.corrupt-*").Length);
            values.ThrowAlways = true;
            Assert.ThrowsExactly<InvalidOperationException>(() => controller.ApplyWorkingSnapshot(LegacyCosts("99")));
            Assert.AreEqual(setters, values.Setters);
        }

        [TestMethod]
        public void RejectedInitialMigrationCannotRewriteValidLegacyStorageAsDefaults()
        {
            var values = new MigratingSettings { ThrowAlways = true };
            var controller = PrepareMigration(values, out _);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            byte[] original = MessagePackSerializer.Serialize(LegacyCosts("123"));
            File.WriteAllBytes(SettingsPath, original);
            Assert.ThrowsExactly<InvalidOperationException>(() => controller.LoadInitialSettings());
            Assert.ThrowsExactly<InvalidOperationException>(() => controller.Activate());
            Assert.AreEqual(0, values.Setters);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(SettingsPath));
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(SettingsPath), "*.corrupt-*").Length);
        }

        [TestMethod]
        public void ExpandedSelectionIsSchemaCheckedBeforeAnySetter()
        {
            var values = new MigratingSettings(); var controller = PrepareMigration(values, out _); controller.Activate();
            int setters = values.Setters;
            values.AddUnknown = true;
            Assert.ThrowsExactly<InvalidDataException>(() => controller.ApplyWorkingSnapshot(LegacyCosts("123")));
            Assert.AreEqual(setters, values.Setters);
        }

        [TestMethod]
        public void DirectMissionEntryAndWorkingReplacementMigrateBeforePersonalOrMissionMerge()
        {
            var values = new MigratingSettings(); var controller = PrepareMigration(values, out var host); controller.Activate();
            byte[] personal = File.ReadAllBytes(SettingsPath);
            host.HasMissionContext = true; host.MissionPresetEditable = true;
            controller.EnterMissionPreset(LegacyCosts("123"), "Foreign mission", true);
            Assert.AreEqual("123", values.HumanExtraBuildingCosts);
            controller.ApplyMissionWorkingSnapshot(LegacyCosts("77"), "Foreign replacement");
            Assert.AreEqual("77", values.HumanExtraBuildingCosts);
            int setters = values.Setters;
            values.ThrowAlways = true;
            Assert.ThrowsExactly<InvalidOperationException>(() => controller.ApplyMissionWorkingSnapshot(LegacyCosts("99"), "Rejected"));
            Assert.AreEqual(setters, values.Setters);
            Assert.AreEqual("77", values.HumanExtraBuildingCosts);
            CollectionAssert.AreEqual(personal, File.ReadAllBytes(SettingsPath));
            values.ThrowAlways = false;
            host.HasMissionContext = false;
            controller.ExitMissionPreset();
            Assert.AreEqual("0", values.HumanExtraBuildingCosts);
        }

        [TestMethod]
        public void PreparedEnterBypassesOnlyItsConsumedEntryWhileNestedWorkingStillMigrates()
        {
            var values = new MigratingSettings(); var controller = PrepareMigration(values, out var host); controller.Activate();
            host.HasMissionContext = true; host.MissionPresetEditable = true;
            int before = values.Calls;
            bool nested = false;
            host.SnapshotApplied = () => {
                if (nested) return;
                nested = true;
                controller.ApplyWorkingSnapshot(LegacyCosts("55"), writeLocalStorage: false);
            };
            var prepared = LegacyCosts("123");
            prepared[nameof(MigratingSettings.HumanExtraBuildingCosts)] = MessagePackSerializer.Serialize("123");
            using (ModSettingsMigration.PrepareMissionEntry(values))
                controller.EnterMissionPreset(prepared, "Prepared host", true);
            Assert.IsTrue(nested);
            Assert.AreEqual(before + 1, values.Calls, "Prepared Enter reran conversion or suppressed fresh nested working values.");
            Assert.AreEqual("55", values.HumanExtraBuildingCosts);
            host.SnapshotApplied = null;
            controller.EnterMissionPreset(LegacyCosts("77"), "Fresh raw", true);
            Assert.AreEqual(before + 2, values.Calls);
            Assert.AreEqual("77", values.HumanExtraBuildingCosts);
        }

        [TestMethod]
        public void PreparedEntryTokensAreEndpointBoundSingleUseAndFinallyRestored()
        {
            object first = new object(), other = new object();
            using (ModSettingsMigration.PrepareMissionEntry(first))
            {
                Assert.IsFalse(ModSettingsMigration.TryConsumePreparedMissionEntry(other));
                using (ModSettingsMigration.PrepareMissionEntry(other))
                {
                    Assert.IsTrue(ModSettingsMigration.TryConsumePreparedMissionEntry(other));
                    Assert.IsFalse(ModSettingsMigration.TryConsumePreparedMissionEntry(other));
                }
                Assert.IsTrue(ModSettingsMigration.TryConsumePreparedMissionEntry(first));
                Assert.IsFalse(ModSettingsMigration.TryConsumePreparedMissionEntry(first));
            }
            Assert.IsFalse(ModSettingsMigration.TryConsumePreparedMissionEntry(first));
            Assert.ThrowsExactly<InvalidOperationException>(() => {
                using (ModSettingsMigration.PrepareMissionEntry(first)) throw new InvalidOperationException("Foreign endpoint rejected entry");
            });
            Assert.IsFalse(ModSettingsMigration.TryConsumePreparedMissionEntry(first), "A failed or foreign entry leaked its token.");
        }

        [TestMethod]
        public void PublishedMigrationAppliesPersistsPublishesAndOverwritesDerivedValuesAsFixed()
        {
            var values = new MigratingSettings(); var controller = PrepareMigration(values, out var host); controller.Activate();
            controller.SavePersonalPreset("old", "Old", "", new[] {
                new PresetSaveSelection { PropertyName = nameof(MigratingSettings.BuildingCosts), Mode = PublishedPresetValueMode.Fixed }
            }, false);
            var preset = controller.PublishedPresets.Single(p => p.Id == "old");
            // An existing old preset has only its packed Fixed source property.
            preset.Settings[nameof(MigratingSettings.BuildingCosts)].Value = "legacy:123";
            controller.LoadPreset(preset);
            Assert.AreEqual("123", values.HumanExtraBuildingCosts);
            Assert.IsTrue(host.Publications.Contains(nameof(MigratingSettings.HumanExtraBuildingCosts)));
            var modes = controller.GetPresetSaveModes(preset);
            Assert.AreEqual(PublishedPresetValueMode.Fixed, modes[nameof(MigratingSettings.HumanExtraBuildingCosts)]);
            controller.SavePersonalPreset("old", "Old", "", modes.Select(pair => new PresetSaveSelection { PropertyName = pair.Key, Mode = pair.Value }), true);
            values.HumanExtraBuildingCosts = "0";
            controller.FlushPersonalStorage();
            Assert.IsTrue(MessagePackSerializer.Deserialize<bool>(Read()[LobbyPresetController.PresetDirtyKey]), "Derived property edits must mark the source modified.");
            controller.LoadPreset(controller.PublishedPresets.Single(p => p.Id == "old"));
            Assert.AreEqual("123", values.HumanExtraBuildingCosts, "Overwrite changed a derived Fixed value into Player.");
            controller.ApplyWorkingSnapshot(new Dictionary<string, byte[]> {
                [nameof(MigratingSettings.BuildingCosts)] = MessagePackSerializer.Serialize("modern")
            });
            Assert.AreEqual("123", values.HumanExtraBuildingCosts, "An unrelated partial source cleared existing extras.");
        }
    }
}
