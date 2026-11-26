#if !API_SHARED_PRESET_TESTS
using APIShared.Localization;
using SHCDESE.NoesisUtil;
using System;
using System.Linq;
using Visibility = Noesis.Visibility;

namespace APIShared.ModSettings
{
    public abstract partial class PresetLobbyModSettingsViewModel
    {
        /// <summary>Localized caption for offering the selected saved preset to the lobby.</summary>
        public string System_PresetShareText => UiText.Get("PresetShare.Share");

        /// <summary>Localized description of the lobby offer action.</summary>
        public string System_PresetShareHelpText => UiText.Get("PresetShare.ShareHelp");

        /// <summary>Availability of the share action in a connected multiplayer lobby.</summary>
        public Visibility System_PresetShareVisibility => PresetShareService.IsLobbyAvailable
            ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Shows the share-only picker entry when the user cannot apply presets.</summary>
        public Visibility System_PresetShareOnlyPickerVisibility =>
            PresetShareService.IsLobbyAvailable && !CanChangePreset ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Localized label for opening the share-only picker.</summary>
        public string System_PresetShareOnlyPickerText => UiText.Get("PresetShare.OpenPicker");

        /// <summary>Localized tooltip for opening the share-only picker.</summary>
        public string System_PresetShareOnlyPickerHelpText => UiText.Get("PresetShare.OpenPickerHelp");

        /// <summary>Whether a saved preset is selected and can be offered.</summary>
        public bool System_CanSharePreset =>
            presetController != null && selectedPresetLoadEntry?.Preset != null && PresetShareService.IsLobbyAvailable;

        /// <summary>On the Unity thread, advertises the selected saved preset to compatible lobby peers.</summary>
        public RelayCommand System_SharePresetCommand { get; }

        private void RaisePresetShareAvailability()
        {
            base.OnPropertyChanged(nameof(System_PresetShareVisibility));
            base.OnPropertyChanged(nameof(System_PresetShareOnlyPickerVisibility));
            base.OnPropertyChanged(nameof(System_CanSharePreset));
        }

        private void ShareSelectedPreset()
        {
            if (!System_CanSharePreset)
                return;

            var preset = selectedPresetLoadEntry.Preset;
            try
            {
                string json = System_ExportSelectedPresetJson();
                DismissPresetStatus();
                int statusRevision = presetOperationStatusRevision;
                if (!PresetShareService.TryOffer(presetController.TargetGuid, preset.Name, json,
                    out PresetShareFailure failure, result =>
                    {
                        // A delayed delivery outcome belongs to the action that started it.
                        if (result != PresetShareFailure.None && statusRevision == presetOperationStatusRevision)
                            SetPresetStatus(PresetShareText.Failure(result), true);
                    }))
                    SetPresetStatus(PresetShareText.Failure(failure), true);
            }
            catch (Exception error)
            {
                presetController.LogPresetOperationFailure("share", preset.Id, error);
                SetPresetStatus(UiText.Get("PresetShare.Invalid"), true);
            }
        }

        internal string System_SharedPresetModName => presetController?.ModName;

        internal void System_ImportSharedPresetJson(string json, string name)
        {
            if (presetController == null || !PresetShareService.IsLobbyAvailable)
                throw new InvalidOperationException("The preset target is unavailable in this lobby.");

            presetController.ImportSharedPresetJson(json, name);
            if (presetLoadPanelOpen)
            {
                string selection = selectedPresetLoadEntry?.StableId;
                RebuildPresetDialogCatalogs();
                System_SelectedPresetLoadEntry = presetLoadEntries.FirstOrDefault(entry => entry.StableId == selection)
                    ?? presetLoadEntries.FirstOrDefault();
            }
        }
    }
}
#endif
