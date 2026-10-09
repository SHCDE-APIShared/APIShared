using APIShared.Internal;
using BepInEx.Logging;
using MessagePack;
using SHCDESE.API;
using SHCDESE.API.Components.ModManager;
using SHCDESE.API.Components.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace APIShared.ModSettings
{
    /// <summary>Materializes and applies settings snapshots with the existing role and application-backend rules.</summary>
    internal sealed partial class LobbyPresetController
    {
        public void LoadPreset(PublishedModSettingsPreset preset)
        {
            if (preset == null || !publishedPresets.Contains(preset))
                throw new InvalidDataException("The selected preset is unavailable.");
            if (owner.IsMissionPresetSelected && !owner.MissionPresetEditable)
                throw new InvalidOperationException("The active mission preset is read-only.");
            ApplyPublishedPreset(preset, writeLocalStorage: !owner.IsMissionPresetSelected);
        }

        public void ApplyDefaultsAsWorkingCopy()
        {
            if (owner.IsMissionPresetSelected)
                throw new InvalidOperationException("Mission defaults must be materialized by the registered mission source provider.");
            var prepared = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (PresetPropertyAccessor property in persistedProperties)
            {
                if (IsHostProperty(property) && !owner.IsLocalHost) continue;
                if (defaults.TryGetValue(property.Name, out byte[] value))
                    prepared[property.Name] = value == null ? null : (byte[])value.Clone();
            }
            ApplySnapshot(prepared, 0, writeLocalStorage: false);
            foreach (KeyValuePair<string, byte[]> entry in prepared) preset1[entry.Key] = entry.Value;
            SetBasedOn(null, false);
            WriteCombinedPayload();
            LogRoutine($"[{modName}] Loaded Mod defaults as editable working settings.");
        }

        public void ApplyWorkingSnapshot(Dictionary<string, byte[]> snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (owner.IsMissionPresetSelected)
            {
                ApplyMissionWorkingSnapshot(snapshot, missionPresetLabel);
                return;
            }
            Dictionary<string, byte[]> clone = Clone(snapshot);
            ApplySnapshot(clone, 0, writeLocalStorage: false);
            preset1 = clone;
            WriteCombinedPayload();
        }

        public Dictionary<string, byte[]> CreateDisabledSnapshot()
        {
            Dictionary<string, byte[]> snapshot = CopyProperties(defaults, hostProperties);
            if (persistedPropertiesByName.TryGetValue("EnableMod", out PresetPropertyAccessor enableProperty) &&
                enableProperty.PropertyType == typeof(bool))
            {
                snapshot[enableProperty.Name] = MessagePackSerializer.Serialize(false);
            }
            return snapshot;
        }

        public Dictionary<string, byte[]> CreateDefaultSnapshot() => Clone(defaults);

        public Dictionary<string, byte[]> CreateCurrentMissionSnapshot() =>
            Clone(owner.IsMissionPresetSelected ? missionPreset : preset1 ?? defaults);

        public Dictionary<string, byte[]> CreatePlayerMissionSnapshot() =>
            Clone(owner.IsMissionPresetSelected ? preset1 ?? defaults : CaptureCurrentSettings());

        public void ApplyMissionWorkingSnapshot(Dictionary<string, byte[]> snapshot, string label)
        {
            if (!owner.IsMissionPresetSelected || !owner.MissionPresetEditable)
                throw new InvalidOperationException("Mission settings are not currently editable.");
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            Dictionary<string, byte[]> merged = Clone(missionPreset ?? defaults);
            foreach (KeyValuePair<string, byte[]> entry in snapshot)
            {
                if (!persistedPropertiesByName.TryGetValue(entry.Key, out PresetPropertyAccessor property))
                    continue;
                if (IsHostProperty(property) && !owner.IsLocalHost)
                    continue;
                merged[entry.Key] = entry.Value == null ? null : (byte[])entry.Value.Clone();
            }
            missionPreset = merged;
            missionPresetLabel = label ?? string.Empty;
            missionBasedOnPreset = null;
            missionPresetDirty = false;
            ApplySnapshot(missionPreset, MissionPresetIndex, writeLocalStorage: false);
            LogRoutine($"[{modName}] Loaded mission source [{SanitizeLogValue(missionPresetLabel)}] into the editable working copy.");
        }

        private void ApplyPublishedPreset(PublishedModSettingsPreset preset, bool writeLocalStorage)
        {
            if (preset == null)
                throw new ArgumentNullException(nameof(preset));

            Dictionary<string, byte[]> playerPreset = owner.IsMissionPresetSelected
                ? Clone(preset1 ?? defaults)
                : CaptureCurrentSettings();
            var prepared = new Dictionary<PresetPropertyAccessor, byte[]>();
            foreach (KeyValuePair<string, PublishedPresetSetting> entry in preset.Settings)
            {
                if (owner.IsRetiredPresetProperty(entry.Key)) continue;
                PresetPropertyAccessor property = persistedPropertiesByName[entry.Key];
                if (IsHostProperty(property) && !owner.IsLocalHost)
                    continue;

                byte[] bytes;
                switch (entry.Value.Mode)
                {
                    case PublishedPresetValueMode.ModDefault:
                        if (!defaults.TryGetValue(property.Name, out bytes))
                            throw new InvalidDataException($"Code default for [{property.Name}] is unavailable.");
                        break;
                    case PublishedPresetValueMode.Player:
                        if (!playerPreset.TryGetValue(property.Name, out bytes) &&
                            !defaults.TryGetValue(property.Name, out bytes))
                        {
                            throw new InvalidDataException($"Player value for [{property.Name}] is unavailable.");
                        }
                        break;
                    case PublishedPresetValueMode.Fixed:
                        object converted = ModSettingsPresetJson.ConvertValue(entry.Value.Value, property.PropertyType);
                        bytes = MessagePackSerializer.Serialize(property.PropertyType, converted);
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported published preset mode for [{property.Name}].");
                }
                prepared[property] = (byte[])bytes.Clone();
            }

            applying = true;
            try
            {
                if (dynamicProvider != null || owner.SettingsApplicationBackend != null) ApplyDynamicValues(prepared);
                else foreach (KeyValuePair<PresetPropertyAccessor, byte[]> entry in prepared)
                {
                    if (!TryApplyProperty(entry.Key, entry.Value))
                        throw new InvalidDataException($"Published value for [{entry.Key.Name}] could not be applied.");
                }
                if (owner.IsMissionPresetSelected)
                {
                    foreach (PresetPropertyAccessor property in prepared.Keys)
                        StoreProperty(missionPreset, property);
                    // Mission attribution is transient; the normal working preset stays suspended.
                    missionBasedOnPreset = preset;
                    missionPresetDirty = false;
                    owner.SetSelectedPresetCore(MissionPresetIndex);
                }
                else
                {
                    foreach (PresetPropertyAccessor property in prepared.Keys)
                        StoreProperty(preset1, property);
                    SetBasedOn(preset, modified: false);
                    owner.SetSelectedPresetCore(0);
                }
            }
            finally
            {
                applying = false;
            }
            owner.OnSettingsSnapshotApplied();
            if (writeLocalStorage) WriteCombinedPayload();
            LogRoutine($"[{modName}] Loaded editable preset [{preset.Name}] from [{preset.ProviderName}].");
        }

        private static string SanitizeLogValue(string value) =>
            (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");

        private void ValidatePublishedPreset(PublishedModSettingsPreset preset)
        {
            foreach (KeyValuePair<string, PublishedPresetSetting> entry in preset.Settings)
            {
                if (owner.IsRetiredPresetProperty(entry.Key)) continue;
                if (!persistedPropertiesByName.TryGetValue(entry.Key, out PresetPropertyAccessor property))
                    throw new InvalidDataException($"Unknown persistent property [{entry.Key}].");
                if (entry.Value == null)
                    throw new InvalidDataException($"Setting [{entry.Key}] is null.");
                if (entry.Value.Mode == PublishedPresetValueMode.Fixed)
                {
                    object converted = ModSettingsPresetJson.ConvertValue(entry.Value.Value, property.PropertyType);
                    MessagePackSerializer.Serialize(property.PropertyType, converted);
                }
            }
        }

        private void ApplySnapshot(
            Dictionary<string, byte[]> stored,
            int selected,
            bool writeLocalStorage)
        {
            applying = true;
            try
            {
                if (dynamicProvider != null || owner.SettingsApplicationBackend != null)
                {
                    var prepared = new Dictionary<PresetPropertyAccessor, byte[]>();
                    foreach (PresetPropertyAccessor property in persistedProperties)
                    {
                        if (selected != MissionPresetIndex && !owner.IsLocalHost && !IsClientProperty(property)) continue;
                        byte[] bytes = null;
                        if (stored != null) stored.TryGetValue(property.Name, out bytes);
                        if (bytes == null) defaults.TryGetValue(property.Name, out bytes);
                        if (bytes == null) throw new InvalidDataException("Missing dynamic setting: " + property.Name);
                        prepared.Add(property, bytes);
                    }
                    ApplyDynamicValues(prepared);
                }
                else foreach (PresetPropertyAccessor property in persistedProperties)
                {
                    bool include = selected == MissionPresetIndex || owner.IsLocalHost || IsClientProperty(property);
                    if (!include)
                        continue;

                    byte[] bytes = null;
                    if (stored != null)
                        stored.TryGetValue(property.Name, out bytes);
                    if (bytes == null)
                        defaults.TryGetValue(property.Name, out bytes);
                    if (bytes == null || !property.CanWrite)
                        continue;

                    if (!TryApplyProperty(property, bytes) &&
                        defaults.TryGetValue(property.Name, out byte[] defaultBytes) &&
                        !ReferenceEquals(bytes, defaultBytes))
                    {
                        TryApplyProperty(property, defaultBytes);
                    }
                }

                owner.SetSelectedPresetCore(selected);
            }
            finally
            {
                applying = false;
            }

            owner.OnSettingsSnapshotApplied();

            if (writeLocalStorage)
                WriteCombinedPayload();
        }

        private void ApplyDynamicValues(Dictionary<PresetPropertyAccessor, byte[]> prepared)
        {
            var values = owner.SettingsApplicationBackend?.ReadDesiredValues() ?? dynamicProvider.ReadValues();
            foreach (var entry in prepared)
                values[entry.Key.Name] = MessagePackSerializer.Deserialize(entry.Key.PropertyType, entry.Value);
            if (owner.SettingsApplicationBackend != null) owner.SettingsApplicationBackend.ReplaceDesiredValues(values);
            else { dynamicProvider.ValidateValues(values); dynamicProvider.ReplaceValues(values); }
        }

        private bool TryApplyProperty(PresetPropertyAccessor property, byte[] bytes)
        {
            try
            {
                object value = MessagePackSerializer.Deserialize(property.PropertyType, bytes);
                if (value == null)
                    return false;

                property.SetValue(owner.SettingsTarget, value);
                return true;
            }
            catch (Exception exception)
            {
                DebugLogHelper.LogWarning(
                    log,
                    $"[{modName}] Could not restore [{property.Name}] from the current settings snapshot: {exception.Message}");
                return false;
            }
        }

        private Dictionary<string, byte[]> CaptureCurrentSettings()
        {
            Dictionary<string, byte[]> snapshot =
                new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (PresetPropertyAccessor property in persistedProperties)
                StoreProperty(snapshot, property);
            return snapshot;
        }

        private void StoreProperty(
            Dictionary<string, byte[]> snapshot,
            PresetPropertyAccessor property)
        {
            if (!property.CanRead)
                return;

            try
            {
                object value = dynamicProvider != null ? dynamicProvider.ReadValue(property.Name) : owner.SettingsApplicationBackend != null ? owner.SettingsApplicationBackend.ReadDesiredValues()[property.Name] : property.GetValue(owner.SettingsTarget);
                if (value == null)
                {
                    snapshot.Remove(property.Name);
                    return;
                }

                snapshot[property.Name] =
                    MessagePackSerializer.Serialize(property.PropertyType, value);
            }
            catch (Exception exception)
            {
                DebugLogHelper.LogWarning(
                    log,
                    $"[{modName}] Could not capture [{property.Name}] for the current settings snapshot: {exception.Message}");
            }
        }

    }
}
