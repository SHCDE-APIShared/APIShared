using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MessagePack;

namespace APIShared.ModSettings
{
    /// <summary>Prepares mission settings without applying values, registering sources or changing personal presets.</summary>
    /// <remarks>Call on the Unity thread after endpoint registration, without concurrent endpoint mutation.
    /// The caller owns source authorization, document normalization and mission lifecycle. Provider, getter,
    /// conversion and selection failures propagate; no partial result is returned. Results own their mutable buffers.</remarks>
    public static class MissionModSettingsPreparation
    {
        /// <summary>Captures host settings selected as Player or Fixed. ModDefault settings are omitted.
        /// Only Fixed selections read live values. Uses schema-3 mission encoding, including the legacy
        /// MessagePack marker for values outside the mission scalar/flat-sequence contract.</summary>
        public static MissionModSettingsEntry CaptureEntry(IModSettingsPresetEndpoint endpoint,
            Func<string, PublishedPresetValueMode> selectMode)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (selectMode == null) throw new ArgumentNullException(nameof(selectMode));
            var result = new MissionModSettingsEntry();
            var player = new List<string>();
            foreach (PropertyInfo property in Properties(endpoint).Values)
            {
                switch (selectMode(property.Name))
                {
                    case PublishedPresetValueMode.ModDefault: break;
                    case PublishedPresetValueMode.Player: player.Add(property.Name); break;
                    case PublishedPresetValueMode.Fixed:
                        object value = property.GetValue(endpoint);
                        result.Overrides[property.Name] = MissionModSettingsJson.IsSupportedValue(value)
                            ? CopyValue(value)
                            : ModSettingsPresetJson.EncodedMessagePackPrefix + Convert.ToBase64String(
                                MessagePackSerializer.Serialize(property.PropertyType, value));
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(selectMode), "Unknown mission value mode.");
                }
            }
            result.PlayerSettings = player.ToArray();
            return result;
        }

        /// <summary>Captures current host values over a detached disabled mission snapshot, or the current
        /// working snapshot of an APIShared model with an application backend. Other snapshot entries are preserved.
        /// Does not refresh or apply backend state. A null provider snapshot throws InvalidDataException.</summary>
        public static Dictionary<string, byte[]> CaptureCurrentSnapshot(IModSettingsPresetEndpoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            var result = CopySnapshot(endpoint is PresetLobbyModSettingsViewModel model && model.System_HasApplicationBackend
                ? model.System_CreateCurrentWorkingSnapshot() : endpoint.System_CreateDisabledMissionPresetSnapshot());
            foreach (PropertyInfo property in Properties(endpoint).Values)
                result[property.Name] = MessagePackSerializer.Serialize(property.PropertyType, property.GetValue(endpoint));
            return result;
        }

        /// <summary>Checks conversion of known host overrides without reading live values or mutating the entry.
        /// Unknown settings and a null entry are ignored. Structural/schema validation remains the codec's responsibility.</summary>
        public static void ValidateEntry(IModSettingsPresetEndpoint endpoint, MissionModSettingsEntry entry)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            entry = WithCurrentNames(endpoint, entry);
            if (entry?.Overrides == null) return;
            var properties = Properties(endpoint);
            foreach (var setting in entry.Overrides)
                if (properties.TryGetValue(setting.Key, out PropertyInfo property))
                    ModSettingsPresetJson.ConvertValue(setting.Value, property.PropertyType);
        }

        /// <summary>Builds a detached working snapshot: mod defaults, then known Player values, then known Fixed
        /// overrides. Missing Player values retain defaults; unknown settings are ignored. A null entry selects
        /// defaults without querying the player snapshot. Null provider snapshots or selected null buffers throw
        /// InvalidDataException. This never applies settings or changes the selected source.</summary>
        public static Dictionary<string, byte[]> MaterializeSource(IModSettingsWorkingCopyEndpoint endpoint,
            MissionModSettingsEntry entry)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            var result = CopySnapshot(endpoint.System_CreateModDefaultSnapshot());
            if (entry == null) return result;
            entry = WithCurrentNames(endpoint, entry);
            var properties = Properties(endpoint);
            var player = endpoint.System_CreatePlayerMissionPresetSnapshot()
                ?? throw new InvalidDataException("The endpoint returned a null player snapshot.");
            foreach (string name in entry.PlayerSettings ?? Array.Empty<string>())
                if (properties.ContainsKey(name) && player.TryGetValue(name, out byte[] bytes))
                    result[name] = bytes == null ? throw new InvalidDataException("The player snapshot contains a null value: " + name)
                        : (byte[])bytes.Clone();
            if (entry.Overrides != null)
                foreach (var setting in entry.Overrides)
                    if (properties.TryGetValue(setting.Key, out PropertyInfo property))
                        result[property.Name] = MessagePackSerializer.Serialize(property.PropertyType,
                            ModSettingsPresetJson.ConvertValue(setting.Value, property.PropertyType));
            return result;
        }

        // A mission document keeps the setting names of the mod version its author used. Returns the entry
        // with declared former names moved to the current properties, or the same instance when nothing
        // moves; the argument is never modified. A stored current name outranks its former names.
        internal static MissionModSettingsEntry WithCurrentNames(object endpoint, MissionModSettingsEntry entry)
        {
            if (entry == null || endpoint == null) return entry;
            ModSettingsMigration.RenameSet renames = ModSettingsMigration.RenamesOf(endpoint.GetType());
            if (renames.Ordered.Length == 0) return entry;
            string[] player = entry.PlayerSettings ?? Array.Empty<string>();
            Dictionary<string, object> overrides = entry.Overrides;
            var present = new HashSet<string>(player, StringComparer.Ordinal);
            if (overrides != null) foreach (string name in overrides.Keys) present.Add(name);
            if (!renames.Ordered.Any(rename => present.Contains(rename.Key))) return entry;
            var newPlayer = player.Where(name => !renames.ByFormerName.ContainsKey(name)).ToList();
            var newOverrides = new Dictionary<string, object>(StringComparer.Ordinal);
            if (overrides != null)
                foreach (var setting in overrides)
                    if (!renames.ByFormerName.ContainsKey(setting.Key)) newOverrides[setting.Key] = setting.Value;
            foreach (var rename in renames.Ordered)
            {
                if (!present.Contains(rename.Key)) continue;
                // Already stored under the current name, or a newer former name has supplied it.
                if (!present.Add(rename.Value)) continue;
                if (overrides != null && overrides.TryGetValue(rename.Key, out object value)) newOverrides[rename.Value] = value;
                else newPlayer.Add(rename.Value);
            }
            newPlayer.Sort(StringComparer.Ordinal);
            return new MissionModSettingsEntry { PlayerSettings = newPlayer.ToArray(), Overrides = newOverrides };
        }

        private static Dictionary<string, PropertyInfo> Properties(object endpoint) =>
            ModSettingsApplication.GetMissionHostProperties(endpoint).ToDictionary(property => property.Name, StringComparer.Ordinal);

        internal static Dictionary<string, byte[]> CopySnapshot(Dictionary<string, byte[]> source)
        {
            if (source == null) throw new InvalidDataException("The endpoint returned a null snapshot.");
            return source.ToDictionary(item => item.Key, item => item.Value == null ? null : (byte[])item.Value.Clone(), StringComparer.Ordinal);
        }

        private static object CopyValue(object value) => value is Array array ? array.Clone()
            : value is IEnumerable sequence && !(value is string) ? sequence.Cast<object>().ToArray() : value;
    }
}
