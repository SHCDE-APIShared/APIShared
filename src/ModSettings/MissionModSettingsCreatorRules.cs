using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MessagePack;

namespace APIShared.ModSettings
{
    /// <summary>Common mission-author rule capture and savegame rule materialization.</summary>
    public static class MissionModSettingsCreatorRules
    {
        /// <summary>Captures rules for every known host property, without reading live values or applying settings.
        /// A missing entry uses mode 0; Player uses 1; Fixed uses 2 with the existing MessagePack value and savegame
        /// property size limit. Unknown properties are ignored. A failed fixed conversion retains the preceding
        /// default/Player rule and reports through onError; diagnostic callback failures are isolated.
        /// Endpoints opting into IModSettingsStoredSnapshotMigration instead return sparse rules: explicit
        /// Player/Fixed selections and authorized Fixed values derived from the raw Fixed selection. Unselected
        /// settings have no synthetic mode-0 rule, preserving absence for future raw migration while retaining
        /// the normal default fallback. Player selections are presence markers, never read or inherited by
        /// derived settings. Converter failures reject capture; existing explicit savegame mode-0 rules remain binding.
        /// Call on the Unity thread after registration; the result owns its mutable buffers. Schema validation
        /// and selecting the authorized active document remain the caller's responsibility.</summary>
        public static Dictionary<string, TrailCreatorRule> Capture(IModSettingsPresetEndpoint endpoint,
            MissionModSettingsEntry entry, Action<string, Exception> onError = null)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            var properties = ModSettingsApplication.GetMissionHostProperties(endpoint).ToDictionary(property => property.Name, StringComparer.Ordinal);
            var result = properties.Keys.ToDictionary(name => name, _ => new TrailCreatorRule { Mode = 0 }, StringComparer.Ordinal);
            if (entry == null) return endpoint is IModSettingsStoredSnapshotMigration
                ? new Dictionary<string, TrailCreatorRule>(StringComparer.Ordinal) : result;
            entry = MissionModSettingsPreparation.WithCurrentNames(endpoint, entry);
            foreach (string name in entry.PlayerSettings ?? Array.Empty<string>())
                if (properties.ContainsKey(name)) result[name] = new TrailCreatorRule { Mode = 1 };
            if (entry.Overrides != null)
                foreach (var setting in entry.Overrides)
                {
                    if (!properties.TryGetValue(setting.Key, out PropertyInfo property)) continue;
                    try
                    {
                        byte[] bytes = MessagePackSerializer.Serialize(property.PropertyType,
                            ModSettingsPresetJson.ConvertValue(setting.Value, property.PropertyType));
                        if (bytes == null || bytes.Length > SavegameModSettings.MaximumPropertyBytes)
                            throw new InvalidDataException("The fixed Trail setting exceeds the savegame property limit.");
                        result[setting.Key] = new TrailCreatorRule { Mode = 2, FixedValue = bytes };
                    }
                    catch (Exception error) { Report(onError, setting.Key, error); }
                }
            if (endpoint is IModSettingsStoredSnapshotMigration)
            {
                var selected = result.Where(item => item.Value.Mode == 2).ToDictionary(
                    item => item.Key, item => (byte[])item.Value.FixedValue.Clone(), StringComparer.Ordinal);
                var player = new HashSet<string>(result.Where(item => item.Value.Mode == 1).Select(item => item.Key), StringComparer.Ordinal);
                foreach (string name in player) selected[name] = null;
                var migrated = ModSettingsMigration.MigrateRawStoredSnapshot(endpoint, selected);
                foreach (string name in result.Where(item => item.Value.Mode == 0).Select(item => item.Key).ToArray()) result.Remove(name);
                foreach (var setting in migrated)
                {
                    if (player.Contains(setting.Key) || !properties.TryGetValue(setting.Key, out PropertyInfo property)) continue;
                    byte[] bytes = setting.Value;
                    if (bytes == null || bytes.Length > SavegameModSettings.MaximumPropertyBytes ||
                        MessagePackSerializer.Deserialize(property.PropertyType, bytes) == null)
                        throw new InvalidDataException("The migrated fixed Trail setting is invalid: " + setting.Key);
                    result[setting.Key] = new TrailCreatorRule { Mode = 2, FixedValue = (byte[])bytes.Clone() };
                }
            }
            return result;
        }

        internal static void Apply(Dictionary<string, byte[]> snapshot, IEnumerable<PropertyInfo> properties,
            Dictionary<string, TrailCreatorRule> rules, Dictionary<string, byte[]> current, bool strictTrail,
            Action<string, Exception> onError)
        {
            foreach (PropertyInfo property in properties)
            {
                if (!rules.TryGetValue(property.Name, out TrailCreatorRule rule)) continue;
                byte[] bytes = rule.Mode == 1 ?
                    current != null && current.TryGetValue(property.Name, out byte[] player) ? player : null :
                    strictTrail && rule.Mode == 2 ? rule.FixedValue : null;
                if (bytes == null) continue;
                try
                {
                    if (MessagePackSerializer.Deserialize(property.PropertyType, bytes) != null)
                        snapshot[property.Name] = (byte[])bytes.Clone();
                }
                catch (Exception error) { Report(onError, property.Name, error); }
            }
        }

        private static void Report(Action<string, Exception> callback, string property, Exception error)
        {
            try { callback?.Invoke(property, error); }
            catch { /* Optional diagnostics cannot change a rule's fallback or block other properties. */ }
        }
    }
}
