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
    /// <summary>Preset source selection, save/load UI and settings search bindings.</summary>
    public abstract partial class PresetLobbyModSettingsViewModel
    {
#if !API_SHARED_PRESET_TESTS
        /// <summary>Localized XAML caption/help for Common.PresetLoad; English fallback: Load preset</summary>
        public string System_PresetLoadText =>
            ResolveSettingsUiTextSafe("Common.PresetLoad", "Load preset");

        /// <summary>Localized XAML caption/help for Common.PresetSave; English fallback: Save preset</summary>
        public string System_PresetSaveText =>
            ResolveSettingsUiTextSafe("Common.PresetSave", "Save preset");

        /// <summary>Localized XAML caption/help for Common.SettingsSource; English fallback: Reset settings to</summary>
        public string System_SettingsSourceText =>
            ResolveSettingsUiTextSafe("Common.SettingsSource", "Reset settings to");

        /// <summary>Localized XAML caption/help for Common.SettingsSourceLoad; English fallback: Reset</summary>
        public string System_SettingsSourceLoadText =>
            ResolveSettingsUiTextSafe("Common.SettingsSourceLoad", "Reset");

        /// <summary>Localized XAML caption/help for Common.SettingsSourceHelp; English fallback: Resets this mod&apos;s settings to the selected source. Personal presets are not changed. In multiplayer, only the host can reset host settings.</summary>
        public string System_SettingsSourceHelpText =>
            ResolveSettingsUiTextSafe(
                "Common.SettingsSourceHelp",
                "Resets this mod's settings to the selected source. Personal presets are not changed. In multiplayer, only the host can reset host settings.");

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

        /// <summary>Application notice or the current source/modified description.</summary>
        public string System_PresetStatusText => !string.IsNullOrEmpty(System_ApplicationNotice) ? System_ApplicationNotice : presetController?.GetStatusText(
            ResolveSettingsUiTextSafe("Common.PresetBasedOn", "Based on"),
            ResolveSettingsUiTextSafe("Common.PresetModified", "modified"),
            ResolveSettingsUiTextSafe("Common.PresetSourcePersonal", "Personal presets"),
            ResolveSettingsUiTextSafe("Common.PresetSourceBundled", "Bundled with this mod"),
            ResolveSettingsUiTextSafe("Common.PresetSourceExternal", "External presets")) ?? string.Empty;

        /// <summary>Shows nonempty source/application status.</summary>
        public Visibility System_PresetStatusVisibility =>
            string.IsNullOrWhiteSpace(System_PresetStatusText) ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>Visibility of the current preset chooser.</summary>
        public Visibility System_PresetLoadPanelVisibility =>
            presetLoadPanelOpen ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Live discovered preset rows for the load chooser; selection does not apply a preset.</summary>
        public ObservableCollection<ModSettingsPresetListEntry> System_PresetLoadEntries =>
            presetLoadEntries;

        /// <summary>Current chooser selection; use the confirm command to apply it.</summary>
        public ModSettingsPresetListEntry System_SelectedPresetLoadEntry
        {
            get => selectedPresetLoadEntry;
            set
            {
                if (ReferenceEquals(selectedPresetLoadEntry, value)) return;
                selectedPresetLoadEntry = value;
                base.OnPropertyChanged(nameof(System_SelectedPresetLoadEntry));
                base.OnPropertyChanged(nameof(System_CanDeleteSelectedPreset));
                base.OnPropertyChanged(nameof(System_PresetDeleteVisibility));
            }
        }

        /// <summary>Localized XAML caption/help for Common.PresetLoadConfirm; English fallback: Load</summary>
        public string System_PresetLoadConfirmText =>
            ResolveSettingsUiTextSafe("Common.PresetLoadConfirm", "Load");

        /// <summary>Localized XAML caption/help for Common.PresetLoadCancel; English fallback: Cancel</summary>
        public string System_PresetLoadCancelText =>
            ResolveSettingsUiTextSafe("Common.PresetLoadCancel", "Cancel");

        /// <summary>Localized XAML caption/help for Common.PresetDelete; English fallback: Delete</summary>
        public string System_PresetDeleteText =>
            ResolveSettingsUiTextSafe("Common.PresetDelete", "Delete");

        /// <summary>Whether the selected row is a personal preset that may be deleted.</summary>
        public bool System_CanDeleteSelectedPreset =>
            selectedPresetLoadEntry?.CanDelete == true;

        /// <summary>Shows delete only for a selected personal preset.</summary>
        public Visibility System_PresetDeleteVisibility =>
            System_CanDeleteSelectedPreset ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Live choices for creating or overwriting personal presets.</summary>
        public ObservableCollection<ModSettingsPresetSaveTarget> System_PresetSaveTargets =>
            presetSaveTargets;

        /// <summary>Save destination; selecting an existing preset populates its name, description and resolution modes.</summary>
        public ModSettingsPresetSaveTarget System_SelectedPresetSaveTarget
        {
            get => selectedPresetSaveTarget;
            set
            {
                if (ReferenceEquals(selectedPresetSaveTarget, value)) return;
                selectedPresetSaveTarget = value;
                if (value?.Preset != null)
                {
                    presetSaveName = value.Preset.Name;
                    presetSaveDescription = value.Preset.Description;
                    ApplyPresetToSaveRows(value.Preset);
                }
                else if (value != null)
                {
                    ResetPresetSaveForm();
                }
                base.OnPropertyChanged(nameof(System_SelectedPresetSaveTarget));
                base.OnPropertyChanged(nameof(System_PresetSaveName));
                base.OnPropertyChanged(nameof(System_PresetSaveDescription));
                base.OnPropertyChanged(nameof(System_CanConfirmPresetSave));
                base.OnPropertyChanged(nameof(System_PresetSaveConfirmHelpText));
            }
        }

        /// <summary>Localized XAML caption/help for Common.PresetSaveTarget; English fallback: Save as</summary>
        public string System_PresetSaveTargetText =>
            ResolveSettingsUiTextSafe("Common.PresetSaveTarget", "Save as");

        /// <summary>Visibility of the personal-preset authoring panel.</summary>
        public Visibility System_PresetSavePanelVisibility =>
            presetSavePanelOpen ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Editable personal preset name; null becomes empty and empty names disable save confirmation.</summary>
        public string System_PresetSaveName
        {
            get => presetSaveName;
            set
            {
                string normalized = value ?? string.Empty;
                if (string.Equals(presetSaveName, normalized, StringComparison.Ordinal)) return;
                presetSaveName = normalized;
                base.OnPropertyChanged(nameof(System_PresetSaveName));
                base.OnPropertyChanged(nameof(System_CanConfirmPresetSave));
                base.OnPropertyChanged(nameof(System_PresetSaveConfirmHelpText));
            }
        }

        /// <summary>Editable optional preset description; null becomes empty.</summary>
        public string System_PresetSaveDescription
        {
            get => presetSaveDescription;
            set
            {
                string normalized = value ?? string.Empty;
                if (string.Equals(presetSaveDescription, normalized, StringComparison.Ordinal)) return;
                presetSaveDescription = normalized;
                base.OnPropertyChanged(nameof(System_PresetSaveDescription));
            }
        }

        /// <summary>Live per-setting resolution-mode rows for personal preset authoring.</summary>
        public ObservableCollection<PresetSaveSettingViewModel> System_PresetSaveSettings =>
            presetSaveSettings;

        /// <summary>Localized XAML caption/help for Common.PresetSaveName; English fallback: Preset name</summary>
        public string System_PresetSaveNameText =>
            ResolveSettingsUiTextSafe("Common.PresetSaveName", "Preset name");

        /// <summary>Localized XAML caption/help for Common.PresetSaveDescription; English fallback: Description (optional)</summary>
        public string System_PresetSaveDescriptionText =>
            ResolveSettingsUiTextSafe("Common.PresetSaveDescription", "Description (optional)");

        /// <summary>Localized XAML caption/help for Common.PresetSaveBulkMode; English fallback: Set all modes</summary>
        public string System_PresetSaveBulkModeText =>
            ResolveSettingsUiTextSafe("Common.PresetSaveBulkMode", "Set all modes");

        /// <summary>Localized XAML caption/help for Common.PresetSaveBulkModeHelp; English fallback: Default: use the mod default. Player: keep the player&apos;s current value. Fixed: apply the saved value. Host Fixed: fix host settings and keep player/local values.</summary>
        public string System_PresetSaveBulkModeHelpText =>
            ResolveSettingsUiTextSafe(
                "Common.PresetSaveBulkModeHelp",
                "Default: use the mod default. Player: keep the player's current value. Fixed: apply the saved value. Host Fixed: fix host settings and keep player/local values.");

        /// <summary>Localized XAML caption/help for Common.PresetLoadSelectionHelp; English fallback: Selecting a preset changes nothing until you choose Load.</summary>
        public string System_PresetLoadSelectionHelpText =>
            ResolveSettingsUiTextSafe(
                "Common.PresetLoadSelectionHelp",
                "Selecting a preset changes nothing until you choose Load.");

        /// <summary>Localized Default, Player, Fixed, Host Fixed and nonselectable Mixed choices in enum order.</summary>
        public ComboBoxItem[] System_PresetSaveBulkModeOptions => new[]
        {
            new ComboBoxItem { Content = ResolveSettingsUiTextSafe("Common.PresetModeDefault", "Default") },
            new ComboBoxItem { Content = ResolveSettingsUiTextSafe("Common.PresetModePlayer", "Player") },
            new ComboBoxItem { Content = ResolveSettingsUiTextSafe("Common.PresetModeFixed", "Fixed") },
            new ComboBoxItem { Content = ResolveSettingsUiTextSafe("Common.PresetModeHostFixed", "Host Fixed") },
            new ComboBoxItem
            {
                Content = ResolveSettingsUiTextSafe("Common.PresetModeMixed", "Mixed"),
                IsEnabled = false,
            },
        };

        /// <summary>Aggregate resolution mode; Host Fixed fixes host fields and preserves player/local values. Mixed is read-only; setting a supported index updates all rows.</summary>
        public int System_PresetSaveBulkModeIndex
        {
            get
            {
                if (presetSaveSettings.Count == 0)
                    return (int)PresetSaveBulkMode.HostFixed;
                int[] modes = presetSaveSettings.Select(item => item.SelectedModeIndex).Distinct().ToArray();
                if (modes.Length == 1)
                    return modes[0];
                if (presetSaveSettings.All(item =>
                    item.SelectedModeIndex == (int)(item.Scope == PresetSettingScope.Host
                        ? PublishedPresetValueMode.Fixed
                        : PublishedPresetValueMode.Player)))
                {
                    return (int)PresetSaveBulkMode.HostFixed;
                }
                return (int)PresetSaveBulkMode.Mixed;
            }
            set
            {
                if (value < 0 || value > (int)PresetSaveBulkMode.HostFixed)
                    return;
                applyingPresetSaveBulkMode = true;
                try
                {
                    foreach (PresetSaveSettingViewModel setting in presetSaveSettings)
                    {
                        setting.SelectedModeIndex = value == (int)PresetSaveBulkMode.HostFixed
                            ? (int)(setting.Scope == PresetSettingScope.Host
                                ? PublishedPresetValueMode.Fixed
                                : PublishedPresetValueMode.Player)
                            : value;
                    }
                }
                finally
                {
                    applyingPresetSaveBulkMode = false;
                }
                base.OnPropertyChanged(nameof(System_PresetSaveBulkModeIndex));
            }
        }

        /// <summary>Localized XAML caption/help for Common.PresetSaveConfirm; English fallback: Save</summary>
        public string System_PresetSaveConfirmText =>
            ResolveSettingsUiTextSafe("Common.PresetSaveConfirm", "Save");

        /// <summary>Whether a nonblank name permits save confirmation; persistence still validates the resulting preset.</summary>
        public bool System_CanConfirmPresetSave =>
            !string.IsNullOrWhiteSpace(presetSaveName);

        /// <summary>Save caption when a name exists, otherwise the localized missing-name explanation.</summary>
        public string System_PresetSaveConfirmHelpText => System_CanConfirmPresetSave
            ? System_PresetSaveConfirmText
            : ResolveSettingsUiTextSafe("Common.PresetSaveNameRequired", "Enter a preset name before saving.");

        /// <summary>Localized XAML caption/help for Common.PresetSaveCancel; English fallback: Cancel</summary>
        public string System_PresetSaveCancelText =>
            ResolveSettingsUiTextSafe("Common.PresetSaveCancel", "Cancel");

        /// <summary>Opens and populates the preset chooser; does not immediately apply a selection.</summary>
        public RelayCommand System_OpenPresetLoadCommand { get; }

        /// <summary>Applies the selected preset through the normal authority-filtered working-copy path.</summary>
        public RelayCommand System_ConfirmPresetLoadCommand { get; }

        /// <summary>Requests explicit deletion confirmation for a selected personal preset.</summary>
        public RelayCommand System_DeletePresetCommand { get; }

        /// <summary>Closes the load chooser without applying its selection.</summary>
        public RelayCommand System_CancelPresetLoadCommand { get; }

        /// <summary>Opens the save form using the current working configuration.</summary>
        public RelayCommand System_OpenPresetSaveCommand { get; }

        /// <summary>Saves the personal preset or requests confirmation before overwriting an existing target.</summary>
        public RelayCommand System_ConfirmPresetSaveCommand { get; }

        /// <summary>Closes personal preset authoring without saving.</summary>
        public RelayCommand System_CancelPresetSaveCommand { get; }

        /// <summary>Resets the editable working copy to the selected source, preserving personal preset files.</summary>
        public RelayCommand System_LoadSettingsSourceCommand { get; }

        /// <summary>Completes the pending delete or overwrite action.</summary>
        public RelayCommand System_ConfirmPresetInlineActionCommand { get; }

        /// <summary>Cancels the pending delete or overwrite action.</summary>
        public RelayCommand System_CancelPresetInlineActionCommand { get; }

        /// <summary>Clears the current operation-status message.</summary>
        public RelayCommand System_DismissPresetStatusCommand { get; }

        /// <summary>Title of the pending delete/overwrite confirmation.</summary>
        public string System_PresetInlineConfirmationTitle => presetInlineConfirmationTitle;
        /// <summary>Explanation of the pending destructive personal-preset action.</summary>
        public string System_PresetInlineConfirmationMessage => presetInlineConfirmationMessage;
        /// <summary>Shows confirmation while delete or overwrite is pending.</summary>
        public Visibility System_PresetInlineConfirmationVisibility =>
            pendingDeletePreset != null || pendingOverwriteId != null ? Visibility.Visible : Visibility.Collapsed;
        /// <summary>Localized XAML caption/help for Common.PresetConfirm; English fallback: Confirm</summary>
        public string System_PresetInlineConfirmText => ResolveSettingsUiTextSafe("Common.PresetConfirm", "Confirm");
        /// <summary>Localized XAML caption/help for Common.PresetSaveCancel; English fallback: Cancel</summary>
        public string System_PresetInlineCancelText => ResolveSettingsUiTextSafe("Common.PresetSaveCancel", "Cancel");
        /// <summary>Most recent preset operation message.</summary>
        public string System_PresetOperationStatusText => presetOperationStatus;
        /// <summary>Shows a nonempty failed-operation message; successful operations do not leave a persistent banner.</summary>
        public Visibility System_PresetOperationStatusVisibility =>
            presetOperationFailed && !string.IsNullOrWhiteSpace(presetOperationStatus)
                ? Visibility.Visible
                : Visibility.Collapsed;
        /// <summary>Shows a nonempty failed-operation message.</summary>
        public Visibility System_PresetOperationErrorVisibility =>
            presetOperationFailed && !string.IsNullOrWhiteSpace(presetOperationStatus) ? Visibility.Visible : Visibility.Collapsed;
        /// <summary>Always collapsed; retained as a compatible XAML binding.</summary>
        public Visibility System_PresetOperationSuccessVisibility =>
            Visibility.Collapsed;
        /// <summary>Localized XAML caption/help for Common.PresetStatusDismiss; English fallback: Close</summary>
        public string System_PresetStatusDismissText => ResolveSettingsUiTextSafe("Common.PresetStatusDismiss", "Close");

        /// <summary>Editable free-text filter; changing it clears exact-key targeting.</summary>
        public string System_ModSettingsSearchText
        {
            get => modSettingsSearchText;
            set
            {
                string normalized = value ?? string.Empty;
                if (string.Equals(modSettingsSearchText, normalized, StringComparison.Ordinal) &&
                    modSettingsSearchExactKey.Length == 0)
                {
                    return;
                }
                modSettingsSearchText = normalized;
                modSettingsSearchExactKey = string.Empty;
                RaiseModSettingsSearchProperties();
            }
        }

        /// <summary>Whether free-text search includes tooltip content as well as visible titles.</summary>
        public bool System_ModSettingsSearchIncludeToolTips
        {
            get => modSettingsSearchIncludeToolTips;
            set
            {
                if (modSettingsSearchIncludeToolTips == value)
                    return;
                modSettingsSearchIncludeToolTips = value;
                RaiseModSettingsSearchProperties();
            }
        }

        /// <summary>Exact target key used by external search navigation, independent of free-text matching.</summary>
        public string System_ModSettingsSearchExactKey => modSettingsSearchExactKey;

        /// <summary>Incrementing focus token for the attached FocusRequest binding.</summary>
        public int System_ModSettingsSearchFocusRequest => modSettingsSearchFocusRequest;

        /// <summary>Whether either free-text filtering or exact-key targeting is active.</summary>
        public bool System_ModSettingsSearchHasActiveFilter =>
            modSettingsSearchExactKey.Length > 0 ||
            !string.IsNullOrWhiteSpace(modSettingsSearchText);

        /// <summary>Shows the expanded search controls.</summary>
        public Visibility System_ModSettingsSearchPanelVisibility =>
            modSettingsSearchExpanded ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Shows the collapsed search affordance.</summary>
        public Visibility System_ModSettingsSearchInactiveVisibility =>
            System_ModSettingsSearchHasActiveFilter ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>Shows no-results feedback for an active filter with no catalog matches.</summary>
        public Visibility System_ModSettingsSearchNoResultsVisibility =>
            System_ModSettingsSearchHasActiveFilter &&
            !ModSettingsSearch.HasMatches(
                this,
                modSettingsSearchText,
                modSettingsSearchIncludeToolTips,
                modSettingsSearchExactKey)
                ? Visibility.Visible
                : Visibility.Collapsed;

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchLabel; English fallback: Search</summary>
        public string System_ModSettingsSearchLabelText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchLabel", "Search");

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchHelp; English fallback: Search setting titles. Optionally include tooltips.</summary>
        public string System_ModSettingsSearchHelpText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchHelp", "Search setting titles. Optionally include tooltips.");

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchToggleHelp; English fallback: Show or hide the settings search.</summary>
        public string System_ModSettingsSearchToggleHelpText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchToggleHelp", "Show or hide the settings search.");

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchIncludeToolTips; English fallback: Search tooltips</summary>
        public string System_ModSettingsSearchIncludeToolTipsText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchIncludeToolTips", "Search tooltips");

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchIncludeToolTipsHelp; English fallback: Also search the explanatory tooltips of settings.</summary>
        public string System_ModSettingsSearchIncludeToolTipsHelpText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchIncludeToolTipsHelp", "Also search the explanatory tooltips of settings.");

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchClearHelp; English fallback: Clear the settings filter.</summary>
        public string System_ModSettingsSearchClearHelpText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchClearHelp", "Clear the settings filter.");

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchNoResults; English fallback: No matching settings found.</summary>
        public string System_ModSettingsSearchNoResultsText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchNoResults", "No matching settings found.");

        /// <summary>Expands or collapses settings search and requests focus when opening.</summary>
        public RelayCommand System_ToggleModSettingsSearchCommand { get; }

        /// <summary>Clears free-text and exact-key targeting.</summary>
        public RelayCommand System_ClearModSettingsSearchCommand { get; }

        private void OpenPresetLoad()
        {
            if (presetLoadPanelOpen)
            {
                presetLoadPanelOpen = false;
                RaisePresetDialogProperties();
                return;
            }
            presetSavePanelOpen = false;
            presetController?.RefreshCatalog();
            RebuildPresetDialogCatalogs();
            selectedPresetLoadEntry = presetLoadEntries.FirstOrDefault();
            presetLoadPanelOpen = true;
            RaisePresetDialogProperties();
        }

        private void ConfirmPresetLoad()
        {
            if (selectedPresetLoadEntry?.Preset == null)
                return;

            try
            {
                ApplyConfirmedPresetSelection(selectedPresetLoadEntry.Preset);
                presetLoadPanelOpen = false;
                RaisePresetDialogProperties();
                RaiseAccessProperties();
                DismissPresetStatus();
            }
            catch (Exception exception)
            {
                SetPresetStatus(
                    ResolveSettingsUiTextSafe("Common.PresetLoadFailedTitle", "Preset load failed") + ": " + exception.Message,
                    true);
            }
        }

        private void CancelPresetLoad()
        {
            presetLoadPanelOpen = false;
            RaisePresetDialogProperties();
        }

        private void DeleteSelectedPreset()
        {
            PublishedModSettingsPreset preset = selectedPresetLoadEntry?.Preset;
            if (preset == null || !System_CanDeleteSelectedPreset)
                return;

            string message = ResolveSettingsUiTextSafe(
                "Common.PresetDeleteConfirm",
                "The personal preset will be permanently deleted. Continue?") +
                Environment.NewLine + Environment.NewLine + preset.Name;
            pendingDeletePreset = preset;
            pendingOverwriteId = null;
            pendingOverwriteSelections = null;
            presetInlineConfirmationTitle = ResolveSettingsUiTextSafe("Common.PresetDeleteTitle", "Delete personal preset");
            presetInlineConfirmationMessage = message;
            RaisePresetInlineProperties();
        }

        private void RebuildSettingsSources()
        {
            if (presetController == null) return;
            string previous = selectedSettingsSource?.Id;
            settingsSources.Clear();
            settingsSources.Add(new ModSettingsWorkingSource
            {
                Id = ModSettingsWorkingSourceRegistry.ModDefaultsId,
                Kind = ModSettingsWorkingSourceKind.ModDefault,
                DisplayName = ResolveSettingsUiTextSafe("Common.SettingsSourceDefaults", "Mod defaults"),
            });
            foreach (ModSettingsWorkingSource source in ModSettingsWorkingSourceRegistry.GetProviderSources(presetController.TargetGuid))
            {
                if (source != null && !string.IsNullOrWhiteSpace(source.Id) &&
                    !string.Equals(source.Id, ModSettingsWorkingSourceRegistry.ModDefaultsId, StringComparison.Ordinal))
                {
                    if (source.Kind == ModSettingsWorkingSourceKind.Trail)
                        source.DisplayName = ResolveSettingsUiTextSafe("Common.SettingsSourceTrail", "Trail settings");
                    else if (source.Kind == ModSettingsWorkingSourceKind.Map)
                        source.DisplayName = ResolveSettingsUiTextSafe("Common.SettingsSourceMap", "Map settings");
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
            ModSettingsWorkingSource source = selectedSettingsSource;
            if (source == null || !System_CanLoadSettingsSource) return;
            try
            {
                System_RefreshOwnConfiguration();
                if (string.Equals(source.Id, ModSettingsWorkingSourceRegistry.ModDefaultsId, StringComparison.Ordinal))
                {
                    System_LoadModDefaults();
                }
                else
                {
                    ModSettingsWorkingSourceRegistry.Apply(presetController.TargetGuid, source.Id);
                    System_CommitConfiguration();
                }
                DismissPresetStatus();
                RaiseAccessProperties();
            }
            catch (Exception exception)
            {
                SetPresetStatus(ResolveSettingsUiTextSafe("Common.SettingsSourceLoadFailed", "Could not reset settings") + ": " + exception.Message, true);
            }
        }

        private void CompletePresetDelete(PublishedModSettingsPreset preset)
        {
            try
            {
                presetController?.DeletePersonalPreset(preset);
                RebuildPresetDialogCatalogs();
                selectedPresetLoadEntry = presetLoadEntries.FirstOrDefault();
                RaisePresetDialogProperties();
                RaiseAccessProperties();
                DismissPresetStatus();
            }
            catch (Exception exception)
            {
                presetController?.LogPresetOperationFailure("delete", preset?.Id, exception);
                SetPresetStatus(ResolveSettingsUiTextSafe("Common.PresetDeleteFailedTitle", "Preset deletion failed") + ": " + exception.Message, true);
            }
        }

        private void OpenPresetSave()
        {
            if (presetSavePanelOpen)
            {
                presetSavePanelOpen = false;
                RaisePresetSaveProperties();
                return;
            }
            presetLoadPanelOpen = false;
            presetController?.RefreshCatalog();
            RebuildPresetDialogCatalogs();
            selectedPresetSaveTarget = presetSaveTargets.FirstOrDefault();
            ExecutePresetAction();
            base.OnPropertyChanged(nameof(System_SelectedPresetSaveTarget));
        }

        private void RebuildPresetDialogCatalogs()
        {
            presetLoadEntries.Clear();
            presetSaveTargets.Clear();
            presetSaveTargets.Add(new ModSettingsPresetSaveTarget
            {
                DisplayText = ResolveSettingsUiTextSafe("Common.PresetSaveNew", "New personal preset"),
            });

            foreach (PublishedModSettingsPreset preset in presetController?.PublishedPresets ??
                Array.Empty<PublishedModSettingsPreset>())
            {
                string sourceLabel;
                switch (preset.SourceKind)
                {
                    case ModSettingsPresetSourceKind.Personal:
                        sourceLabel = ResolveSettingsUiTextSafe("Common.PresetSourcePersonal", "Personal presets");
                        break;
                    case ModSettingsPresetSourceKind.Bundled:
                        sourceLabel = ResolveSettingsUiTextSafe("Common.PresetSourceBundled", "Bundled with this mod");
                        break;
                    default:
                        sourceLabel = ResolveSettingsUiTextSafe("Common.PresetSourceExternal", "External presets") +
                            ": " + preset.ProviderName;
                        break;
                }
                presetLoadEntries.Add(new ModSettingsPresetListEntry
                {
                    Preset = preset,
                    SourceLabel = sourceLabel,
                });
                if (preset.CanOverwrite)
                {
                    presetSaveTargets.Add(new ModSettingsPresetSaveTarget
                    {
                        Preset = preset,
                        DisplayText = preset.Name,
                    });
                }
            }
        }

        private void ApplyPresetToSaveRows(PublishedModSettingsPreset preset)
        {
            if (preset == null)
                return;
            foreach (PresetSaveSettingViewModel row in presetSaveSettings)
            {
                if (preset.Settings.TryGetValue(row.PropertyName, out PublishedPresetSetting setting))
                {
                    row.SelectedModeIndex = (int)setting.Mode;
                }
                else
                {
                    row.SelectedModeIndex = (int)PublishedPresetValueMode.Player;
                }
            }
        }

        private void RaisePresetDialogProperties()
        {
            base.OnPropertyChanged(nameof(System_PresetLoadPanelVisibility));
            base.OnPropertyChanged(nameof(System_PresetSavePanelVisibility));
            base.OnPropertyChanged(nameof(System_PresetLoadEntries));
            base.OnPropertyChanged(nameof(System_SelectedPresetLoadEntry));
            base.OnPropertyChanged(nameof(System_CanDeleteSelectedPreset));
            base.OnPropertyChanged(nameof(System_PresetDeleteVisibility));
            base.OnPropertyChanged(nameof(System_PresetSaveTargets));
            base.OnPropertyChanged(nameof(System_SelectedPresetSaveTarget));
            base.OnPropertyChanged(nameof(System_PresetStatusText));
            base.OnPropertyChanged(nameof(System_PresetStatusVisibility));
        }

        private void ExecutePresetAction()
        {
            presetSaveSettings.Clear();
            string[] modeOptions =
            {
                ResolveSettingsUiTextSafe("Common.PresetModeDefault", "Default"),
                ResolveSettingsUiTextSafe("Common.PresetModePlayer", "Player"),
                ResolveSettingsUiTextSafe("Common.PresetModeFixed", "Fixed"),
            };
            foreach (PresetSettingDescriptor descriptor in System_GetPresetSettingDescriptors())
            {
                var setting = new PresetSaveSettingViewModel(
                    descriptor,
                    ResolvePresetSettingScopeText(descriptor.Scope),
                    modeOptions);
                setting.RestartHelp = ResolveSettingsUiTextSafe("Common.RestartRequiredOption", "Restart required");
                setting.PropertyChanged += OnPresetSaveSettingPropertyChanged;
                presetSaveSettings.Add(setting);
            }
            ResetPresetSaveForm();
            presetSavePanelOpen = true;
            RaisePresetSaveProperties();
        }

        private void ResetPresetSaveForm()
        {
            foreach (PresetSaveSettingViewModel setting in presetSaveSettings)
            {
                setting.SelectedModeIndex = (int)(setting.Scope == PresetSettingScope.Host
                    ? PublishedPresetValueMode.Fixed
                    : PublishedPresetValueMode.Player);
            }
            presetSaveName = string.Empty;
            presetSaveDescription = string.Empty;
            base.OnPropertyChanged(nameof(System_PresetSaveBulkModeIndex));
        }

        private void OnPresetSaveSettingPropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            if (!applyingPresetSaveBulkMode &&
                string.Equals(args?.PropertyName, nameof(PresetSaveSettingViewModel.SelectedModeIndex), StringComparison.Ordinal))
            {
                base.OnPropertyChanged(nameof(System_PresetSaveBulkModeIndex));
            }
        }

        private string ResolvePresetSettingScopeText(PresetSettingScope scope)
        {
            switch (scope)
            {
                case PresetSettingScope.Host:
                    return ResolveSettingsUiTextSafe("Common.PresetScopeHost", "Host");
                case PresetSettingScope.Player:
                    return ResolveSettingsUiTextSafe("Common.PresetScopePlayer", "Player");
                case PresetSettingScope.Local:
                    return ResolveSettingsUiTextSafe("Common.PresetScopeLocal", "Local");
                default:
                    return scope.ToString();
            }
        }

        private void ConfirmPresetSave()
        {
            try
            {
                PresetSaveSelection[] selections = CreatePresetSaveSelections();
                PublishedModSettingsPreset existing = selectedPresetSaveTarget?.Preset;
                if (existing != null)
                {
                    pendingDeletePreset = null;
                    pendingOverwriteId = existing.Id;
                    pendingOverwriteSelections = selections;
                    presetInlineConfirmationTitle = ResolveSettingsUiTextSafe("Common.PresetSaveOverwriteTitle", "Overwrite personal preset");
                    presetInlineConfirmationMessage = ResolveSettingsUiTextSafe("Common.PresetSaveOverwrite", "The selected personal preset will be completely replaced. Continue?");
                    RaisePresetInlineProperties();
                    return;
                }

                string id = presetController?.CreateUniquePersonalPresetId(presetSaveName) ??
                    CreatePublishedPresetId(presetSaveName);
                System_SavePersonalPreset(
                    id,
                    presetSaveName,
                    presetSaveDescription,
                    selections,
                    overwrite: false);
                ShowPresetSaveCompleted();
            }
            catch (Exception exception)
            {
                presetController?.LogPresetOperationFailure("save", selectedPresetSaveTarget?.Preset?.Id, exception);
                ShowPresetSaveError(exception);
            }
        }

        private PresetSaveSelection[] CreatePresetSaveSelections() =>
            presetSaveSettings.Select(item => item.ToSelection()).ToArray();

        private void CompletePresetSave(string id, PresetSaveSelection[] selections, bool overwrite)
        {
            try
            {
                System_SavePersonalPreset(
                    id,
                    presetSaveName,
                    presetSaveDescription,
                    selections,
                    overwrite);
                ShowPresetSaveCompleted();
            }
            catch (Exception exception)
            {
                presetController?.LogPresetOperationFailure(overwrite ? "overwrite" : "save", id, exception);
                ShowPresetSaveError(exception);
            }
        }

        private void ShowPresetSaveCompleted()
        {
            presetSavePanelOpen = false;
            presetController?.RefreshCatalog();
            RebuildPresetDialogCatalogs();
            RaisePresetSaveProperties();
            DismissPresetStatus();
        }

        private void ShowPresetSaveError(Exception exception)
        {
            SetPresetStatus(ResolveSettingsUiTextSafe("Common.PresetSaveFailedTitle", "Preset save failed") + ": " + exception.Message, true);
        }

        private void ConfirmPresetInlineAction()
        {
            PublishedModSettingsPreset delete = pendingDeletePreset;
            string overwriteId = pendingOverwriteId;
            PresetSaveSelection[] selections = pendingOverwriteSelections;
            ClearPresetInlineConfirmation();
            if (delete != null) CompletePresetDelete(delete);
            else if (overwriteId != null) CompletePresetSave(overwriteId, selections ?? Array.Empty<PresetSaveSelection>(), true);
        }

        private void CancelPresetInlineAction()
        {
            if (pendingDeletePreset != null) presetController?.LogPresetOperationCancelled("delete", pendingDeletePreset.Id);
            else if (pendingOverwriteId != null) presetController?.LogPresetOperationCancelled("overwrite", pendingOverwriteId);
            ClearPresetInlineConfirmation();
        }

        private void ClearPresetInlineConfirmation()
        {
            pendingDeletePreset = null;
            pendingOverwriteId = null;
            pendingOverwriteSelections = null;
            presetInlineConfirmationTitle = string.Empty;
            presetInlineConfirmationMessage = string.Empty;
            RaisePresetInlineProperties();
        }

        private void SetPresetStatus(string message, bool failed)
        {
            presetOperationStatus = message ?? string.Empty;
            presetOperationFailed = failed;
            RaisePresetInlineProperties();
        }

        private void DismissPresetStatus() => SetPresetStatus(string.Empty, false);

        private void RaisePresetInlineProperties()
        {
            base.OnPropertyChanged(nameof(System_PresetInlineConfirmationTitle));
            base.OnPropertyChanged(nameof(System_PresetInlineConfirmationMessage));
            base.OnPropertyChanged(nameof(System_PresetInlineConfirmationVisibility));
            base.OnPropertyChanged(nameof(System_PresetOperationStatusText));
            base.OnPropertyChanged(nameof(System_PresetOperationStatusVisibility));
            base.OnPropertyChanged(nameof(System_PresetOperationErrorVisibility));
            base.OnPropertyChanged(nameof(System_PresetOperationSuccessVisibility));
        }

        private void CancelPresetSave()
        {
            presetSavePanelOpen = false;
            RaisePresetSaveProperties();
        }

        private void RaisePresetSaveProperties()
        {
            base.OnPropertyChanged(nameof(System_PresetSavePanelVisibility));
            base.OnPropertyChanged(nameof(System_PresetSaveName));
            base.OnPropertyChanged(nameof(System_PresetSaveDescription));
            base.OnPropertyChanged(nameof(System_PresetSaveSettings));
            base.OnPropertyChanged(nameof(System_PresetSaveBulkModeIndex));
            base.OnPropertyChanged(nameof(System_CanConfirmPresetSave));
            base.OnPropertyChanged(nameof(System_PresetSaveConfirmHelpText));
            RaisePresetDialogProperties();
        }

        private static string CreatePublishedPresetId(string name)
        {
            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                throw new InvalidDataException("A preset name is required.");
            var result = new System.Text.StringBuilder(trimmed.Length);
            bool separator = false;
            foreach (char character in trimmed.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_')
                {
                    result.Append(character);
                    separator = false;
                }
                else if (!separator)
                {
                    result.Append('-');
                    separator = true;
                }
            }
            string id = result.ToString().Trim('-');
            if (id.Length == 0)
                throw new InvalidDataException("The preset name does not contain a usable id.");
            return id.Length <= 128 ? id : id.Substring(0, 128).TrimEnd('-');
        }

        /// <summary>Safe reflection bridge used by the optional global search host.</summary>
        public bool System_ApplyModSettingsSearchTarget(string key, string title)
        {
            string normalizedKey = ModSettingsSearchMatcher.Normalize(key);
            if (normalizedKey.Length == 0)
                return false;

            modSettingsSearchText = title ?? string.Empty;
            modSettingsSearchExactKey = normalizedKey;
            modSettingsSearchExpanded = true;
            RaiseModSettingsSearchProperties();
            return true;
        }

        private void ToggleModSettingsSearch()
        {
            modSettingsSearchExpanded = !modSettingsSearchExpanded;
            RaiseModSettingsSearchProperties();
            if (!modSettingsSearchExpanded)
                return;

            unchecked
            {
                modSettingsSearchFocusRequest++;
                if (modSettingsSearchFocusRequest <= 0)
                    modSettingsSearchFocusRequest = 1;
            }
            base.OnPropertyChanged(nameof(System_ModSettingsSearchFocusRequest));
        }

        private void ClearModSettingsSearch()
        {
            modSettingsSearchText = string.Empty;
            modSettingsSearchExactKey = string.Empty;
            RaiseModSettingsSearchProperties();
        }

        private void RaiseModSettingsSearchProperties()
        {
            base.OnPropertyChanged(nameof(System_ModSettingsSearchText));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchIncludeToolTips));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchExactKey));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchHasActiveFilter));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchPanelVisibility));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchInactiveVisibility));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchNoResultsVisibility));
        }
#endif
    }
}
