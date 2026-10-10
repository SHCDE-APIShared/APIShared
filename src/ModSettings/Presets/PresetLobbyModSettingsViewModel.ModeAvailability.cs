using APIShared.GameModes;
using Noesis;

namespace APIShared.ModSettings
{
    public abstract partial class PresetLobbyModSettingsViewModel
    {
        /// <summary>Optional mode presentation rules. Configure on the UI thread before settings registration; binding does not alter settings, authority or persistence.</summary>
        public ModSettingsModeAvailability System_ModeAvailability { get; } = new ModSettingsModeAvailability();
        /// <summary>One compact page-level legend, localized through ResolveSettingsUiText.</summary>
        public string System_ModeNoticeText => ResolveSettingsUiTextSafe("Common.InactiveModeSettingsNotice",
            "Settings inactive in the selected game mode have a tinted background. Changes are saved for later games.");
        /// <summary>Shows the compact legend only when some setting is inactive; consumers can combine it with their existing notice.</summary>
        public Visibility System_ModeNoticeVisibility => System_ModeAvailability.HasInactive ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Combines an existing direct-launch notice with the mode legend. Opt-in consumer binding; the legacy direct-launch properties keep their contract.</summary>
        public string System_ConsumerModeNoticeText => System_DirectLaunchNoticeVisibility == Visibility.Visible ? System_DirectLaunchNoticeText : System_ModeNoticeText;
        /// <summary>Shows one notice when either the configured direct-launch explanation or the inactive-mode legend applies.</summary>
        public Visibility System_ConsumerModeNoticeVisibility => System_DirectLaunchNoticeVisibility == Visibility.Visible || System_ModeAvailability.HasInactive ? Visibility.Visible : Visibility.Collapsed;

        private bool refreshingModeAvailability;

        private void RefreshModeAvailability()
        {
            // Notice PropertyChanged listeners can synchronously request another access refresh.
            if (refreshingModeAvailability) return;
            refreshingModeAvailability = true;
            try
            {
                // Menu evidence is deliberately separate from the running mission snapshot.
                // These sets retain uncertainty rather than claiming a more specific launch family.
                if (settingsMenuContext == SettingsMenuContext.Campaign)
                    System_ModeAvailability.SetPreview(GameplayModAllowedContext.Campaign, isRealMultiplayer);
                else if (settingsMenuContext == SettingsMenuContext.DirectTrail)
                {
                    var possible = GameplayModAllowedContext.VanillaTrail | GameplayModAllowedContext.CustomTrail |
                        GameplayModAllowedContext.CoopTrail | GameplayModAllowedContext.SandsOfTime;
                    // Explicit trail settings can supply customized launch evidence. The page alone
                    // does not establish its family or successful launch; retain both possibilities.
                    if (missionPresetContext && missionPresetHasExplicitSettings)
                        possible |= GameplayModAllowedContext.CustomizedVanillaTrail | GameplayModAllowedContext.CustomizedCustomTrail |
                            GameplayModAllowedContext.CustomizedCoopTrail | GameplayModAllowedContext.CustomizedSandsOfTime;
                    System_ModeAvailability.SetPreview(possible, isRealMultiplayer);
                }
                else if (settingsMenuContext == SettingsMenuContext.CustomizeSetup)
                    System_ModeAvailability.SetPreview(GameplayModAllowedContext.CustomGame | GameplayModAllowedContext.CustomizedVanillaTrail |
                        GameplayModAllowedContext.CustomizedCustomTrail | GameplayModAllowedContext.CustomizedCoopTrail |
                        GameplayModAllowedContext.CustomizedSandsOfTime, isRealMultiplayer);
                else
                {
                    GameModeSnapshot snapshot = GameModeHelper.Capture();
                    if (snapshot.Kind == GameModeKind.Unknown) System_ModeAvailability.SetPreview(GameplayModAllowedContext.None, null);
                    else System_ModeAvailability.SetMission(snapshot);
                }
                RaiseModeNoticeProperties();
            }
            finally { refreshingModeAvailability = false; }
        }

        private void RaiseModeNoticeProperties()
        {
            base.OnPropertyChanged(nameof(System_ModeNoticeVisibility));
            base.OnPropertyChanged(nameof(System_ModeNoticeText));
            base.OnPropertyChanged(nameof(System_ConsumerModeNoticeVisibility));
            base.OnPropertyChanged(nameof(System_ConsumerModeNoticeText));
        }
    }
}
