using APIShared.GameModes;
using APIShared.ModSettings;
using APIShared.Internal;
using BepInEx;
using BepInEx.Logging;
using MessagePack;
using SHCDESE.API;
using SHCDESE.API.Components.ModManager;
using SHCDESE.API.Components.Network;
using SHCDESE.BepInEx.Bootstrap;
using SHCDESE.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
#if !API_SHARED_PRESET_TESTS
using R3;
using SHCDESE.EventAPI;
using SHCDESE.NoesisUtil;
#endif
using ComboBoxItem = Noesis.ComboBoxItem;
using Visibility = Noesis.Visibility;
#if API_SHARED_LOBBY_OBSERVER && !API_SHARED_PRESET_TESTS
using APIShared;
#endif

namespace APIShared.ModSettings
{
    public abstract partial class PresetLobbyModSettingsViewModel
    {
#if !API_SHARED_PRESET_TESTS
        /// <summary>Localized XAML caption/help for Common.SettingsSource; English fallback: Reset settings to</summary>
        public string System_SettingsSourceText =>
            ResolveSettingsUiTextSafe("Common.SettingsSource", APIShared.Localization.UiText.English("Common.SettingsSource"));

        /// <summary>Localized XAML caption/help for Common.SettingsSourceLoad; English fallback: Reset</summary>
        public string System_SettingsSourceLoadText =>
            ResolveSettingsUiTextSafe("Common.SettingsSourceLoad", APIShared.Localization.UiText.English("Common.SettingsSourceLoad"));

        /// <summary>Localized XAML caption/help for Common.SettingsSourceHelp; English fallback: Resets this mod&apos;s settings to the selected source. Personal presets are not changed. In multiplayer, only the host can reset host settings.</summary>
        public string System_SettingsSourceHelpText =>
            ResolveSettingsUiTextSafe("Common.SettingsSourceHelp", APIShared.Localization.UiText.English("Common.SettingsSourceHelp"));

        /// <summary>Live observable choices for resetting working settings; bind but do not mutate the collection.</summary>
        public ObservableCollection<ModSettingsWorkingSource> System_SettingsSources => settingsSources;

        /// <summary>Selected reset source; selection alone does not apply it.</summary>
        public ModSettingsWorkingSource System_SelectedSettingsSource
        {
            get => selectedSettingsSource;
            set
            {
                if (ReferenceEquals(selectedSettingsSource, value)) return;
                selectedSettingsSource = value;
                base.OnPropertyChanged(nameof(System_SelectedSettingsSource));
                base.OnPropertyChanged(nameof(System_CanLoadSettingsSource));
            }
        }

        /// <summary>Whether a source is selected and the mission working copy permits reset.</summary>
        public bool System_CanLoadSettingsSource =>
            selectedSettingsSource != null && (!IsMissionPresetSelected || missionPresetEditable);

        /// <summary>Shows source selection when persistent settings exist.</summary>
        public Visibility System_SettingsSourceVisibility =>
            presetController?.HasPersistentSettings == true ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Application notice and the current source/modified description, joined only when both exist.</summary>
        public string System_PresetStatusText
        {
            get
            {
                string status = presetController?.GetStatusText(
                    ResolveSettingsUiTextSafe("Common.PresetBasedOn", APIShared.Localization.UiText.English("Common.PresetBasedOn")),
                    ResolveSettingsUiTextSafe("Common.PresetModified", APIShared.Localization.UiText.English("Common.PresetModified")),
                    ResolveSettingsUiTextSafe("Common.PresetSourcePersonal", APIShared.Localization.UiText.English("Common.PresetSourcePersonal")),
                    ResolveSettingsUiTextSafe("Common.PresetSourceBundled", APIShared.Localization.UiText.English("Common.PresetSourceBundled")),
                    ResolveSettingsUiTextSafe("Common.PresetSourceExternal", APIShared.Localization.UiText.English("Common.PresetSourceExternal"))) ?? string.Empty;
                string notice = System_ApplicationNotice;
                if (string.IsNullOrEmpty(notice)) return status;
                return string.IsNullOrEmpty(status) ? notice : notice + " · " + status;
            }
        }

        /// <summary>Shows nonempty source/application status.</summary>
        public Visibility System_PresetStatusVisibility =>
            string.IsNullOrWhiteSpace(System_PresetStatusText) ? Visibility.Collapsed : Visibility.Visible;

        private void RebuildSettingsSources()
        {
            if (presetController == null) return;
            string previous = selectedSettingsSource?.Id;
            settingsSources.Clear();
            settingsSources.Add(new ModSettingsWorkingSource
            {
                Id = ModSettingsWorkingSourceRegistry.ModDefaultsId,
                Kind = ModSettingsWorkingSourceKind.ModDefault,
                DisplayName = ResolveSettingsUiTextSafe("Common.SettingsSourceDefaults", APIShared.Localization.UiText.English("Common.SettingsSourceDefaults")),
            });
            foreach (ModSettingsWorkingSource source in ModSettingsWorkingSourceRegistry.GetProviderSources(presetController.TargetGuid))
            {
                if (source != null && !string.IsNullOrWhiteSpace(source.Id) &&
                    !string.Equals(source.Id, ModSettingsWorkingSourceRegistry.ModDefaultsId, StringComparison.Ordinal))
                {
                    if (source.Kind == ModSettingsWorkingSourceKind.Trail)
                        source.DisplayName = ResolveSettingsUiTextSafe("Common.SettingsSourceTrail", APIShared.Localization.UiText.English("Common.SettingsSourceTrail"));
                    else if (source.Kind == ModSettingsWorkingSourceKind.Map)
                        source.DisplayName = ResolveSettingsUiTextSafe("Common.SettingsSourceMap", APIShared.Localization.UiText.English("Common.SettingsSourceMap"));
                    else if (source.Kind == ModSettingsWorkingSourceKind.Personal)
                        source.DisplayName = ResolveSettingsUiTextSafe("Common.SettingsSourcePersonal", APIShared.Localization.UiText.English("Common.SettingsSourcePersonal"));
                    settingsSources.Add(source);
                }
            }
            ModSettingsWorkingSource preferredSource = settingsSources.FirstOrDefault(item => item.IsPreferred) ??
                settingsSources.FirstOrDefault(item => string.Equals(item.Id, ModSettingsWorkingSourceRegistry.ModDefaultsId, StringComparison.Ordinal));
            string preferred = preferredSource?.Id ?? ModSettingsWorkingSourceRegistry.ModDefaultsId;
            string preferredToken = preferred + "\n" + (preferredSource?.PreferenceContextId ?? string.Empty);
            bool preferredChanged = !string.Equals(
                preferredSettingsSourceToken,
                preferredToken,
                StringComparison.Ordinal);
            selectedSettingsSource = (preferredChanged
                    ? settingsSources.FirstOrDefault(item => string.Equals(item.Id, preferred, StringComparison.Ordinal))
                    : settingsSources.FirstOrDefault(item => string.Equals(item.Id, previous, StringComparison.Ordinal))) ??
                settingsSources.FirstOrDefault(item => string.Equals(item.Id, preferred, StringComparison.Ordinal)) ??
                settingsSources.FirstOrDefault();
            preferredSettingsSourceToken = preferredToken;
            base.OnPropertyChanged(nameof(System_SettingsSources));
            base.OnPropertyChanged(nameof(System_SelectedSettingsSource));
            base.OnPropertyChanged(nameof(System_CanLoadSettingsSource));
            base.OnPropertyChanged(nameof(System_SettingsSourceVisibility));
        }

        private void LoadSelectedSettingsSource()
        {
            using (ModSettingsSearchUpdateScope.Begin(this))
            {
                ModSettingsWorkingSource source = selectedSettingsSource;
                if (source == null || !System_CanLoadSettingsSource) return;
                try
                {
                    // The personal source captures its own values and rollback state before applying.
                    // Refreshing desired values here would erase the mission draft on failure.
                    bool personal = string.Equals(source.Id, ModSettingsWorkingSourceRegistry.PersonalSettingsId, StringComparison.Ordinal);
                    if (!personal) System_RefreshOwnConfiguration();
                    if (string.Equals(source.Id, ModSettingsWorkingSourceRegistry.ModDefaultsId, StringComparison.Ordinal))
                    {
                        System_LoadModDefaults();
                    }
                    else
                    {
                        ModSettingsWorkingSourceRegistry.Apply(presetController.TargetGuid, source.Id);
                        if (!personal) System_CommitConfiguration();
                    }
                    DismissPresetStatus();
                    RaiseAccessProperties();
                }
                catch (Exception exception)
                {
                    SetPresetStatus(ResolveSettingsUiTextSafe("Common.SettingsSourceLoadFailed", APIShared.Localization.UiText.English("Common.SettingsSourceLoadFailed")) + ": " + exception.Message, true);
                }
            }
        }
#endif
    }
}
