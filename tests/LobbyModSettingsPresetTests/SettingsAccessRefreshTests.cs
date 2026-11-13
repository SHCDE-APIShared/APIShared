using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LobbyModSettingsPresetTests
{
    public partial class PresetTests
    {
        // Every access refresh re-publishes the mode notices to the bound view, so their count is the
        // refresh work an observer sees. One unchanged refresh calibrates the count per refresh.
        private static int ModeNotices(List<string> notifications) =>
            notifications.Count(name => name == nameof(PresetLobbyModSettingsViewModel.System_ModeNoticeVisibility));

        private static int ModeNoticesPerRefresh(PresetLobbyModSettingsViewModel settings)
        {
            var notifications = new List<string>();
            System.ComponentModel.PropertyChangedEventHandler record = (_, args) => notifications.Add(args.PropertyName);
            settings.PropertyChanged += record;
            settings.System_RefreshSettingsAccess();
            settings.PropertyChanged -= record;
            Assert(ModeNotices(notifications) > 0, "An access refresh did not publish the mode notices.");
            return ModeNotices(notifications);
        }

        private static readonly string[] RoleOutputs =
        {
            nameof(PresetLobbyModSettingsViewModel.IsLocalSettingsHost), nameof(PresetLobbyModSettingsViewModel.CanEditHostSettings),
            nameof(PresetLobbyModSettingsViewModel.CanEditMissionIndependentHostSettings), nameof(PresetLobbyModSettingsViewModel.CanToggleHostSettings),
            nameof(PresetLobbyModSettingsViewModel.CanChangePreset), nameof(PresetLobbyModSettingsViewModel.CanResetSettings),
            nameof(PresetLobbyModSettingsViewModel.PresetVisibility), nameof(PresetLobbyModSettingsViewModel.AreSettingsEditable),
            nameof(PresetLobbyModSettingsViewModel.HostSettingsOpacity),
        };

        private sealed class ObservedRole { internal bool Host = true; }

        private FakeSettings StartAccessSettings(string name, ObservedRole role)
        {
            var settings = new FakeSettings { System_TestObservedLocalHost = () => role.Host };
            settings.PreparePresets(null, Path.Combine(testRoot, name, "PresetTest.dll"), ModName);
            settings.LoadInitialPresetSettings();
            settings.ActivatePresets();
            return settings;
        }

        [TestMethod]
        public void RoleChangeUpdatesEveryAccessOutputWithoutRecursiveRefreshes()
        {
            var role = new ObservedRole();
            var settings = StartAccessSettings("AccessRoleChange", role);
            int perRefresh = ModeNoticesPerRefresh(settings);
            Assert(settings.IsLocalSettingsHost && settings.CanEditHostSettings && settings.CanChangePreset,
                "The fixture did not start as the settings host.");
            var notifications = new List<string>();
            int observerRefreshes = 0;
            settings.PropertyChanged += (_, args) =>
            {
                notifications.Add(args.PropertyName);
                // An observer that asks for fresh access state while it is being published.
                if (args.PropertyName == nameof(settings.CanEditHostSettings))
                {
                    observerRefreshes++;
                    settings.System_RefreshSettingsAccess();
                }
            };

            role.Host = false;
            settings.System_RefreshSettingsAccess();

            Assert(!settings.IsLocalSettingsHost && !settings.CanEditHostSettings && !settings.CanChangePreset &&
                !settings.CanResetSettings && settings.PresetVisibility == Noesis.Visibility.Collapsed,
                "A client role did not lock the host gates.");
            foreach (string output in RoleOutputs)
                Assert(notifications.Contains(output), "The role change did not notify " + output + ".");
            Assert(observerRefreshes == 1, "One role change published the editing gate " + observerRefreshes + " times.");
            Assert(ModeNotices(notifications) == perRefresh,
                "Access notifications started recursive refreshes: " + ModeNotices(notifications) + " mode notices.");

            notifications.Clear();
            role.Host = true;
            settings.System_RefreshSettingsAccess();
            Assert(settings.IsLocalSettingsHost && settings.CanEditHostSettings && settings.CanChangePreset,
                "Regaining the host role did not unlock the host gates.");
            Assert(notifications.Contains(nameof(settings.CanEditHostSettings)), "Regained host rights were not notified.");
        }

        [TestMethod]
        public void SettingEditsRefreshAccessOnceEachAndStillDetectRoleChanges()
        {
            var role = new ObservedRole();
            var settings = StartAccessSettings("AccessSingleEdit", role);
            int perRefresh = ModeNoticesPerRefresh(settings);
            var notifications = new List<string>();
            settings.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

            settings.Number = 41;

            Assert(ModeNotices(notifications) == perRefresh, "One edit published " + ModeNotices(notifications) + " mode notices.");
            Assert(notifications.Count(name => name == nameof(settings.Number)) == 1, "The edited setting was not notified exactly once.");

            // A synchronized host value can be the first sign of a client role; the edit must reveal it.
            notifications.Clear();
            role.Host = false;
            settings.Number = 42;
            Assert(!settings.IsLocalSettingsHost && !settings.CanEditHostSettings,
                "A setting notification no longer detects a changed role.");
            Assert(notifications.Contains(nameof(settings.CanEditHostSettings)), "The detected role was not notified.");
        }

        [TestMethod]
        public void ContextChangeOutsideARefreshStillDetectsTheCurrentRole()
        {
            var role = new ObservedRole();
            var settings = StartAccessSettings("AccessContextChange", role);
            int perRefresh = ModeNoticesPerRefresh(settings);
            var notifications = new List<string>();
            settings.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

            role.Host = false;
            // Raises the access outputs itself, as mission and preset transitions do.
            settings.System_SetExplicitMissionSettings(true);

            Assert(!settings.IsLocalSettingsHost && !settings.CanEditHostSettings,
                "Access outputs raised outside a refresh no longer re-read the current role.");
            Assert(notifications.LastIndexOf(nameof(settings.CanEditHostSettings)) >
                notifications.IndexOf(nameof(settings.CanEditHostSettings)),
                "The detected role was not published after the context change.");
            // The context change's own mode refresh plus the one refresh that follows it.
            Assert(ModeNotices(notifications) == 2 * perRefresh,
                "A context change started one refresh per access output: " + ModeNotices(notifications) + " mode notices.");
        }
    }
}
