using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LobbyModSettingsPresetTests
{
    public partial class PresetTests
    {
        [TestMethod]
        public void LateTrailSourcePublicationAndRepeatedResetKeepPersonalStorage()
        {
            var settings = StartDocuments();
            settings.Number = 23;
            byte[] personal = File.ReadAllBytes(NewFile);
            var provider = new MissionWorkingSources(settings);
            int changes = 0;
            Action changed = () => changes++;
            ModSettingsWorkingSourceRegistry.SourcesChanged += changed;
            ModSettingsWorkingSourceRegistry.Register(provider);
            try
            {
                Assert(ModSettingsWorkingSourceRegistry.GetProviderSources(TargetGuid).Count == 0, "Unexpected mission source before selection.");
                var snapshot = settings.System_CreateCurrentWorkingSnapshot();
                snapshot[nameof(settings.Number)] = MessagePackSerializer.Serialize(91);
                settings.System_EnterMissionPreset(snapshot, "Trail", true);
                provider.Publish("coop:package:fingerprint:1:1");
                var sources = ModSettingsWorkingSourceRegistry.GetProviderSources(TargetGuid);
                Assert(changes == 2 && sources.Count == 3 && sources.Single(source => source.IsPreferred).Id == ModSettingsWorkingSourceRegistry.TrailId,
                    "Late Trail publication did not forward source invalidation and preferred identity.");
                settings.Number = 99;
                ModSettingsWorkingSourceRegistry.Apply(TargetGuid, ModSettingsWorkingSourceRegistry.TrailId);
                Assert(settings.Number == 91, "Trail reset did not restore fixed author settings.");
                settings.Number = 100;
                ModSettingsWorkingSourceRegistry.Apply(TargetGuid, ModSettingsWorkingSourceRegistry.TrailId);
                Assert(settings.Number == 91, "A working edit replaced the Trail reset source.");
                CollectionAssert.AreEqual(personal, File.ReadAllBytes(NewFile));

                settings.System_LoadModDefaults();
                Assert(settings.Number == 5, "Mod defaults no longer reset independently of the Trail.");
                ModSettingsWorkingSourceRegistry.Apply(TargetGuid, ModSettingsWorkingSourceRegistry.PersonalSettingsId);
                Assert(settings.Number == 23 && settings.IsMissionPresetActive && settings.MissionPresetEditable,
                    "Personal reset did not restore the stored values within the editable mission.");
                Assert(settings.System_TestPresetStatusText.Contains("Personal Settings"), "Personal reset lost its source attribution.");
                settings.Number = 101;
                ModSettingsWorkingSourceRegistry.Apply(TargetGuid, ModSettingsWorkingSourceRegistry.PersonalSettingsId);
                Assert(settings.Number == 23, "A temporary edit replaced the personal reset source.");
                ModSettingsWorkingSourceRegistry.Apply(TargetGuid, ModSettingsWorkingSourceRegistry.MapId);
                Assert(settings.Number == 37, "Map reset no longer works alongside a Trail source.");
                provider.Entry = new MissionModSettingsEntry { PlayerSettings = new[] { nameof(settings.Number) } };
                provider.Publish("coop:package:fingerprint:1:2");
                Assert(ModSettingsWorkingSourceRegistry.GetProviderSources(TargetGuid).Single(source => source.IsPreferred).PreferenceContextId.EndsWith(":1:2"),
                    "New mission did not publish its distinct preferred Trail identity.");
                ModSettingsWorkingSourceRegistry.Apply(TargetGuid, ModSettingsWorkingSourceRegistry.TrailId);
                Assert(settings.Number == 23, "Player rules used the modified working copy instead of personal settings.");
                provider.Entry = new MissionModSettingsEntry();
                ModSettingsWorkingSourceRegistry.Apply(TargetGuid, ModSettingsWorkingSourceRegistry.TrailId);
                Assert(settings.Number == 5, "Omitted Trail values did not resolve to mod defaults.");
                CollectionAssert.AreEqual(personal, File.ReadAllBytes(NewFile));
                provider.Clear();
                Assert(ModSettingsWorkingSourceRegistry.GetProviderSources(TargetGuid).Count == 0, "Leaving the mission retained a stale source.");
                settings.System_ExitMissionPreset();
                Assert(settings.Number == 23, "Exit did not restore personal settings.");
                Assert(StartDocuments().Number == 23, "Restart loaded temporary reset values as personal settings.");
            }
            finally
            {
                settings.System_ExitMissionPreset();
                ModSettingsWorkingSourceRegistry.Unregister(provider);
                ModSettingsWorkingSourceRegistry.SourcesChanged -= changed;
            }
        }

        [TestMethod]
        public void TrailSourceApplicationRespectsReadOnlyAndClientHostValues()
        {
            var settings = StartDocuments();
            byte[] personal = File.ReadAllBytes(NewFile);
            var provider = new MissionWorkingSources(settings);
            ModSettingsWorkingSourceRegistry.Register(provider);
            try
            {
                var snapshot = settings.System_CreateCurrentWorkingSnapshot();
                snapshot[nameof(settings.Number)] = MessagePackSerializer.Serialize(77);
                settings.System_EnterMissionPreset(snapshot, "Trail", false);
                provider.Publish("readonly");
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.ThrowsExactly<InvalidOperationException>(() =>
                    ModSettingsWorkingSourceRegistry.Apply(TargetGuid, ModSettingsWorkingSourceRegistry.TrailId));
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.ThrowsExactly<InvalidOperationException>(() =>
                    ModSettingsWorkingSourceRegistry.Apply(TargetGuid, ModSettingsWorkingSourceRegistry.PersonalSettingsId));
                Assert(settings.Number == 77, "Read-only Trail accepted a reset.");
                settings.System_ExitMissionPreset();
                settings.System_EnterMissionPreset(snapshot, "Trail", true);
                // Model the resolved remote role; host/client transport is covered by the consumer harness.
                typeof(PresetLobbyModSettingsViewModel).GetField("isLocalHost", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(settings, false);
                ModSettingsWorkingSourceRegistry.Apply(TargetGuid, ModSettingsWorkingSourceRegistry.TrailId);
                Assert(settings.Number == 77, "Client reset replaced host-owned mission values.");
                ModSettingsWorkingSourceRegistry.Apply(TargetGuid, ModSettingsWorkingSourceRegistry.PersonalSettingsId);
                Assert(settings.Number == 77, "Personal reset replaced authenticated host-owned client values.");
                CollectionAssert.AreEqual(personal, File.ReadAllBytes(NewFile));
            }
            finally
            {
                typeof(PresetLobbyModSettingsViewModel).GetField("isLocalHost", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(settings, true);
                settings.System_ExitMissionPreset();
                ModSettingsWorkingSourceRegistry.Unregister(provider);
            }
        }

        // This boundary double supplies sources; production ViewModel, registry,
        // materialization and controller perform reset/persistence work. The suite
        // excludes source-selector UI via API_SHARED_PRESET_TESTS; no UI-rendering claim.
        private sealed class MissionWorkingSources : IModSettingsWorkingSourceProvider
        {
            private readonly FakeSettings settings;
            private string context;
            internal int Applies;
            internal MissionModSettingsEntry Entry = new MissionModSettingsEntry {
                Overrides = new Dictionary<string, object> { [nameof(FakeSettings.Number)] = 91L } };
            public event Action SourcesChanged;
            internal MissionWorkingSources(FakeSettings settings) { this.settings = settings; }
            internal void Publish(string id) { context = id; SourcesChanged?.Invoke(); }
            internal void Clear() { context = null; SourcesChanged?.Invoke(); }
            public IReadOnlyList<ModSettingsWorkingSource> GetSources(string guid) => context == null
                ? Array.Empty<ModSettingsWorkingSource>() : new[] {
                    new ModSettingsWorkingSource { Id = ModSettingsWorkingSourceRegistry.PersonalSettingsId, Kind = ModSettingsWorkingSourceKind.Personal },
                    new ModSettingsWorkingSource { Id = ModSettingsWorkingSourceRegistry.TrailId, Kind = ModSettingsWorkingSourceKind.Trail,
                        IsPreferred = true, PreferenceContextId = context },
                    new ModSettingsWorkingSource { Id = ModSettingsWorkingSourceRegistry.MapId, Kind = ModSettingsWorkingSourceKind.Map,
                        PreferenceContextId = context } };
            public void Apply(string guid, string id)
            {
                Applies++;
                if (id == ModSettingsWorkingSourceRegistry.PersonalSettingsId)
                {
                    settings.System_ApplyMissionPresetSnapshot(settings.System_CreatePlayerMissionPresetSnapshot(), "Personal Settings");
                    settings.System_CommitConfiguration();
                    return;
                }
                MissionModSettingsEntry entry = id == ModSettingsWorkingSourceRegistry.ModDefaultsId ? null :
                    id == ModSettingsWorkingSourceRegistry.TrailId ? Entry :
                    new MissionModSettingsEntry { Overrides = new Dictionary<string, object> { [nameof(FakeSettings.Number)] = 37L } };
                settings.System_ApplyMissionPresetSnapshot(MissionModSettingsPreparation.MaterializeSource(settings, entry), id);
            }
            public void ApplyMany(IEnumerable<string> guids, string id) { foreach (string guid in guids) Apply(guid, id); }
        }
    }
}
