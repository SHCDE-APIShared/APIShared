using System;
using System.ComponentModel;

namespace APIShared.ModSettings
{
    /// <summary>Mutable row model used by the standard personal-preset save dialog.</summary>
    public sealed class PresetSaveSettingViewModel : INotifyPropertyChanged
    {
        private int selectedModeIndex = (int)PublishedPresetValueMode.Fixed;

        /// <summary>Creates a save-dialog row from a non-null descriptor; optional localized scope/mode labels fall back to defaults. Mode labels are copied when exactly three are supplied.</summary>
        public PresetSaveSettingViewModel(PresetSettingDescriptor descriptor)
            : this(descriptor, descriptor?.Scope.ToString(), null)
        {
        }

        /// <summary>Creates a save-dialog row from a non-null descriptor; optional localized scope/mode labels fall back to defaults. Mode labels are copied when exactly three are supplied.</summary>
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

        /// <summary>Exact persistent setting key; use the declared property name, not its translated label.</summary>
        public string PropertyName { get; }
        /// <summary>Whether changes to this setting require startup application instead of live application.</summary>
        public bool RequiresRestart { get; }
        /// <summary>Localized restart explanation appended to the setting tooltip when required.</summary>
        public string RestartHelp { get; set; } = "Restart required";
        /// <summary>Stable property key with any restart explanation for the save-dialog tooltip.</summary>
        public string SettingHelp => PropertyName + (RequiresRestart ? " — " + RestartHelp : "");
        /// <summary>Optional display group for preset authoring; not part of the persistent key.</summary>
        public string Group { get; }
        /// <summary>Localized display label; persistent identity is independent of this text.</summary>
        public string DisplayName { get; }
        /// <summary>Host, per-player or local ownership used for preset filtering and application.</summary>
        public PresetSettingScope Scope { get; }
        /// <summary>Localized ownership label; falls back to the scope name.</summary>
        public string ScopeText { get; }
        /// <summary>Default/player/fixed option labels in enum order.</summary>
        public string[] ModeOptions { get; }

        /// <summary>Selected resolution mode (0..2); invalid indices become Fixed. Changes raise PropertyChanged synchronously.</summary>
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

        /// <summary>Synchronous notification when the selected resolution mode changes; bind on the Unity thread.</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Creates a persistence selection from this row key and selected resolution mode.</summary>
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
        /// <summary>Composite source/provider/target/preset identity used to distinguish equally named presets.</summary>
        public string StableId => Preset?.StableId ?? string.Empty;
        /// <summary>Human-readable preset name.</summary>
        public string Name => Preset?.Name ?? string.Empty;
        /// <summary>Optional explanatory text for the preset chooser.</summary>
        public string Description => Preset?.Description ?? string.Empty;
        /// <summary>Identity of the mod supplying this preset, independently of the target mod.</summary>
        public string ProviderGuid => Preset?.ProviderGuid ?? string.Empty;
        /// <summary>Display label of the supplying mod.</summary>
        public string ProviderName => Preset?.ProviderName ?? string.Empty;
        /// <summary>Origin of this preset; determines whether personal overwrite/delete is allowed.</summary>
        public ModSettingsPresetSourceKind SourceKind =>
            Preset?.SourceKind ?? ModSettingsPresetSourceKind.Personal;
        /// <summary>True only for personal presets; bundled and external sources remain read-only.</summary>
        public bool CanOverwrite => Preset?.CanOverwrite == true;
        /// <summary>True only for personal presets; deleting provider-owned files is not offered.</summary>
        public bool CanDelete => Preset?.SourceKind == ModSettingsPresetSourceKind.Personal;
        /// <summary>Localized origin label displayed beside the preset name.</summary>
        public string SourceLabel { get; internal set; } = string.Empty;
        /// <summary>Combined label used by selection controls.</summary>
        public string DisplayText => SourceLabel + " · " + Name;
        /// <summary>Returns the display label for selection controls; not a persistent identifier.</summary>
        public override string ToString() => DisplayText;
    }

    /// <summary>A deliberate destination offered by the personal-preset save dialog.</summary>
    public sealed class ModSettingsPresetSaveTarget
    {
        internal PublishedModSettingsPreset Preset { get; set; }
        /// <summary>Whether this save target represents creating a preset rather than overwriting an existing personal preset.</summary>
        public bool IsNew => Preset == null;
        /// <summary>Human-readable preset name.</summary>
        public string Name => Preset?.Name ?? string.Empty;
        /// <summary>Composite source/provider/target/preset identity used to distinguish equally named presets.</summary>
        public string StableId => Preset?.StableId ?? string.Empty;
        /// <summary>Combined label used by selection controls.</summary>
        public string DisplayText { get; internal set; } = string.Empty;
        /// <summary>Returns the display label for selection controls; not a persistent identifier.</summary>
        public override string ToString() => DisplayText;
    }

}
