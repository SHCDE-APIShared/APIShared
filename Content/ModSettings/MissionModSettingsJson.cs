using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;

namespace APIShared.ModSettings
{
    /// <summary>Schema-3 mission-settings codec. Normalization mutates the supplied document and entries, orders names ordinally, removes empty entries and rejects player/fixed conflicts. It does not apply settings or access files.</summary>
    public static class MissionModSettingsJson
    {
        /// <summary>Maximum UTF-8 size accepted by string parsing and serialization (8 MiB).</summary>
        public const int MaximumBytes = 8 * 1024 * 1024;
        /// <summary>Maximum combined player-name and override count per mod, checked before deduplication.</summary>
        public const int MaximumSettingsPerMod = 16384;
        /// <summary>Parses UTF-8-sized JSON text with an object root and the schema-3 mods contract. Throws InvalidDataException for missing/oversized text or invalid schema data.</summary>
        public static MissionModSettingsDocument ParseObject(string json)
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                throw new InvalidDataException("Mod settings exceed 8 MiB or are missing.");
            object rootObject = APIShared.Internal.DependencyFreeJson.Parse(json);
            if (!(rootObject is Dictionary<string, object> root))
                throw new InvalidDataException("Trail mod-settings JSON root must be an object.");
            return ParseDocument(root);
        }

        /// <summary>Parses an already decoded object using Dictionary objects, List arrays and native scalars. Applies the same schema validation; the caller owns the enclosing document byte limit.</summary>
        public static MissionModSettingsDocument ParseDocument(Dictionary<string, object> root)
        {
            if (!root.TryGetValue("schemaVersion", out object schema) || !(schema is int schemaVersion) || schemaVersion != 3)
                throw new InvalidDataException("Unsupported Trail mod-settings schemaVersion.");
            if (!root.TryGetValue("mods", out object modsObject) || !(modsObject is Dictionary<string, object> mods))
                throw new InvalidDataException("Trail mod-settings JSON requires a mods object.");

            MissionModSettingsDocument document = MissionModSettingsDocument.CreateModDefaults();
            foreach (KeyValuePair<string, object> mod in mods)
            {
                if (string.IsNullOrWhiteSpace(mod.Key))
                    throw new InvalidDataException("Trail mod-settings contains an empty mod id.");
                if (!(mod.Value is Dictionary<string, object> rawEntry))
                    throw new InvalidDataException($"Mod entry [{mod.Key}] must be an object.");
                var entry = new MissionModSettingsEntry();
                if (!rawEntry.TryGetValue("playerSettings", out object playerSettingsObject) ||
                    !(playerSettingsObject is List<object> rawPlayerSettings))
                {
                    throw new InvalidDataException($"Mod entry [{mod.Key}] requires a playerSettings array.");
                }
                entry.PlayerSettings = rawPlayerSettings
                    .Select(value => value as string ?? throw new InvalidDataException(
                        $"Mod entry [{mod.Key}].playerSettings must contain only strings."))
                    .ToArray();
                if (!rawEntry.TryGetValue("overrides", out object settingsObject))
                    throw new InvalidDataException($"Mod entry [{mod.Key}] requires an overrides object.");
                if (!(settingsObject is Dictionary<string, object> settings))
                    throw new InvalidDataException($"Mod entry [{mod.Key}].overrides must be an object.");
                foreach (KeyValuePair<string, object> setting in settings)
                {
                    if (string.IsNullOrWhiteSpace(setting.Key) || !IsSupportedValue(setting.Value))
                        throw new InvalidDataException($"Mod entry [{mod.Key}] contains an unsupported value for [{setting.Key}].");
                }
                entry.Overrides = settings;
                if (entry.PlayerSettings.Length != 0 || entry.Overrides.Count != 0)
                    document.Mods[mod.Key] = entry;
            }
            return NormalizeAndValidate(document, "Trail mod-settings");
        }

        /// <summary>Normalizes in place and returns the supplied document, or a new default document for null. Previously visited entries may already be normalized if a later entry fails validation.</summary>
        public static MissionModSettingsDocument NormalizeAndValidate(MissionModSettingsDocument settings, string path)
        {
            settings = settings ?? MissionModSettingsDocument.CreateModDefaults();
            if (settings.SchemaVersion != 3)
                throw new InvalidDataException((path ?? "modSettings") + ".schemaVersion must be 3.");
            settings.Mods = settings.Mods ?? new Dictionary<string, MissionModSettingsEntry>(StringComparer.Ordinal);
            var normalized = new Dictionary<string, MissionModSettingsEntry>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, MissionModSettingsEntry> mod in settings.Mods
                .OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                string id = mod.Key;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidDataException((path ?? "modSettings") + ".mods contains an empty mod id.");
                MissionModSettingsEntry entry = mod.Value ?? new MissionModSettingsEntry();
                if ((entry.PlayerSettings?.Length ?? 0) + (entry.Overrides?.Count ?? 0) > MaximumSettingsPerMod)
                    throw new InvalidDataException("Too many mod settings: " + id);
                if ((entry.PlayerSettings ?? Array.Empty<string>())
                    .Any(name => string.IsNullOrWhiteSpace(name) ||
                        !string.Equals(name, name.Trim(), StringComparison.Ordinal)))
                {
                    throw new InvalidDataException((path ?? "modSettings") + "." + id + ".playerSettings contains an empty or padded property name.");
                }
                entry.PlayerSettings = (entry.PlayerSettings ?? Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                entry.Overrides = entry.Overrides ?? new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> value in entry.Overrides)
                {
                    if (string.IsNullOrWhiteSpace(value.Key) || !IsSupportedValue(value.Value))
                        throw new InvalidDataException((path ?? "modSettings") + "." + id + ".overrides contains an unsupported value for " + value.Key + ".");
                }
                entry.Overrides = entry.Overrides
                    .OrderBy(value => value.Key, StringComparer.Ordinal)
                    .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
                string conflict = entry.PlayerSettings.FirstOrDefault(entry.Overrides.ContainsKey);
                if (conflict != null)
                    throw new InvalidDataException((path ?? "modSettings") + "." + id + " selects " + conflict + " as both player and fixed.");
                if (entry.PlayerSettings.Length != 0 || entry.Overrides.Count != 0)
                    normalized[id] = entry;
            }
            settings.Mods = normalized;
            return settings;
        }

        /// <summary>Removes names absent from the current schema, preserving the document and retained entry identities. Returns removed names in ordinal order; removes an entry when it becomes empty.</summary>
        public static string[] RemoveUnknownSettings(
            MissionModSettingsDocument document,
            string modId,
            IEnumerable<string> currentSettingNames)
        {
            if (document?.Mods == null || string.IsNullOrEmpty(modId) ||
                !document.Mods.TryGetValue(modId, out MissionModSettingsEntry entry) || entry == null)
            {
                return Array.Empty<string>();
            }

            entry.PlayerSettings = entry.PlayerSettings ?? Array.Empty<string>();
            entry.Overrides = entry.Overrides ?? new Dictionary<string, object>(StringComparer.Ordinal);
            var currentNames = new HashSet<string>(currentSettingNames ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            string[] removed = entry.PlayerSettings
                .Concat(entry.Overrides.Keys)
                .Where(name => !currentNames.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            entry.PlayerSettings = entry.PlayerSettings
                .Where(currentNames.Contains)
                .ToArray();
            foreach (string name in removed)
                entry.Overrides.Remove(name);
            if (entry.PlayerSettings.Length == 0 && entry.Overrides.Count == 0)
                document.Mods.Remove(modId);
            return removed;
        }

        private static string CheckSerializedSize(string json)
        {
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Mod settings exceed 8 MiB.");
            return json;
        }

        /// <summary>Normalizes in place and writes deterministic schema-3 JSON with CRLF and an 8 MiB UTF-8 limit. Normalization remains visible even when serialization later fails.</summary>
        public static string Serialize(MissionModSettingsDocument document)
        {
            document = NormalizeAndValidate(document, "Trail mod-settings");
            var mods = new OrderedDictionary(StringComparer.Ordinal);
            foreach (string id in document.Mods.Keys
                .OrderBy(value => value, StringComparer.Ordinal))
            {
                MissionModSettingsEntry entry = document.Mods[id];
                var playerSettings = new List<object>();
                foreach (string setting in entry.PlayerSettings)
                    playerSettings.Add(setting);
                var settings = new OrderedDictionary(StringComparer.Ordinal);
                if (entry.Overrides != null)
                {
                    foreach (KeyValuePair<string, object> setting in
                        entry.Overrides.OrderBy(item => item.Key, StringComparer.Ordinal))
                    {
                        if (string.IsNullOrWhiteSpace(setting.Key) || !IsSupportedValue(setting.Value))
                            throw new InvalidDataException($"Mod entry [{id}] contains an unsupported value for [{setting.Key}].");
                        settings.Add(setting.Key, setting.Value);
                    }
                }

                if (playerSettings.Count != 0 || settings.Count != 0)
                {
                    mods.Add(id, new OrderedDictionary(StringComparer.Ordinal)
                    {
                        { "playerSettings", playerSettings },
                        { "overrides", settings }
                    });
                }
            }

            return CheckSerializedSize(APIShared.Internal.DependencyFreeJson.Serialize(new OrderedDictionary(StringComparer.Ordinal)
            {
                { "schemaVersion", 3 },
                { "mods", mods }
            }));
        }

        private static bool IsNativeScalar(object value) =>
            value is bool || value is string ||
            value is byte || value is sbyte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong ||
            value is decimal || value is double;

        /// <summary>Accepts native Boolean/string/integer/decimal/double scalars or a flat enumerable of those scalars. Null, float, dictionaries and nested sequences are unsupported; non-finite doubles fail during JSON serialization.</summary>
        public static bool IsSupportedValue(object value)
        {
            if (IsNativeScalar(value))
                return true;
            if (!(value is IEnumerable sequence) || value is IDictionary)
                return false;
            foreach (object item in sequence)
            {
                if (!IsNativeScalar(item))
                    return false;
            }
            return true;
        }
    }
}
