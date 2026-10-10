using APIShared;
using APIShared.GameModes;
using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace LobbyModSettingsPresetTests
{
    [TestClass]
    public sealed class ModeAvailabilityTests
    {
        private static GameplayModActivationProfile Profile(GameplayModAllowedContext contexts, bool multiplayer = true) =>
            new GameplayModActivationProfile("foreign.example", "Foreign example", contexts, multiplayer);
        private static GameModeSnapshot Mode(GameModeKind kind, GameModeLaunchVariant variant = GameModeLaunchVariant.Standard, bool multiplayer = false, bool conflict = false) =>
            new GameModeSnapshot(multiplayer, false, false, false, kind == GameModeKind.MapEditor, false, false,
                false, false, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, kind, variant, 0, 0, 0, 0, 0, conflict);

        [TestMethod]
        public void ForeignGateUsesExistingEvaluatorForEveryModeAndVariant()
        {
            var profile = Profile(GameplayModAllowedContext.CustomGame | GameplayModAllowedContext.MapEditor |
                GameplayModAllowedContext.CustomizedVanillaTrail | GameplayModAllowedContext.CustomizedCustomTrail |
                GameplayModAllowedContext.CustomizedCoopTrail | GameplayModAllowedContext.CustomizedSandsOfTime, false);
            var gate = new GameplayModeGate(profile);
            foreach (GameModeKind kind in Enum.GetValues(typeof(GameModeKind)))
            foreach (GameModeLaunchVariant variant in Enum.GetValues(typeof(GameModeLaunchVariant)))
            foreach (bool multiplayer in new[] { false, true })
            foreach (bool conflict in new[] { false, true })
            {
                var mode = Mode(kind, variant, multiplayer, conflict);
                bool expected = GameplayModModePolicy.IsAllowed(profile, mode, out var reason);
                gate.Update(mode);
                Assert.AreEqual(expected, gate.IsAllowed, $"{kind}/{variant}/{multiplayer}/{conflict}");
                Assert.AreEqual(reason, gate.Reason);
                Assert.IsFalse(gate.IsEnabled(false));
                Assert.AreEqual(expected, gate.IsEnabled(true));
            }
        }

        [TestMethod]
        public void LifecycleGateResetsAndIsolatesListeners()
        {
            var gate = new GameplayModeGate(Profile(GameplayModAllowedContext.CustomGame));
            int transitions = 0;
            gate.StateChanged += _ => throw new InvalidOperationException("Consumer failure");
            gate.StateChanged += _ => { transitions++; Assert.AreEqual(transitions == 1, gate.IsAllowed); };
            var context = new MissionContext(1, MissionStartKind.NewGame, Mode(GameModeKind.CustomGame), "map.map", "Map", false, 160, 1, 1);
            gate.Update(new MissionLifecycleNotification(context, MissionLifecycleKind.Initialization, MissionInitializationPhase.BeforeLoad, MissionEndReason.None, false));
            gate.Update(new MissionLifecycleNotification(context, MissionLifecycleKind.Start, MissionInitializationPhase.ManagedReady, MissionEndReason.None, true, true));
            Assert.AreEqual(1, transitions, "Ready-session replay duplicated the transition.");
            gate.Update(new MissionLifecycleNotification(context, MissionLifecycleKind.End, MissionInitializationPhase.ManagedReady, MissionEndReason.Unloaded, true));
            Assert.AreEqual(2, transitions);
            Assert.AreEqual(GameModeKind.Unknown, gate.Snapshot.Kind);
            Assert.IsFalse(gate.IsAllowed);
        }

        [TestMethod]
        public void PreviewIsNeutralWithoutTargetOrWithMixedPermissions()
        {
            var availability = new ModSettingsModeAvailability();
            availability.ConfigureDefault(Profile(GameplayModAllowedContext.CustomGame));
            var state = availability.GetState("some.setting");
            Assert.AreEqual(SettingModeStatus.Unknown, state.Status);
            availability.SetPreview(GameplayModAllowedContext.Campaign, false);
            Assert.IsTrue(state.IsInactive);
            availability.SetPreview(GameplayModAllowedContext.Campaign | GameplayModAllowedContext.CustomGame, false);
            Assert.AreEqual(SettingModeStatus.Unknown, state.Status);
            availability.SetPreview(GameplayModAllowedContext.CustomGame, false);
            Assert.AreEqual(SettingModeStatus.Active, state.Status);
            availability.SetPreview(GameplayModAllowedContext.None, false);
            Assert.AreEqual(SettingModeStatus.Unknown, state.Status);
        }

        [TestMethod]
        public void ExceptionsAndLocalPreferencesDoNotInheritBlanketBlocking()
        {
            var availability = new ModSettingsModeAvailability();
            availability.ConfigureDefault(Profile(GameplayModAllowedContext.CustomGame));
            availability.Configure("blueprints", Profile(GameplayModAllowedContext.Campaign | GameplayModAllowedContext.MapEditor));
            availability.Configure("hotkey", null);
            availability.Configure("hunter", Profile(GameplayModAllowedContext.CustomGame, false));
            availability.ConfigureNetworkVariant("mp.value", true);
            availability.SetPreview(GameplayModAllowedContext.Campaign, false);
            Assert.IsTrue(availability.GetState("spawning").IsInactive);
            Assert.AreEqual(SettingModeStatus.Active, availability.GetState("blueprints").Status);
            Assert.AreEqual(SettingModeStatus.Unknown, availability.GetState("hotkey").Status);
            availability.SetPreview(GameplayModAllowedContext.CustomGame, true);
            Assert.IsTrue(availability.GetState("hunter").IsInactive);
            Assert.AreEqual(SettingModeStatus.Active, availability.GetState("mp.value").Status);
            availability.SetPreview(GameplayModAllowedContext.CustomGame, false);
            Assert.IsTrue(availability.GetState("mp.value").IsInactive);
            availability.SetPreview(GameplayModAllowedContext.Campaign, null);
            Assert.IsTrue(availability.GetState("mp.value").IsInactive, "Unknown network identity hid a definite mode exclusion.");
            availability.SetPreview(GameplayModAllowedContext.CustomGame, null);
            Assert.AreEqual(SettingModeStatus.Unknown, availability.GetState("mp.value").Status);
        }

        [TestMethod]
        public void MissionEvidenceAndObservableRowsRefreshWithoutReplacingState()
        {
            var availability = new ModSettingsModeAvailability();
            availability.ConfigureDefault(Profile(GameplayModAllowedContext.CustomizedVanillaTrail));
            var state = availability.GetState("value");
            int changes = 0;
            state.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(state.Status)) changes++; };
            availability.SetMission(Mode(GameModeKind.VanillaTrail));
            Assert.IsTrue(state.IsInactive);
            availability.SetMission(Mode(GameModeKind.VanillaTrail, GameModeLaunchVariant.Customized));
            Assert.AreEqual(SettingModeStatus.Active, state.Status);
            availability.SetMission(Mode(GameModeKind.VanillaTrail, GameModeLaunchVariant.RestoredCustomizedSave));
            Assert.AreEqual(2, changes);
            availability.SetMission(Mode(GameModeKind.VanillaTrail, GameModeLaunchVariant.Customized, conflict: true));
            Assert.IsTrue(state.IsInactive);
            availability.SetMission(default);
            Assert.AreEqual(SettingModeStatus.Unknown, state.Status);
            Assert.AreSame(state, availability.GetState("value"));
        }

        [TestMethod]
        public void SynchronousAccessRefreshListenersCannotReenterModeRefresh()
        {
            var settings = new Settings();
            var refresh = typeof(PresetLobbyModSettingsViewModel).GetMethod("RefreshModeAvailability",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            int notifications = 0;
            settings.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(settings.System_ModeNoticeVisibility)) return;
                Assert.IsTrue(++notifications < 20, "Mode notices recursively refresh the same page.");
                // Models the synchronous access-refresh subscriber installed by PreparePresets,
                // without requiring Unity's native network singleton in this managed test.
                refresh.Invoke(settings, null);
            };
            settings.System_TestSetSettingsMenuContext(false, true, false);
            Assert.IsTrue(notifications > 0);
            settings.System_TestSetSettingsMenuContext(true, false, false);
            Assert.IsTrue(notifications < 20);
        }
        private sealed class Settings : PresetLobbyModSettingsViewModel
        {
            public bool ConfiguredValue { get; set; } = true;
        }

        [TestMethod]
        public void MenuChangesAndExplicitTrailSourcesKeepValuesAndEditingRights()
        {
            var settings = new Settings();
            settings.System_ModeAvailability.ConfigureDefault(Profile(GameplayModAllowedContext.CustomGame |
                GameplayModAllowedContext.CustomizedVanillaTrail | GameplayModAllowedContext.CustomizedCustomTrail |
                GameplayModAllowedContext.CustomizedCoopTrail | GameplayModAllowedContext.CustomizedSandsOfTime));
            var row = settings.System_ModeAvailability.GetState("value");
            bool hostRights = settings.CanEditHostSettings;
            bool clientRights = settings.CanEditClientSettings;
            settings.System_TestSetSettingsMenuContext(false, true, false);
            Assert.IsTrue(row.IsInactive);
            Assert.AreEqual(Noesis.Visibility.Visible, settings.System_ConsumerModeNoticeVisibility);
            settings.System_TestSetSettingsMenuContext(true, false, false);
            Assert.AreEqual(SettingModeStatus.Active, row.Status);
            settings.System_TestSetSettingsMenuContext(false, false, true);
            Assert.IsTrue(row.IsInactive);
            settings.System_SetExplicitMissionSettings(true);
            // Without an active mission-preset source, a flag alone must not unlock the preview.
            Assert.IsTrue(row.IsInactive);
            settings.System_TestSetSettingsMenuContext(false, false, false);
            Assert.AreEqual(SettingModeStatus.Unknown, row.Status);
            Assert.IsTrue(settings.ConfiguredValue);
            Assert.AreEqual(hostRights, settings.CanEditHostSettings);
            Assert.AreEqual(clientRights, settings.CanEditClientSettings);
        }
    }
}
