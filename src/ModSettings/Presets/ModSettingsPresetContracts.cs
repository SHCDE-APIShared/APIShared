using System;
using System.Collections.Generic;
#pragma warning disable 1591 // Public schema members are documented by the APIShared preset guide.

namespace APIShared.ModSettings
{
    /// <summary>Typed APIShared endpoint consumed by optional mission-setting providers such as ExtendedData.</summary>
    public interface IModSettingsPresetEndpoint
    {
        Dictionary<string, byte[]> System_CreateDisabledMissionPresetSnapshot();
        void System_EnterMissionPreset(Dictionary<string, byte[]> snapshot, string label, bool editable);
        void System_ExitMissionPreset();
        bool IsMissionPresetActive { get; }
    }

    /// <summary>Optional source metadata supplied before entering a mission preset.</summary>
    public interface IModSettingsMissionSourceEndpoint
    {
        void System_SetExplicitMissionSettings(bool hasExplicitSettings);
    }

    /// <summary>Extended endpoint for replacing an editable mission working copy without touching its source.</summary>
    public interface IModSettingsWorkingCopyEndpoint : IModSettingsPresetEndpoint
    {
        Dictionary<string, byte[]> System_CreateModDefaultSnapshot();
        Dictionary<string, byte[]> System_CreateCurrentMissionPresetSnapshot();
        Dictionary<string, byte[]> System_CreatePlayerMissionPresetSnapshot();
        void System_ApplyMissionPresetSnapshot(Dictionary<string, byte[]> snapshot, string label);
        Dictionary<string, byte[]> System_CreateCurrentWorkingSnapshot();
        void System_ApplyWorkingSnapshot(Dictionary<string, byte[]> snapshot);
        void System_LoadModDefaults();
    }

    /// <summary>Determines how a published preset resolves one selected setting.</summary>
    public enum PublishedPresetValueMode
    {
        ModDefault = 0,
        Player = 1,
        Fixed = 2,
    }

    /// <summary>Bulk choices offered by the standard personal-preset save dialog.</summary>
    public enum PresetSaveBulkMode
    {
        ModDefault = 0,
        Player = 1,
        Fixed = 2,
        HostFixed = 3,
        Mixed = 4,
    }

    /// <summary>Describes the ownership of a persistent lobby setting.</summary>
    public enum PresetSettingScope
    {
        Host = 0,
        Player = 1,
        Local = 2,
    }

    /// <summary>Identifies who owns a preset and whether it may be replaced by the player.</summary>
    public enum ModSettingsPresetSourceKind
    {
        Personal = 0,
        Bundled = 1,
        External = 2,
        Mission = 3,
    }

    /// <summary>One persistent setting exposed to preset authoring UI.</summary>
    public sealed class PresetSettingDescriptor
    {
        public string PropertyName { get; internal set; } = string.Empty;
        public Type PropertyType { get; internal set; }
        public string Group { get; internal set; } = string.Empty;
        public string DisplayName { get; internal set; } = string.Empty;
        public bool RequiresRestart { get; internal set; }
        public PresetSettingScope Scope { get; internal set; }
    }

    /// <summary>One author-selected setting passed to personal-preset persistence.</summary>
    public sealed class PresetSaveSelection
    {
        public string PropertyName { get; set; } = string.Empty;
        public PublishedPresetValueMode Mode { get; set; } = PublishedPresetValueMode.Fixed;
    }

    /// <summary>One setting selected by a published preset.</summary>
    public sealed class PublishedPresetSetting
    {
        public PublishedPresetValueMode Mode { get; set; }
        public object Value { get; set; }
    }

    /// <summary>A validated, provider-qualified preset offered by a target mod.</summary>
    public sealed class PublishedModSettingsPreset
    {
        public ModSettingsPresetSourceKind SourceKind { get; internal set; }
        public string ProviderGuid { get; internal set; } = string.Empty;
        public string ProviderName { get; internal set; } = string.Empty;
        public string TargetGuid { get; internal set; } = string.Empty;
        public string Id { get; internal set; } = string.Empty;
        public string Name { get; internal set; } = string.Empty;
        public string Description { get; internal set; } = string.Empty;
        public string MinimumTargetVersion { get; internal set; } = string.Empty;
        public string MaximumTargetVersion { get; internal set; } = string.Empty;
        public IReadOnlyDictionary<string, PublishedPresetSetting> Settings { get; internal set; } =
            new Dictionary<string, PublishedPresetSetting>(StringComparer.Ordinal);
        public string SourcePath { get; internal set; } = string.Empty;

        public bool CanOverwrite => SourceKind == ModSettingsPresetSourceKind.Personal;
        public string StableId => SourceKind + "\n" + ProviderGuid + "\n" + TargetGuid + "\n" + Id;
    }

}
