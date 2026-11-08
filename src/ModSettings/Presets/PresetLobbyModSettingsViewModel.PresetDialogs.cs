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
            ResolveSettingsUiTextSafe("Common.PresetLoad", APIShared.Localization.UiText.English("Common.PresetLoad"));

        /// <summary>Localized XAML caption/help for Common.PresetSave; English fallback: Save preset</summary>
        public string System_PresetSaveText =>
            ResolveSettingsUiTextSafe("Common.PresetSave", APIShared.Localization.UiText.English("Common.PresetSave"));

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
            ResolveSettingsUiTextSafe("Common.PresetLoadConfirm", APIShared.Localization.UiText.English("Common.PresetLoadConfirm"));

        /// <summary>Localized XAML caption/help for Common.PresetLoadCancel; English fallback: Cancel</summary>
        public string System_PresetLoadCancelText =>
            ResolveSettingsUiTextSafe("Common.PresetLoadCancel", APIShared.Localization.UiText.English("Common.PresetLoadCancel"));

        /// <summary>Localized XAML caption/help for Common.PresetDelete; English fallback: Delete</summary>
        public string System_PresetDeleteText =>
            ResolveSettingsUiTextSafe("Common.PresetDelete", APIShared.Localization.UiText.English("Common.PresetDelete"));

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
            ResolveSettingsUiTextSafe("Common.PresetSaveTarget", APIShared.Localization.UiText.English("Common.PresetSaveTarget"));

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
            ResolveSettingsUiTextSafe("Common.PresetSaveName", APIShared.Localization.UiText.English("Common.PresetSaveName"));

        /// <summary>Localized XAML caption/help for Common.PresetSaveDescription; English fallback: Description (optional)</summary>
        public string System_PresetSaveDescriptionText =>
            ResolveSettingsUiTextSafe("Common.PresetSaveDescription", APIShared.Localization.UiText.English("Common.PresetSaveDescription"));

        /// <summary>Localized XAML caption/help for Common.PresetSaveBulkMode; English fallback: Set all modes</summary>
        public string System_PresetSaveBulkModeText =>
            ResolveSettingsUiTextSafe("Common.PresetSaveBulkMode", APIShared.Localization.UiText.English("Common.PresetSaveBulkMode"));

        /// <summary>Localized XAML caption/help for Common.PresetSaveBulkModeHelp; English fallback: Default: use the mod default. Player: keep the player&apos;s current value. Fixed: apply the saved value. Host Fixed: fix host settings and keep player/local values.</summary>
        public string System_PresetSaveBulkModeHelpText =>
            ResolveSettingsUiTextSafe("Common.PresetSaveBulkModeHelp", APIShared.Localization.UiText.English("Common.PresetSaveBulkModeHelp"));

        /// <summary>Localized XAML caption/help for Common.PresetLoadSelectionHelp; English fallback: Selecting a preset changes nothing until you choose Load.</summary>
        public string System_PresetLoadSelectionHelpText =>
            ResolveSettingsUiTextSafe("Common.PresetLoadSelectionHelp", APIShared.Localization.UiText.English("Common.PresetLoadSelectionHelp"));

        /// <summary>Localized Default, Player, Fixed, Host Fixed and nonselectable Mixed choices in enum order.</summary>
        public ComboBoxItem[] System_PresetSaveBulkModeOptions => new[]
        {
            new ComboBoxItem { Content = ResolveSettingsUiTextSafe("Common.PresetModeDefault", APIShared.Localization.UiText.English("Common.PresetModeDefault")) },
            new ComboBoxItem { Content = ResolveSettingsUiTextSafe("Common.PresetModePlayer", APIShared.Localization.UiText.English("Common.PresetModePlayer")) },
            new ComboBoxItem { Content = ResolveSettingsUiTextSafe("Common.PresetModeFixed", APIShared.Localization.UiText.English("Common.PresetModeFixed")) },
            new ComboBoxItem { Content = ResolveSettingsUiTextSafe("Common.PresetModeHostFixed", APIShared.Localization.UiText.English("Common.PresetModeHostFixed")) },
            new ComboBoxItem
            {
                Content = ResolveSettingsUiTextSafe("Common.PresetModeMixed", APIShared.Localization.UiText.English("Common.PresetModeMixed")),
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
            ResolveSettingsUiTextSafe("Common.PresetSaveConfirm", APIShared.Localization.UiText.English("Common.PresetSaveConfirm"));

        /// <summary>Whether a nonblank name permits save confirmation; persistence still validates the resulting preset.</summary>
        public bool System_CanConfirmPresetSave =>
            !string.IsNullOrWhiteSpace(presetSaveName);

        /// <summary>Save caption when a name exists, otherwise the localized missing-name explanation.</summary>
        public string System_PresetSaveConfirmHelpText => System_CanConfirmPresetSave
            ? System_PresetSaveConfirmText
            : ResolveSettingsUiTextSafe("Common.PresetSaveNameRequired", APIShared.Localization.UiText.English("Common.PresetSaveNameRequired"));

        /// <summary>Localized XAML caption/help for Common.PresetSaveCancel; English fallback: Cancel</summary>
        public string System_PresetSaveCancelText =>
            ResolveSettingsUiTextSafe("Common.PresetSaveCancel", APIShared.Localization.UiText.English("Common.PresetSaveCancel"));

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
        public string System_PresetInlineConfirmText => ResolveSettingsUiTextSafe("Common.PresetConfirm", APIShared.Localization.UiText.English("Common.PresetConfirm"));
        /// <summary>Localized XAML caption/help for Common.PresetSaveCancel; English fallback: Cancel</summary>
        public string System_PresetInlineCancelText => ResolveSettingsUiTextSafe("Common.PresetSaveCancel", APIShared.Localization.UiText.English("Common.PresetSaveCancel"));
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
        public string System_PresetStatusDismissText => ResolveSettingsUiTextSafe("Common.PresetStatusDismiss", APIShared.Localization.UiText.English("Common.PresetStatusDismiss"));

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
                    ResolveSettingsUiTextSafe("Common.PresetLoadFailedTitle", APIShared.Localization.UiText.English("Common.PresetLoadFailedTitle")) + ": " + exception.Message,
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

            string message = ResolveSettingsUiTextSafe("Common.PresetDeleteConfirm", APIShared.Localization.UiText.English("Common.PresetDeleteConfirm")) +
                Environment.NewLine + Environment.NewLine + preset.Name;
            pendingDeletePreset = preset;
            pendingOverwriteId = null;
            pendingOverwriteSelections = null;
            presetInlineConfirmationTitle = ResolveSettingsUiTextSafe("Common.PresetDeleteTitle", APIShared.Localization.UiText.English("Common.PresetDeleteTitle"));
            presetInlineConfirmationMessage = message;
            RaisePresetInlineProperties();
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
                SetPresetStatus(ResolveSettingsUiTextSafe("Common.PresetDeleteFailedTitle", APIShared.Localization.UiText.English("Common.PresetDeleteFailedTitle")) + ": " + exception.Message, true);
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
                DisplayText = ResolveSettingsUiTextSafe("Common.PresetSaveNew", APIShared.Localization.UiText.English("Common.PresetSaveNew")),
            });

            foreach (PublishedModSettingsPreset preset in presetController?.PublishedPresets ??
                Array.Empty<PublishedModSettingsPreset>())
            {
                string sourceLabel;
                switch (preset.SourceKind)
                {
                    case ModSettingsPresetSourceKind.Personal:
                        sourceLabel = ResolveSettingsUiTextSafe("Common.PresetSourcePersonal", APIShared.Localization.UiText.English("Common.PresetSourcePersonal"));
                        break;
                    case ModSettingsPresetSourceKind.Bundled:
                        sourceLabel = ResolveSettingsUiTextSafe("Common.PresetSourceBundled", APIShared.Localization.UiText.English("Common.PresetSourceBundled"));
                        break;
                    default:
                        sourceLabel = ResolveSettingsUiTextSafe("Common.PresetSourceExternal", APIShared.Localization.UiText.English("Common.PresetSourceExternal")) +
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
                ResolveSettingsUiTextSafe("Common.PresetModeDefault", APIShared.Localization.UiText.English("Common.PresetModeDefault")),
                ResolveSettingsUiTextSafe("Common.PresetModePlayer", APIShared.Localization.UiText.English("Common.PresetModePlayer")),
                ResolveSettingsUiTextSafe("Common.PresetModeFixed", APIShared.Localization.UiText.English("Common.PresetModeFixed")),
            };
            foreach (PresetSettingDescriptor descriptor in System_GetPresetSettingDescriptors())
            {
                var setting = new PresetSaveSettingViewModel(
                    descriptor,
                    ResolvePresetSettingScopeText(descriptor.Scope),
                    modeOptions);
                setting.RestartHelp = ResolveSettingsUiTextSafe("Common.RestartRequiredOption", APIShared.Localization.UiText.English("Common.RestartRequiredOption"));
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
                    return ResolveSettingsUiTextSafe("Common.PresetScopeHost", APIShared.Localization.UiText.English("Common.PresetScopeHost"));
                case PresetSettingScope.Player:
                    return ResolveSettingsUiTextSafe("Common.PresetScopePlayer", APIShared.Localization.UiText.English("Common.PresetScopePlayer"));
                case PresetSettingScope.Local:
                    return ResolveSettingsUiTextSafe("Common.PresetScopeLocal", APIShared.Localization.UiText.English("Common.PresetScopeLocal"));
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
                    presetInlineConfirmationTitle = ResolveSettingsUiTextSafe("Common.PresetSaveOverwriteTitle", APIShared.Localization.UiText.English("Common.PresetSaveOverwriteTitle"));
                    presetInlineConfirmationMessage = ResolveSettingsUiTextSafe("Common.PresetSaveOverwrite", APIShared.Localization.UiText.English("Common.PresetSaveOverwrite"));
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
            SetPresetStatus(ResolveSettingsUiTextSafe("Common.PresetSaveFailedTitle", APIShared.Localization.UiText.English("Common.PresetSaveFailedTitle")) + ": " + exception.Message, true);
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
#endif
    }
}
