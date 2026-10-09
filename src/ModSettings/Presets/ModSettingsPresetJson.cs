using APIShared.Internal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
#pragma warning disable 1591 // Public schema members are documented by the APIShared preset guide.

namespace APIShared.ModSettings
{
    /// <summary>Shared JSON contract for loose <c>preset_*.json</c> files.</summary>
    public static class ModSettingsPresetJson
    {
        public const int SchemaVersion = 1;
        public const string EncodedMessagePackPrefix = "messagepack-base64:";
        public const int MaximumSettings = 16384;
        public const int MaximumDescriptionLength = 8192;

        public static PublishedModSettingsPreset Parse(
            string json,
            string providerGuid,
            string providerName,
            string expectedTargetGuid,
            string sourcePath)
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) > ModSettingsPresetCatalog.MaximumFileBytes)
                throw new InvalidDataException("Preset exceeds the 8 MiB limit.");
            if (!(DependencyFreeJson.Parse(json) is Dictionary<string, object> root))
                throw new InvalidDataException("Preset JSON root must be an object.");
            RequireKnownKeys(
                root,
                "preset",
                "schemaVersion", "id", "name", "description", "targetGuid",
                "minimumTargetVersion", "maximumTargetVersion", "settings");
            RequireExactInt(root, "schemaVersion", SchemaVersion);

            string id = RequireIdentifier(root, "id", 128);
            string name = RequireText(root, "name", 256);
            string targetGuid = RequireIdentifier(root, "targetGuid", 200);
            if (!string.Equals(targetGuid, expectedTargetGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Preset targetGuid does not match its Override directory.");

            string description = OptionalText(root, "description", MaximumDescriptionLength);
            string minimum = OptionalText(root, "minimumTargetVersion", 80);
            string maximum = OptionalText(root, "maximumTargetVersion", 80);
            if (!root.TryGetValue("settings", out object settingsValue) ||
                !(settingsValue is Dictionary<string, object> rawSettings))
            {
                throw new InvalidDataException("Preset JSON requires a settings object.");
            }
            if (rawSettings.Count == 0 || rawSettings.Count > MaximumSettings)
                throw new InvalidDataException("Preset settings count is outside the supported range.");

            var settings = new Dictionary<string, PublishedPresetSetting>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> entry in rawSettings)
            {
                string propertyName = ValidatePropertyName(entry.Key);
                if (!(entry.Value is Dictionary<string, object> rawSetting))
                    throw new InvalidDataException("Preset setting [" + propertyName + "] must be an object.");
                RequireKnownKeys(rawSetting, "setting " + propertyName, "mode", "value");
                string modeText = RequireText(rawSetting, "mode", 32);
                PublishedPresetValueMode mode;
                switch (modeText)
                {
                    case "modDefault": mode = PublishedPresetValueMode.ModDefault; break;
                    case "player": mode = PublishedPresetValueMode.Player; break;
                    case "fixed": mode = PublishedPresetValueMode.Fixed; break;
                    default: throw new InvalidDataException("Unknown preset mode [" + modeText + "] for [" + propertyName + "].");
                }

                bool hasValue = rawSetting.TryGetValue("value", out object fixedValue);
                if (mode == PublishedPresetValueMode.Fixed && (!hasValue || fixedValue == null))
                    throw new InvalidDataException("Fixed preset setting [" + propertyName + "] requires a non-null value.");
                if (mode != PublishedPresetValueMode.Fixed && hasValue)
                    throw new InvalidDataException("Only fixed preset settings may contain value.");
                settings.Add(propertyName, new PublishedPresetSetting { Mode = mode, Value = fixedValue });
            }

            return new PublishedModSettingsPreset
            {
                ProviderGuid = providerGuid ?? string.Empty,
                ProviderName = string.IsNullOrWhiteSpace(providerName) ? providerGuid ?? string.Empty : providerName,
                TargetGuid = targetGuid,
                Id = id,
                Name = name,
                Description = description,
                MinimumTargetVersion = minimum,
                MaximumTargetVersion = maximum,
                Settings = settings,
                SourcePath = sourcePath ?? string.Empty,
            };
        }

        public static string Serialize(
            string targetGuid,
            string id,
            string name,
            string description,
            string minimumTargetVersion,
            string maximumTargetVersion,
            IReadOnlyDictionary<string, PublishedPresetSetting> settings)
        {
            targetGuid = ValidateIdentifier(targetGuid, "targetGuid", 200);
            id = ValidateIdentifier(id, "id", 128);
            name = ValidateText(name, "name", 256);
            description = ValidateOptionalText(
                description,
                "description",
                MaximumDescriptionLength);
            if (settings == null || settings.Count == 0 || settings.Count > MaximumSettings)
                throw new InvalidDataException("Preset settings count is outside the supported range.");

            var serializedSettings = new OrderedDictionary(StringComparer.Ordinal);
            foreach (KeyValuePair<string, PublishedPresetSetting> entry in settings.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                string propertyName = ValidatePropertyName(entry.Key);
                PublishedPresetSetting setting = entry.Value ?? throw new InvalidDataException("Preset setting is null.");
                var value = new OrderedDictionary(StringComparer.Ordinal)
                {
                    { "mode", ToJsonMode(setting.Mode) },
                };
                if (setting.Mode == PublishedPresetValueMode.Fixed)
                {
                    if (setting.Value == null)
                        throw new InvalidDataException("Fixed preset setting [" + propertyName + "] is null.");
                    value.Add("value", setting.Value);
                }
                serializedSettings.Add(propertyName, value);
            }

            var root = new OrderedDictionary(StringComparer.Ordinal)
            {
                { "schemaVersion", SchemaVersion },
                { "id", id },
                { "name", name },
            };
            if (!string.IsNullOrWhiteSpace(description)) root.Add("description", description.Trim());
            root.Add("targetGuid", targetGuid);
            if (!string.IsNullOrWhiteSpace(minimumTargetVersion)) root.Add("minimumTargetVersion", minimumTargetVersion.Trim());
            if (!string.IsNullOrWhiteSpace(maximumTargetVersion)) root.Add("maximumTargetVersion", maximumTargetVersion.Trim());
            root.Add("settings", serializedSettings);
            string json = DependencyFreeJson.Serialize(root);
            if (Encoding.UTF8.GetByteCount(json) > ModSettingsPresetCatalog.MaximumFileBytes)
                throw new InvalidDataException("Preset exceeds the 8 MiB limit.");
            return json;
        }

        public static object ConvertValue(object value, Type targetType)
        {
            if (value == null) throw new InvalidDataException("Null cannot be assigned to [" + targetType.FullName + "].");
            if (targetType != typeof(string) && value is string encoded && encoded.StartsWith(EncodedMessagePackPrefix, StringComparison.Ordinal))
            {
                byte[] bytes = Convert.FromBase64String(encoded.Substring(EncodedMessagePackPrefix.Length));
                return MessagePack.MessagePackSerializer.Deserialize(targetType, bytes);
            }

            Type effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (effectiveType.IsInstanceOfType(value)) return value;
            if (effectiveType.IsArray)
            {
                IEnumerable sequence = value as IEnumerable;
                if (effectiveType.GetArrayRank() != 1 || effectiveType.GetElementType().IsArray ||
                    sequence == null || value is string)
                {
                    throw new InvalidDataException("Only one-dimensional arrays of directly supported values may use JSON arrays.");
                }
                Type elementType = effectiveType.GetElementType();
                var converted = new List<object>();
                foreach (object item in sequence) converted.Add(ConvertValue(item, elementType));
                Array array = Array.CreateInstance(elementType, converted.Count);
                for (int index = 0; index < converted.Count; index++) array.SetValue(converted[index], index);
                return array;
            }
            if (effectiveType.IsEnum)
            {
                if (value is string enumName)
                {
                    if (long.TryParse(enumName, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        throw new InvalidDataException("Numeric enum strings are not supported.");
                    try
                    {
                        return Enum.Parse(effectiveType, enumName, ignoreCase: false);
                    }
                    catch (ArgumentException exception)
                    {
                        throw new InvalidDataException("Unknown enum name [" + enumName + "] for [" + effectiveType.FullName + "].", exception);
                    }
                }
                Type sourceType = value.GetType();
                bool supportedPrimitive = sourceType == typeof(bool) || sourceType == typeof(byte) ||
                    sourceType == typeof(sbyte) || sourceType == typeof(short) || sourceType == typeof(ushort) ||
                    sourceType == typeof(int) || sourceType == typeof(uint) || sourceType == typeof(long) ||
                    sourceType == typeof(ulong);
                if (!supportedPrimitive)
                    throw new InvalidDataException("Enum values must use a declared name or an integral legacy representation.");
                byte[] primitive = MessagePack.MessagePackSerializer.Serialize(sourceType, value);
                return MessagePack.MessagePackSerializer.Deserialize(effectiveType, primitive);
            }
            return Convert.ChangeType(value, effectiveType, CultureInfo.InvariantCulture);
        }

        public static object ToJsonValue(Type propertyType, object value)
        {
            if (value == null) throw new InvalidDataException("Preset values cannot be null.");
            Type type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (type.IsEnum) return value.ToString();
            if (IsJsonScalar(value)) return value;
            if (type.IsArray && type.GetArrayRank() == 1)
            {
                var items = new List<object>();
                foreach (object item in (IEnumerable)value)
                {
                    if (!IsJsonScalar(item) && !item.GetType().IsEnum)
                        return EncodeMessagePack(propertyType, value);
                    items.Add(item.GetType().IsEnum ? item.ToString() : item);
                }
                return items;
            }
            return EncodeMessagePack(propertyType, value);
        }

        private static string EncodeMessagePack(Type type, object value) =>
            EncodedMessagePackPrefix + Convert.ToBase64String(MessagePack.MessagePackSerializer.Serialize(type, value));
        private static bool IsJsonScalar(object value) => value is bool || value is string || value is byte || value is sbyte ||
            value is short || value is ushort || value is int || value is uint || value is long || value is ulong ||
            value is float || value is double || value is decimal;
        private static string ToJsonMode(PublishedPresetValueMode mode) => mode == PublishedPresetValueMode.ModDefault
            ? "modDefault" : mode == PublishedPresetValueMode.Player ? "player" : mode == PublishedPresetValueMode.Fixed
            ? "fixed" : throw new InvalidDataException("Unknown published preset mode.");
        private static void RequireExactInt(Dictionary<string, object> root, string key, int expected)
        {
            if (!root.TryGetValue(key, out object value) || !(value is int number) || number != expected)
                throw new InvalidDataException("Unsupported preset " + key + ".");
        }
        private static string RequireIdentifier(Dictionary<string, object> root, string key, int max) =>
            !root.TryGetValue(key, out object value) || !(value is string text)
                ? throw new InvalidDataException("Preset JSON requires string " + key + ".")
                : ValidateIdentifier(text, key, max);
        private static string RequireText(Dictionary<string, object> root, string key, int max) =>
            !root.TryGetValue(key, out object value) || !(value is string text)
                ? throw new InvalidDataException("Preset JSON requires string " + key + ".")
                : ValidateText(text, key, max);
        private static string OptionalText(Dictionary<string, object> root, string key, int max) =>
            !root.TryGetValue(key, out object value) ? string.Empty : value is string text
                ? ValidateOptionalText(text, key, max)
                : throw new InvalidDataException("Preset " + key + " must be a string.");
        private static string ValidateIdentifier(string value, string key, int max)
        {
            string text = ValidateText(value, key, max);
            if (text.Any(ch => char.IsControl(ch) || ch == '/' || ch == '\\'))
                throw new InvalidDataException("Preset " + key + " contains unsafe characters.");
            return text;
        }
        private static string ValidateText(string value, string key, int max)
        {
            string text = value?.Trim() ?? string.Empty;
            if (text.Length == 0 || text.Length > max) throw new InvalidDataException("Preset " + key + " is empty or too long.");
            return text;
        }
        private static string ValidateOptionalText(string value, string key, int max)
        {
            string text = value?.Trim() ?? string.Empty;
            if (text.Length > max) throw new InvalidDataException("Preset " + key + " is too long.");
            return text;
        }
        private static string ValidatePropertyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || !string.Equals(name, name.Trim(), StringComparison.Ordinal))
                throw new InvalidDataException("Preset contains an invalid property name.");
            return name;
        }

        private static void RequireKnownKeys(
            Dictionary<string, object> values,
            string context,
            params string[] allowedKeys)
        {
            var allowed = new HashSet<string>(allowedKeys, StringComparer.Ordinal);
            string unknown = values.Keys.FirstOrDefault(key => !allowed.Contains(key));
            if (unknown != null)
                throw new InvalidDataException("Unknown " + context + " member [" + unknown + "].");
        }
    }

}
