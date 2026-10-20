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
        [TestMethod]
        public void ActivationOpacityIsIndependentAndNotAnEditingGate()
        {
            var settings = StartOpacitySettings();
            var notifications = new List<string>();
            settings.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            bool hostRights = settings.CanEditHostSettings;
            bool clientRights = settings.CanEditClientSettings;
            Assert(settings.HostSettingsOpacity == 1f && settings.ClientSettingsOpacity == 1f,
                "Active settings must be fully opaque.");
            settings.HostSettingsEnabled = false;
            Assert(settings.HostSettingsOpacity == 0.65f && settings.ClientSettingsOpacity == 1f,
                "Host activation must dim only host content.");
            Assert(notifications.Contains(nameof(settings.HostSettingsOpacity)), "Host toggle did not notify the view.");
            notifications.Clear();
            settings.ClientSettingsEnabled = false;
            Assert(settings.ClientSettingsOpacity == 0.65f, "Client activation did not dim its content.");
            Assert(notifications.Contains(nameof(settings.ClientSettingsOpacity)), "Client toggle did not notify the view.");
            Assert(settings.CanEditHostSettings == hostRights && settings.CanEditClientSettings == clientRights,
                "Presentation changed editing rights.");
            settings.HostSettingsEnabled = true;
            settings.ClientSettingsEnabled = true;
            Assert(settings.HostSettingsOpacity == 1f && settings.ClientSettingsOpacity == 1f,
                "Re-enabling did not restore full opacity.");
        }

        [TestMethod]
        public void SnapshotAndResetRefreshOpacityWithoutPersistingPresentation()
        {
            var settings = StartOpacitySettings();
            var notifications = new List<string>();
            settings.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            var snapshot = settings.System_CreateCurrentWorkingSnapshot();
            snapshot[nameof(settings.EnableMod)] = MessagePackSerializer.Serialize(false);
            settings.System_EnterMissionPreset(snapshot, "Trail", false);
            Assert(settings.HostSettingsOpacity == 0.65f && settings.ClientSettingsOpacity == 1f,
                "Mission activation did not update the presentation.");
            Assert(!settings.CanEditHostSettings && !settings.CanEditClientSettings,
                "Opacity must not remove the mission read-only lock.");
            Assert(notifications.Contains(nameof(settings.HostSettingsOpacity)), "Snapshot did not notify opacity.");
            notifications.Clear();
            settings.System_ExitMissionPreset();
            Assert(settings.HostSettingsOpacity == 1f, "Leaving the mission did not restore opacity.");
            Assert(notifications.Contains(nameof(settings.HostSettingsOpacity)), "Restoration did not notify opacity.");
            settings.HostSettingsEnabled = false;
            settings.ClientSettingsEnabled = false;
            notifications.Clear();
            settings.System_LoadModDefaults();
            Assert(settings.HostSettingsOpacity == 1f && settings.ClientSettingsOpacity == 1f,
                "Reset did not restore activation presentation.");
            Assert(notifications.Contains(nameof(settings.HostSettingsOpacity)) &&
                notifications.Contains(nameof(settings.ClientSettingsOpacity)), "Reset did not notify both scopes.");
            snapshot = settings.System_CreateCurrentWorkingSnapshot();
            Assert(!snapshot.ContainsKey(nameof(settings.HostSettingsOpacity)) &&
                !snapshot.ContainsKey(nameof(settings.ClientSettingsOpacity)), "Presentation leaked into persisted settings.");
        }

        [TestMethod]
        public void PublishedPresetAndRestartPreserveOpacityAndInactiveEdits()
        {
            Directory.CreateDirectory(LegacyPresets);
            File.WriteAllText(Path.Combine(LegacyPresets, "preset_inactive.json"),
                CreateLegacyPresetJson("inactive", "Inactive", false, 37));
            var settings = StartDocuments();
            var notifications = new List<string>();
            settings.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            settings.System_TestLoadPreset(settings.System_TestPublishedPresets.Single(p => p.Id == "inactive").StableId);
            Assert(settings.HostSettingsOpacity == 0.65f && settings.Number == 37 &&
                notifications.Contains(nameof(settings.HostSettingsOpacity)), "Published preset did not update opacity.");
            settings.Number = 48;
            var restarted = StartDocuments();
            Assert(restarted.HostSettingsOpacity == 0.65f && restarted.Number == 48,
                "Restart lost inactive settings or their activation presentation.");
            restarted.HostSettingsEnabled = true;
            Assert(restarted.HostSettingsOpacity == 1f && restarted.Number == 48,
                "Reactivation replaced the prepared value.");
        }

        [TestMethod]
        public void MissingActivationSwitchKeepsFullOpacity()
        {
            var settings = new EmptySettings();
            Assert(settings.HostSettingsOpacity == 1f && settings.ClientSettingsOpacity == 1f,
                "Unprepared settings must not be dimmed.");
            settings.PreparePresets(null, Path.Combine(testRoot, "EmptyOpacity.dll"), "EmptyOpacity");
            Assert(settings.HostSettingsOpacity == 1f && settings.ClientSettingsOpacity == 1f,
                "A schema without switches must remain fully opaque.");
            var hostOnly = StartDocuments();
            hostOnly.EnableMod = false;
            Assert(hostOnly.HostSettingsOpacity == 0.65f && hostOnly.ClientSettingsOpacity == 1f,
                "A host switch must not dim a client scope without its own switch.");
        }

        private OpacitySettings StartOpacitySettings()
        {
            var settings = new OpacitySettings();
            settings.PreparePresets(null, Path.Combine(testRoot, "Opacity.dll"), "Opacity", "Tests.Opacity", new Version(1, 0));
            settings.LoadInitialPresetSettings();
            settings.ActivatePresets();
            return settings;
        }

        // Actual setters/authority are covered by the workspace host/client harness;
        // this fixture isolates the production controller and its UI notifications.
        private sealed class OpacitySettings : PresetLobbyModSettingsViewModel
        {
            private bool hostEnabled = true;
            private bool clientEnabled = true;
            [SyncHostOnly]
            public bool EnableMod
            {
                get => hostEnabled;
                set { if (hostEnabled == value) return; hostEnabled = value; OnPropertyChanged(nameof(EnableMod)); }
            }
            [PresetLocal]
            public bool EnableClientFeatures
            {
                get => clientEnabled;
                set { if (clientEnabled == value) return; clientEnabled = value; OnPropertyChanged(nameof(EnableClientFeatures)); }
            }
        }
    }
}
