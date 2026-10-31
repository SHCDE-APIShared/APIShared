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
            bool host = owner.IsLocalHost;
            foreach (PresetPropertyAccessor property in persistedProperties)
            {
                if (IsHostProperty(property) && !host) continue;
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
            Dictionary<string, byte[]> clone = Clone(preset1 ?? defaults);
            bool host = owner.IsLocalHost;
            foreach (var entry in snapshot)
                if (persistedPropertiesByName.TryGetValue(entry.Key, out var property) &&
                    (host || IsClientProperty(property)))
                    clone[entry.Key] = entry.Value == null ? null : (byte[])entry.Value.Clone();
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
            Clone(preset1 ?? defaults);

        public void ApplyMissionWorkingSnapshot(Dictionary<string, byte[]> snapshot, string label)
        {
            if (!owner.IsMissionPresetSelected || !owner.MissionPresetEditable)
                throw new InvalidOperationException("Mission settings are not currently editable.");
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            Dictionary<string, byte[]> merged = Clone(missionPreset ?? defaults);
            bool host = owner.IsLocalHost;
            foreach (KeyValuePair<string, byte[]> entry in snapshot)
            {
                if (!persistedPropertiesByName.TryGetValue(entry.Key, out PresetPropertyAccessor property))
                    continue;
                if (IsHostProperty(property) && !host)
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
            bool host = owner.IsLocalHost;
            foreach (KeyValuePair<string, PublishedPresetSetting> entry in preset.Settings)
            {
                if (owner.IsRetiredPresetProperty(entry.Key)) continue;
                PresetPropertyAccessor property = persistedPropertiesByName[entry.Key];
                if (IsHostProperty(property) && !host)
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

            // The stored copy reads the live values back, so a substituted default or a
            // rejected value is recorded as it now is; the preset then counts as modified.
            ApplyPreparedValues(prepared, result =>
            {
                if (owner.IsMissionPresetSelected)
                {
                    foreach (PresetPropertyAccessor property in prepared.Keys)
                        StoreProperty(missionPreset, property);
                    // Mission attribution is transient; the normal working preset stays suspended.
                    missionBasedOnPreset = preset;
                    missionPresetDirty = result.HasChanges;
                    owner.SetSelectedPresetCore(MissionPresetIndex);
                }
                else
                {
                    foreach (PresetPropertyAccessor property in prepared.Keys)
                        StoreProperty(preset1, property);
                    SetBasedOn(preset, modified: result.HasChanges);
                    owner.SetSelectedPresetCore(0);
                }
            }, publish: true);
            if (writeLocalStorage) WriteCombinedPayload();
            DebugLogHelper.LogInfo(log, $"SETTINGS_PRESET: mod={targetGuid}, action=load, " +
                $"context={SanitizeLogValue(owner.IsMissionPresetSelected ? missionContextLabel : "Personal")}, " +
                $"source={SanitizeLogValue(ModSettingsApplication.ContextId)}, preset={SanitizeLogValue(preset.StableId)}, " +
                $"modified={(owner.IsMissionPresetSelected ? missionPresetDirty : presetDirty)}, " +
                $"persistence={(owner.IsMissionPresetSelected ? "temporary" : "personal")}.");
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

        /// <summary>
        /// Applies a stored snapshot. A reflected setter that rejects its value falls back to
        /// the code default; the remaining settings are applied either way. With
        /// <paramref name="retryableCleanup"/> a setting that could not be applied at all is
        /// rethrown after the other values, so the owning cleanup keeps its context and can retry.
        /// Otherwise it is reported and the stored snapshot keeps the requested value for the
        /// next load.
        /// </summary>
        private SnapshotApplicationResult ApplySnapshot(
            Dictionary<string, byte[]> stored,
            int selected,
            bool writeLocalStorage,
            bool? restoreHost = null,
            bool publish = true,
            bool retryableCleanup = false)
        {
            // Capture inclusion once, before a setter's PropertyChanged can refresh
            // the live role. Deserialization likewise finishes before any mutation.
            bool includeHost = restoreHost ?? (selected == MissionPresetIndex || owner.IsLocalHost);
            // Reflected presets historically omit null values (CaptureCurrentSettings and
            // TryApplyProperty do the same). A missing optional value must not prevent
            // activation or restoration of the other settings. Backends retain their
            // existing complete-configuration validation.
            bool omitNull = dynamicProvider == null && owner.SettingsApplicationBackend == null;
            var prepared = new Dictionary<PresetPropertyAccessor, byte[]>();
            foreach (PresetPropertyAccessor property in persistedProperties)
            {
                if (!includeHost && !IsClientProperty(property)) continue;
                if (!property.CanWrite) continue;
                byte[] bytes = null;
                stored?.TryGetValue(property.Name, out bytes);
                if (bytes == null) defaults.TryGetValue(property.Name, out bytes);
                if (bytes == null && omitNull) continue;
                try
                {
                    if (bytes == null) throw new InvalidDataException("Missing setting: " + property.Name);
                    if (MessagePackSerializer.Deserialize(property.PropertyType, bytes) == null)
                    {
                        if (omitNull) continue;
                        throw new InvalidDataException("Missing setting: " + property.Name);
                    }
                }
                catch
                {
                    if (!defaults.TryGetValue(property.Name, out bytes) || bytes == null) throw;
                    if (MessagePackSerializer.Deserialize(property.PropertyType, bytes) == null)
                    {
                        if (omitNull) continue;
                        throw;
                    }
                }
                prepared.Add(property, (byte[])bytes.Clone());
            }
            int previousSelection = owner.SelectedPreset;
            SnapshotApplicationResult result;
            retryableSetterFailure = false;
            try
            {
                result = ApplyPreparedValues(prepared, _ => owner.SetSelectedPresetCore(selected), publish);
                retryableSetterFailure = retryableCleanup && result.Failed.Count != 0;
                if (retryableSetterFailure) result.ThrowFirstFailure();
            }
            catch
            {
                LobbySettingsRestoreScope.Execute(() => owner.SetSelectedPresetCore(previousSelection));
                throw;
            }
            // A substituted default is what the setting now holds; keep the working copy honest.
            if (!retryableCleanup && stored != null)
                foreach (PresetPropertyAccessor property in result.FellBack)
                    StoreProperty(stored, property);
            if (writeLocalStorage) WriteCombinedPayload();
            return result;
        }

        /// <summary>
        /// Applies prepared values as one snapshot. Reflected setters are isolated: a rejected
        /// value is replaced by the code default and the other settings are still applied, so a
        /// single failing setter can neither block a preset nor leave a partially applied one.
        /// Selection commit, notification and publication always run once the values are set.
        /// Dynamic providers and application backends keep their complete-configuration validation.
        /// </summary>
        private SnapshotApplicationResult ApplyPreparedValues(Dictionary<PresetPropertyAccessor, byte[]> prepared,
            Action<SnapshotApplicationResult> commit, bool publish)
        {
            using (ModSettingsSearchUpdateScope.Begin(owner.SettingsTarget))
            {
                var result = new SnapshotApplicationResult();
                bool previousApplying = applying;
                applying = true;
                try
                {
                    LobbySettingsRestoreScope.Execute(() =>
                    {
                        if (dynamicProvider != null || owner.SettingsApplicationBackend != null) ApplyDynamicValues(prepared);
                        else foreach (var entry in prepared)
                            ApplyReflectedValue(entry.Key, entry.Value, result);
                        commit(result);
                        // Preserve the protected callback contract: the outer snapshot is
                        // complete, while its notifications still cannot escape to storage.
                        applying = previousApplying;
                        owner.OnSettingsSnapshotApplied();
                    });
                }
                finally
                {
                    applying = previousApplying;
                }

                ReportApplicationFailures(result);
                if (publish && !previousApplying)
                    owner.PublishSettingsSnapshot(prepared.Keys.Select(property => property.Name).ToArray());
                return result;
            }
        }

        private void ApplyReflectedValue(PresetPropertyAccessor property, byte[] bytes, SnapshotApplicationResult result)
        {
            Exception failure;
            try
            {
                property.SetValue(owner.SettingsTarget, MessagePackSerializer.Deserialize(property.PropertyType, bytes));
                return;
            }
            catch (Exception exception) { failure = exception; }
            // The default is tried once, unless it was the rejected value itself.
            if (defaults.TryGetValue(property.Name, out byte[] fallback) && fallback != null &&
                !fallback.SequenceEqual(bytes))
            {
                try
                {
                    object value = MessagePackSerializer.Deserialize(property.PropertyType, fallback);
                    if (value != null)
                    {
                        property.SetValue(owner.SettingsTarget, value);
                        result.FellBack.Add(property);
                        result.FallbackReasons.Add(failure);
                        return;
                    }
                }
                catch { /* The original rejection is the reported cause. */ }
            }
            result.Failed.Add(property);
            result.FailureReasons.Add(failure);
        }

        private void ReportApplicationFailures(SnapshotApplicationResult result)
        {
            for (int index = 0; index < result.FellBack.Count; index++)
                DebugLogHelper.LogWarning(log,
                    $"[{modName}] Setting [{result.FellBack[index].Name}] rejected its stored value; the code default was applied: " +
                    Describe(result.FallbackReasons[index]));
            for (int index = 0; index < result.Failed.Count; index++)
                DebugLogHelper.LogError(log,
                    $"[{modName}] Setting [{result.Failed[index].Name}] could not be applied, not even its code default; " +
                    "the other settings were applied: " + Describe(result.FailureReasons[index]));
        }

        private static string Describe(Exception exception) =>
            (exception is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException : exception).ToString();

        private sealed class SnapshotApplicationResult
        {
            internal readonly List<PresetPropertyAccessor> FellBack = new List<PresetPropertyAccessor>();
            internal readonly List<Exception> FallbackReasons = new List<Exception>();
            internal readonly List<PresetPropertyAccessor> Failed = new List<PresetPropertyAccessor>();
            internal readonly List<Exception> FailureReasons = new List<Exception>();
            internal bool HasChanges => FellBack.Count != 0 || Failed.Count != 0;

            // Rethrows the original setter exception, so retrying owners see the same failure type.
            internal void ThrowFirstFailure()
            {
                if (FailureReasons.Count != 0)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(FailureReasons[0]).Throw();
            }
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
