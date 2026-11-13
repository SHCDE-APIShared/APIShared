using APIShared.GameModes;
using APIShared.ModSettings;
using BepInEx.Logging;
using CrusaderDE;
using MessagePack;
using R3;
using SHCDESE.API;
using SHCDESE.API.Components.Archive;
using SHCDESE.API.Components.ModManager;
using SHCDESE.API.Components.SaveData;
using SHCDESE.IO;
using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace APIShared
{
    public static partial class SavegameModSettings
    {
        private enum RecordReadState { Invalid, Missing, Valid }

        private static bool TryReadRecord(string path, out SavegameModSettingsRecord record) =>
            ReadRecord(path, out record) == RecordReadState.Valid;

        // An unreadable APIShared record (damaged appended archive, newer schema, invalid
        // mode) never blocks the save itself: it loads like a save without a record,
        // with the current host settings.
        private static SavegameModSettingsRecord ReadRecordForLoad(string path, out bool legacy)
        {
            RecordReadState read = ReadRecord(path, out SavegameModSettingsRecord record);
            if (read != RecordReadState.Valid)
                NativeApiLog.Warning(log, "Savegame ModSettings fallback: file=" + path + ",record=" + read +
                    ",entry=" + EntryName + ",source=current-host-settings,choice=forced-no-usable-record; " +
                    "saved host settings cannot be restored because the record is missing, unreadable or unsupported.");
            legacy = read != RecordReadState.Valid;
            // Migration is outside ReadRecord's invalid-wire fallback: an invalid required
            // transfer must refuse this load, not silently select current host settings.
            return read == RecordReadState.Valid ? NormalizeRecordForLoad(record) : record;
        }

        private static SavegameModSettingsRecord NormalizeRecordForLoad(SavegameModSettingsRecord record) =>
            ModSettingsMigration.MigrateSavegameRecord(record,
                useStoredValues: !(useChosenCurrent && IsCurrentChoiceAllowed(record)));

        private static RecordReadState ReadRecord(string path, out SavegameModSettingsRecord record)
        {
            record = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return RecordReadState.Invalid;
                if (!MapArchive.TryLoad(path, out MapArchive archive))
                {
                    // Vanilla saves may have no appended ZIP at all. An identifiable
                    // damaged ZIP is different from a genuinely missing mod entry.
                    return ZipUtil.FindEocdHeaderIndex(path) < 0 &&
                        ZipUtil.FindFirstZipHeaderIndex(path) < 0
                        ? RecordReadState.Missing : RecordReadState.Invalid;
                }
                using (archive)
                {
                    byte[] bytes = archive.TryReadBinaryFile(EntryName, true);
                    if (bytes == null) return RecordReadState.Missing;
                    return TryDeserialize(bytes, out record) ? RecordReadState.Valid : RecordReadState.Invalid;
                }
            }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "Could not inspect savegame ModSettings: file=" + path +
                    ",entry=" + EntryName + ",error=" + error);
                return RecordReadState.Invalid;
            }
        }

        internal static bool TryDeserialize(byte[] bytes, out SavegameModSettingsRecord record)
        {
            record = null;
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes) return false;
            try
            {
                SavegameModSettingsRecord candidate = MessagePackSerializer.Deserialize<SavegameModSettingsRecord>(bytes);
                if (candidate == null || candidate.Version != SchemaVersion || candidate.Mods == null ||
                    candidate.CreatorRules == null ||
                    !Enum.IsDefined(typeof(GameModeKind), candidate.Kind) ||
                    !Enum.IsDefined(typeof(GameModeLaunchVariant), candidate.Variant)) return false;
                if (candidate.Kind == (int)GameModeKind.Unknown || candidate.Kind == (int)GameModeKind.MapEditor ||
                    candidate.Mods.Count > MaximumMods) return false;
                foreach (KeyValuePair<string, Dictionary<string, byte[]>> mod in candidate.Mods)
                {
                    if (string.IsNullOrWhiteSpace(mod.Key) || mod.Key.Length > 256 ||
                        mod.Value == null || mod.Value.Count > MaximumPropertiesPerMod) return false;
                    foreach (KeyValuePair<string, byte[]> property in mod.Value)
                        if (string.IsNullOrWhiteSpace(property.Key) || property.Key.Length > 256 ||
                            property.Value == null || property.Value.Length > MaximumPropertyBytes) return false;
                }
                if (candidate.CreatorRules.Count > MaximumMods) return false;
                foreach (var mod in candidate.CreatorRules)
                {
                    if (string.IsNullOrWhiteSpace(mod.Key) || mod.Key.Length > 256 ||
                        mod.Value == null || mod.Value.Count > MaximumPropertiesPerMod) return false;
                    foreach (var property in mod.Value)
                    {
                        TrailCreatorRule rule = property.Value;
                        if (string.IsNullOrWhiteSpace(property.Key) || property.Key.Length > 256 ||
                            rule == null || rule.Mode < 0 || rule.Mode > 2 ||
                            (rule.Mode == 2 && (rule.FixedValue == null || rule.FixedValue.Length > MaximumPropertyBytes)) ||
                            (rule.Mode != 2 && rule.FixedValue != null)) return false;
                    }
                }
                record = candidate;
                return true;
            }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "Invalid savegame ModSettings: " + error.Message);
                return false;
            }
        }

        internal static byte[] SerializeWithinLimit(SavegameModSettingsRecord record)
        {
            byte[] bytes = MessagePackSerializer.Serialize(record);
            if (bytes.Length <= MaximumBytes) return bytes;
            // A reduced payload must never admit an unverifiable startup configuration. Current readers
            // ignore the flag (it is kept for older builds); the record itself therefore loses its
            // customization permission, which keeps a Trail on its strict baseline.
            record.LockedByConflict = true;
            record.Variant = (int)GameModeLaunchVariant.Standard;
            record.TrailCustomizeAllowed = false;
            foreach (string modId in record.Mods.Keys.OrderByDescending(value => value, StringComparer.Ordinal).ToArray())
            {
                Dictionary<string, byte[]> properties = record.Mods[modId];
                foreach (string propertyName in properties.Keys.OrderByDescending(value => value, StringComparer.Ordinal).ToArray())
                {
                    properties.Remove(propertyName);
                    NativeApiLog.Error(log, "Omitted savegame setting to stay within the payload limit: " +
                        modId + "." + propertyName);
                    bytes = MessagePackSerializer.Serialize(record);
                    if (bytes.Length <= MaximumBytes) return bytes;
                }
                record.Mods.Remove(modId);
                bytes = MessagePackSerializer.Serialize(record);
                if (bytes.Length <= MaximumBytes) return bytes;
            }
            // Rules cannot be truncated selectively: doing so could turn a fixed
            // Trail restriction into an unrestricted current value on restore.
            record.LockedByConflict = true;
            record.CreatorRules?.Clear();
            bytes = MessagePackSerializer.Serialize(record);
            if (bytes.Length <= MaximumBytes) return bytes;
            throw new InvalidDataException("Savegame mode metadata alone exceeds the payload limit.");
        }
    }
}
