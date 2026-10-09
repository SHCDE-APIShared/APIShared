using System;
using System.ComponentModel;
#pragma warning disable 1591 // Public schema members are documented by the APIShared preset guide.

namespace APIShared.ModSettings
{
    /// <summary>Mutable row model used by the standard personal-preset save dialog.</summary>
    public sealed class PresetSaveSettingViewModel : INotifyPropertyChanged
    {
        private int selectedModeIndex = (int)PublishedPresetValueMode.Fixed;

        public PresetSaveSettingViewModel(PresetSettingDescriptor descriptor)
            : this(descriptor, descriptor?.Scope.ToString(), null)
        {
        }

        public PresetSaveSettingViewModel(
            PresetSettingDescriptor descriptor,
            string scopeText,
            string[] modeOptions)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            PropertyName = descriptor.PropertyName;
            RequiresRestart = descriptor.RequiresRestart;
            Group = descriptor.Group;
            DisplayName = string.IsNullOrEmpty(descriptor.DisplayName) ? PropertyName : descriptor.DisplayName;
            Scope = descriptor.Scope;
            ScopeText = string.IsNullOrWhiteSpace(scopeText) ? descriptor.Scope.ToString() : scopeText;
            ModeOptions = modeOptions != null && modeOptions.Length == 3
                ? (string[])modeOptions.Clone()
                : new[] { "ModDefault", "Player", "Fixed" };
        }

        public string PropertyName { get; }
        public bool RequiresRestart { get; }
        public string RestartHelp { get; set; } = "Restart required";
        public string SettingHelp => PropertyName + (RequiresRestart ? " — " + RestartHelp : "");
        public string Group { get; }
        public string DisplayName { get; }
        public PresetSettingScope Scope { get; }
        public string ScopeText { get; }
        public string[] ModeOptions { get; }

        public int SelectedModeIndex
        {
            get => selectedModeIndex;
            set
            {
                if (value < 0 || value > 2) value = (int)PublishedPresetValueMode.Fixed;
                if (selectedModeIndex == value) return;
                selectedModeIndex = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedModeIndex)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public PresetSaveSelection ToSelection() => new PresetSaveSelection
        {
            PropertyName = PropertyName,
            Mode = (PublishedPresetValueMode)SelectedModeIndex,
        };
    }

    /// <summary>One source-labelled row shown by the standard preset load/save dialogs.</summary>
    public sealed class ModSettingsPresetListEntry
    {
        internal PublishedModSettingsPreset Preset { get; set; }
        public string StableId => Preset?.StableId ?? string.Empty;
        public string Name => Preset?.Name ?? string.Empty;
        public string Description => Preset?.Description ?? string.Empty;
        public string ProviderGuid => Preset?.ProviderGuid ?? string.Empty;
        public string ProviderName => Preset?.ProviderName ?? string.Empty;
        public ModSettingsPresetSourceKind SourceKind =>
            Preset?.SourceKind ?? ModSettingsPresetSourceKind.Personal;
        public bool CanOverwrite => Preset?.CanOverwrite == true;
        public bool CanDelete => Preset?.SourceKind == ModSettingsPresetSourceKind.Personal;
        public string SourceLabel { get; internal set; } = string.Empty;
        public string DisplayText => SourceLabel + " · " + Name;
        public override string ToString() => DisplayText;
    }

    /// <summary>A deliberate destination offered by the personal-preset save dialog.</summary>
    public sealed class ModSettingsPresetSaveTarget
    {
        internal PublishedModSettingsPreset Preset { get; set; }
        public bool IsNew => Preset == null;
        public string Name => Preset?.Name ?? string.Empty;
        public string StableId => Preset?.StableId ?? string.Empty;
        public string DisplayText { get; internal set; } = string.Empty;
        public override string ToString() => DisplayText;
    }

}
