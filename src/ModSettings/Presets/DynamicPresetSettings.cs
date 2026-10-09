using APIShared.ModSettings;
using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.Reflection;
using SHCDESE.API.Components.Network;

namespace APIShared.ModSettings
{
    /// <summary>Options supplied by an optional external configuration API.</summary>
    public sealed class DynamicPresetSetting
    {
        /// <summary>Stable setting key, required and limited to 256 characters.</summary>
        public string Key { get; set; }
        /// <summary>Exact CLR type of DefaultValue and values accepted by the provider.</summary>
        public Type ValueType { get; set; }
        /// <summary>Non-null code default with exactly ValueType; not the current working value.</summary>
        public object DefaultValue { get; set; }
        /// <summary>Host or Local scope; dynamic per-player settings are not supported.</summary>
        public PresetSettingScope Scope { get; set; }
        /// <summary>Optional display group for preset authoring; not part of the persistent key.</summary>
        public string Group { get; set; }
        /// <summary>Localized display label; persistent identity is independent of this text.</summary>
        public string DisplayName { get; set; }
        /// <summary>Whether changes to this setting require startup application instead of live application.</summary>
        public bool RequiresRestart { get; set; }
    }

    /// <summary>Marks a property whose changed value must be staged for startup application.</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true)]
    public sealed class RequiresRestartAttribute : Attribute { }

    /// <summary>Applies a complete configuration, separately from its editable working copy.</summary>
    public interface IModSettingsApplicationBackend
    {
        /// <summary>Returns the complete editable configuration intended for application.</summary>
        Dictionary<string, object> ReadDesiredValues();
        /// <summary>Replaces the whole editable configuration; does not by itself apply it to the running game.</summary>
        void ReplaceDesiredValues(Dictionary<string, object> values);
        /// <summary>Returns the configuration actually active in the running process.</summary>
        Dictionary<string, object> ReadActiveValues();
        /// <summary>Returns the personal configuration to restore after temporary mission settings.</summary>
        Dictionary<string, object> ReadOwnValues();
        /// <summary>Returns the startup-staged configuration, or null when no package is pending.</summary>
        Dictionary<string, object> ReadPendingValues();
        /// <summary>Identity of the active temporary configuration; empty means personal configuration.</summary>
        string ActiveContextId { get; }
        /// <summary>Stages a complete configuration for startup using the supplied mission identity, or empty for a personal choice.</summary>
        void StageValues(Dictionary<string, object> values, string contextId);
        /// <summary>Applies a complete configuration live; subsequent ReadActiveValues must reflect the requested values.</summary>
        void ApplyValues(Dictionary<string, object> values, string contextId);
        /// <summary>Restores the backend-owned personal configuration after a temporary context.</summary>
        void ReturnToOwnConfiguration();
        /// <summary>Discards the startup package without substituting another network transport.</summary>
        void DiscardPendingConfiguration();
    }

    /// <summary>Optional admission check owned by a mod's existing authenticated host transport.</summary>
    public interface INetworkModSettingsApplicationBackend
    {
        /// <summary>Whether the authenticated host transport, rather than local staging, owns this client configuration.</summary>
        bool IsNetworkConfigurationClient { get; }
        /// <summary>Checks/prepares the existing authenticated transport; true means the configuration is ready to launch.</summary>
        bool PrepareNetworkConfiguration();
    }

    /// <summary>Whole-snapshot operations on a local working copy; never a second network transport.</summary>
    public interface IDynamicPresetSettingsProvider
    {
        /// <summary>Returns the schema for the complete working configuration; descriptors must have valid keys, defaults and types.</summary>
        IReadOnlyList<DynamicPresetSetting> GetSettings();
        /// <summary>Returns the complete local working configuration.</summary>
        Dictionary<string, object> ReadValues();
        /// <summary>Reads one schema key from the local working configuration.</summary>
        object ReadValue(string key);
        /// <summary>Validates the entire proposed snapshot without mutating the working configuration; rejects invalid values by throwing.</summary>
        void ValidateValues(Dictionary<string, object> values);
        /// <summary>Replaces a complete validated working snapshot; partial per-property mutation is not used for dynamic providers.</summary>
        void ReplaceValues(Dictionary<string, object> values);
    }

    internal sealed class PresetPropertyAccessor
    {
        private readonly PropertyInfo reflected;
        private readonly DynamicPresetSetting dynamic;
        private readonly IDynamicPresetSettingsProvider provider;
        internal PresetPropertyAccessor(PropertyInfo property) { reflected = property; }
        internal PresetPropertyAccessor(DynamicPresetSetting setting, IDynamicPresetSettingsProvider source)
        {
            dynamic = setting;
            provider = source;
            if (string.IsNullOrWhiteSpace(setting.Key) || setting.Key.Length > 256 || setting.ValueType == null ||
                setting.DefaultValue == null || setting.DefaultValue.GetType() != setting.ValueType ||
                setting.Scope == PresetSettingScope.Player)
                throw new ArgumentException("Invalid dynamic setting descriptor: " + setting.Key);
        }
        internal string Group => dynamic?.Group ?? string.Empty;
        internal string DisplayName => dynamic?.DisplayName ?? Name;
        internal string Name => reflected?.Name ?? dynamic.Key;
        internal Type PropertyType => reflected?.PropertyType ?? dynamic.ValueType;
        internal bool CanRead => reflected?.CanRead ?? true;
        internal bool CanWrite => reflected?.CanWrite ?? true;
        internal bool IsDynamic => dynamic != null;
        internal bool RequiresRestart => dynamic?.RequiresRestart ?? reflected.IsDefined(typeof(RequiresRestartAttribute), true);
        internal object DefaultValue => dynamic?.DefaultValue;
        internal object GetValue(object owner) => reflected != null ? reflected.GetValue(owner) : provider.ReadValue(Name);
        internal void SetValue(object owner, object value)
        {
            if (reflected == null) throw new InvalidOperationException("Dynamic settings require a complete snapshot.");
            reflected.SetValue(owner, value);
            // Dynamic values are validated and replaced together by the preset controller.
        }
        internal T GetCustomAttribute<T>() where T : Attribute
        {
            if (reflected != null) return reflected.GetCustomAttribute<T>();
            if (typeof(T) == typeof(SyncHostOnlyAttribute) && dynamic.Scope == PresetSettingScope.Host)
                return (T)(Attribute)new SyncHostOnlyAttribute();
            if (typeof(T) == typeof(PresetLocalAttribute) && dynamic.Scope == PresetSettingScope.Local)
                return (T)(Attribute)new PresetLocalAttribute();
            return null;
        }
    }
}
