using System;
using System.Collections.Generic;

namespace APIShared.ModSettings
{
    /// <summary>Typed APIShared endpoint consumed by optional mission-setting providers such as ExtendedData.</summary>
    /// <remarks>Call on the Unity thread after settings registration. Snapshot dictionaries contain serialized setting values; implementations must not retain caller-owned mutable buffers as shared working state.</remarks>
    public interface IModSettingsPresetEndpoint
    {
        /// <summary>Creates a detached serialized mission snapshot with discovered activation settings disabled; does not apply it.</summary>
        Dictionary<string, byte[]> System_CreateDisabledMissionPresetSnapshot();
        /// <summary>Enters a temporary mission snapshot with the supplied label and editability; personal presets are preserved.</summary>
        void System_EnterMissionPreset(Dictionary<string, byte[]> snapshot, string label, bool editable);
        /// <summary>Leaves temporary mission settings and restores the personal preset context.</summary>
        void System_ExitMissionPreset();
        /// <summary>Whether a temporary mission preset context is active, independently of which preset is selected.</summary>
        bool IsMissionPresetActive { get; }
    }

    /// <summary>Optional source metadata supplied before entering a mission preset.</summary>
    public interface IModSettingsMissionSourceEndpoint
    {
        /// <summary>Records whether mission data supplied explicit settings, controlling the direct-launch notice.</summary>
        void System_SetExplicitMissionSettings(bool hasExplicitSettings);
    }

    /// <summary>Extended endpoint for replacing an editable mission working copy without touching its source.</summary>
    public interface IModSettingsWorkingCopyEndpoint : IModSettingsPresetEndpoint
    {
        /// <summary>Creates a detached serialized snapshot of captured code defaults.</summary>
        Dictionary<string, byte[]> System_CreateModDefaultSnapshot();
        /// <summary>Returns a detached current working snapshot suitable for mission integration; does not imply a mission-only source.</summary>
        Dictionary<string, byte[]> System_CreateCurrentMissionPresetSnapshot();
        /// <summary>Creates the personal snapshot used as the player contribution to mission settings.</summary>
        Dictionary<string, byte[]> System_CreatePlayerMissionPresetSnapshot();
        /// <summary>Replaces the temporary mission snapshot and label without modifying its external source.</summary>
        void System_ApplyMissionPresetSnapshot(Dictionary<string, byte[]> snapshot, string label);
        /// <summary>Creates a detached serialized snapshot of the currently editable settings.</summary>
        Dictionary<string, byte[]> System_CreateCurrentWorkingSnapshot();
        /// <summary>Applies a complete working snapshot through the preset controller without replacing its source.</summary>
        void System_ApplyWorkingSnapshot(Dictionary<string, byte[]> snapshot);
        /// <summary>Loads defaults into working settings and commits application state; may require backend startup staging.</summary>
        void System_LoadModDefaults();
    }

    /// <summary>Determines how a published preset resolves one selected setting.</summary>
    public enum PublishedPresetValueMode
    {
        /// <summary>Resolve this value from captured code defaults.</summary>
        ModDefault = 0,
        /// <summary>Resolve from the player configuration, or identify per-player ownership when used as a scope.</summary>
        Player = 1,
        /// <summary>Apply the explicitly saved value.</summary>
        Fixed = 2,
    }

    /// <summary>Bulk choices offered by the standard personal-preset save dialog.</summary>
    public enum PresetSaveBulkMode
    {
        /// <summary>Resolve this value from captured code defaults.</summary>
        ModDefault = 0,
        /// <summary>Resolve from the player configuration, or identify per-player ownership when used as a scope.</summary>
        Player = 1,
        /// <summary>Apply the explicitly saved value.</summary>
        Fixed = 2,
        /// <summary>Fix host settings and retain player/local values.</summary>
        HostFixed = 3,
        /// <summary>Read-only aggregate indicator when rows use different resolution modes.</summary>
        Mixed = 4,
    }

    /// <summary>Describes the ownership of a persistent lobby setting.</summary>
    public enum PresetSettingScope
    {
        /// <summary>Settings controlled by the host and synchronized to clients.</summary>
        Host = 0,
        /// <summary>Settings owned independently by each player and synchronized through per-player slots.</summary>
        Player = 1,
        /// <summary>Settings persisted locally without multiplayer synchronization.</summary>
        Local = 2,
    }

    /// <summary>Identifies who owns a preset and whether it may be replaced by the player.</summary>
    public enum ModSettingsPresetSourceKind
    {
        /// <summary>Player-owned preset that may be overwritten or deleted.</summary>
        Personal = 0,
        /// <summary>Read-only preset shipped by the target mod.</summary>
        Bundled = 1,
        /// <summary>Read-only preset shipped by another provider.</summary>
        External = 2,
        /// <summary>Temporary mission-owned preset, separate from personal files.</summary>
        Mission = 3,
    }

    /// <summary>One persistent setting exposed to preset authoring UI.</summary>
    public sealed class PresetSettingDescriptor
    {
        /// <summary>Exact persistent setting key; use the declared property name, not its translated label.</summary>
        public string PropertyName { get; internal set; } = string.Empty;
        /// <summary>CLR type used to validate and deserialize this setting.</summary>
        public Type PropertyType { get; internal set; }
        /// <summary>Optional display group for preset authoring; not part of the persistent key.</summary>
        public string Group { get; internal set; } = string.Empty;
        /// <summary>Localized display label; persistent identity is independent of this text.</summary>
        public string DisplayName { get; internal set; } = string.Empty;
        /// <summary>Whether changes to this setting require startup application instead of live application.</summary>
        public bool RequiresRestart { get; internal set; }
        /// <summary>Host, per-player or local ownership used for preset filtering and application.</summary>
        public PresetSettingScope Scope { get; internal set; }
    }

    /// <summary>One author-selected setting passed to personal-preset persistence.</summary>
    public sealed class PresetSaveSelection
    {
        /// <summary>Exact persistent setting key; use the declared property name, not its translated label.</summary>
        public string PropertyName { get; set; } = string.Empty;
        /// <summary>Selects default, current player value or fixed value resolution when applying this setting.</summary>
        public PublishedPresetValueMode Mode { get; set; } = PublishedPresetValueMode.Fixed;
    }

    /// <summary>One setting selected by a published preset.</summary>
    public sealed class PublishedPresetSetting
    {
        /// <summary>Selects default, current player value or fixed value resolution when applying this setting.</summary>
        public PublishedPresetValueMode Mode { get; set; }
        /// <summary>Value used only in Fixed mode; default/player modes resolve values at application time.</summary>
        public object Value { get; set; }
    }

    /// <summary>A validated, provider-qualified preset offered by a target mod.</summary>
    public sealed class PublishedModSettingsPreset
    {
        /// <summary>Origin of this preset; determines whether personal overwrite/delete is allowed.</summary>
        public ModSettingsPresetSourceKind SourceKind { get; internal set; }
        /// <summary>Identity of the mod supplying this preset, independently of the target mod.</summary>
        public string ProviderGuid { get; internal set; } = string.Empty;
        /// <summary>Display label of the supplying mod.</summary>
        public string ProviderName { get; internal set; } = string.Empty;
        /// <summary>Plugin GUID whose settings this preset addresses.</summary>
        public string TargetGuid { get; internal set; } = string.Empty;
        /// <summary>Stable provider-local identifier, independent of the display label.</summary>
        public string Id { get; internal set; } = string.Empty;
        /// <summary>Human-readable preset name.</summary>
        public string Name { get; internal set; } = string.Empty;
        /// <summary>Optional explanatory text for the preset chooser.</summary>
        public string Description { get; internal set; } = string.Empty;
        /// <summary>Optional lower target-plugin version bound checked during preset discovery.</summary>
        public string MinimumTargetVersion { get; internal set; } = string.Empty;
        /// <summary>Optional upper target-plugin version bound checked during preset discovery.</summary>
        public string MaximumTargetVersion { get; internal set; } = string.Empty;
        /// <summary>Selected setting keys and their value-resolution rules; values are validated against the target schema.</summary>
        public IReadOnlyDictionary<string, PublishedPresetSetting> Settings { get; internal set; } =
            new Dictionary<string, PublishedPresetSetting>(StringComparer.Ordinal);
        /// <summary>Path of the discovered preset file, used for diagnostics and personal overwrite operations.</summary>
        public string SourcePath { get; internal set; } = string.Empty;

        /// <summary>True only for personal presets; bundled and external sources remain read-only.</summary>
        public bool CanOverwrite => SourceKind == ModSettingsPresetSourceKind.Personal;
        /// <summary>Composite source/provider/target/preset identity used to distinguish equally named presets.</summary>
        public string StableId => SourceKind + "\n" + ProviderGuid + "\n" + TargetGuid + "\n" + Id;
    }

}
