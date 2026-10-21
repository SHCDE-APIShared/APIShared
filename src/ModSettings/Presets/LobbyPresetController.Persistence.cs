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
    /// <summary>Reads and composes the existing MessagePack envelope; binary I/O belongs to LobbyPresetStorage.</summary>
    internal sealed partial class LobbyPresetController
    {
        private bool initialSettingsLoaded;

        // Runs before UI binding, with writes disabled. External providers retain ownership
        // of their startup configuration; APIShared must not replay a stale working file there.
        internal void LoadInitialSettings()
        {
            if (initialSettingsLoaded) return;
            initialSettingsLoaded = true;
            if (persistedProperties.Length == 0 || dynamicProvider != null) return;
            if (!storage.TryRead(out Dictionary<string, byte[]> initial)) return;
            bool previousApplying = applying;
            applying = true;
            try
            {
                foreach (PresetPropertyAccessor property in persistedProperties)
                    if (property.CanWrite && initial.TryGetValue(property.Name, out byte[] bytes) && bytes != null)
                        TryApplyProperty(property, bytes);
            }
            finally { applying = previousApplying; }
        }

        public void Activate()
        {
            if (active)
                return;

            if (persistedProperties.Length == 0)
            {
                active = true;
                LogRoutine($"[{modName}] Preset storage skipped because the ViewModel has no persistent settings.");
                return;
            }

            LoadInitialSettings();
            bool needsRelocation = storage.NeedsRelocation;
            Dictionary<string, byte[]> payload = null;
            bool fileExists = storage.Exists;
            if (fileExists && !storage.TryRead(out payload))
            {
                storage.BackupCorruptFile();
                // Do not turn a failed read into a successful defaults-only migration.
                legacyMigrationPending = true;
                payload = null;
            }

            bool needsRewrite = needsRelocation;
            PublishedModSettingsPreset legacyPublishedToMaterialize = null;
            Dictionary<string, byte[]> legacyPreset1 = null;
            Dictionary<string, byte[]> legacyPreset2 = null;
            int legacySelected = 0;
            int loadedSchemaVersion = 0;
            string oldPublishedId = string.Empty;
            if (payload != null && payload.ContainsKey(SchemaVersionKey))
            {
                try
                {
                    int schemaVersion = MessagePackSerializer.Deserialize<int>(payload[SchemaVersionKey]);
                    loadedSchemaVersion = schemaVersion;
                    if (schemaVersion < 1 || schemaVersion > SchemaVersion)
                        throw new InvalidDataException($"Unsupported preset schema version [{schemaVersion}].");
                    if (schemaVersion == SchemaVersion)
                    {
                        if (payload.TryGetValue(LegacyPresetImportCompletedKey, out byte[] importCompletedBytes))
                            legacyPresetImportCompleted = MessagePackSerializer.Deserialize<bool>(importCompletedBytes);
                        preset1 = ReadSnapshot(payload, CurrentSettingsKey) ?? CaptureCurrentSettings();
                        if (payload.TryGetValue(BasedOnPresetKey, out byte[] basedOnBytes))
                            basedOnStableId = MessagePackSerializer.Deserialize<string>(basedOnBytes) ?? string.Empty;
                        if (payload.TryGetValue(PresetDirtyKey, out byte[] dirtyBytes))
                            presetDirty = MessagePackSerializer.Deserialize<bool>(dirtyBytes);
                    }
                    else
                    {
                        legacySelected = payload.TryGetValue(ActivePresetKey, out byte[] selectedBytes)
                            ? NormalizePreset(MessagePackSerializer.Deserialize<int>(selectedBytes))
                            : 0;
                        legacyPreset1 = ReadSnapshot(payload, Preset1Key) ?? CaptureCurrentSettings();
                        legacyPreset2 = ReadSnapshot(payload, Preset2Key);
                        if (schemaVersion >= 2 && payload.TryGetValue(PublishedPresetKey, out byte[] publishedBytes))
                            oldPublishedId = MessagePackSerializer.Deserialize<string>(publishedBytes) ?? string.Empty;
                    }
                }
                catch (Exception exception)
                {
                    DebugLogHelper.LogError(
                        log,
                        $"[{modName}] Preset metadata is invalid: {exception}");
                    storage.BackupCorruptFile();
                    if (needsRelocation) legacyMigrationPending = true;
                    payload = null;
                }
            }

            if (payload != null && loadedSchemaVersion > 0 && loadedSchemaVersion < SchemaVersion)
            {
                preset1 = Clone(legacySelected == 1 && legacyPreset2 != null ? legacyPreset2 : legacyPreset1);
                legacyPublishedToMaterialize = FindLegacyPublishedPreset(oldPublishedId);
                try
                {
                    SaveLegacySnapshotAsPersonalPreset("legacy-preset-1", "Preset 1 (migrated)", legacyPreset1);
                    if (legacyPreset2 != null)
                        SaveLegacySnapshotAsPersonalPreset("legacy-preset-2", "Preset 2 (migrated)", legacyPreset2);
                    RefreshCatalog();
                    if (legacyPublishedToMaterialize == null)
                    {
                        PublishedModSettingsPreset migrated = publishedPresets.FirstOrDefault(item =>
                            item.SourceKind == ModSettingsPresetSourceKind.Personal &&
                            string.Equals(item.Id, legacySelected == 1 ? "legacy-preset-2" : "legacy-preset-1", StringComparison.OrdinalIgnoreCase));
                        SetBasedOn(migrated, modified: false);
                    }
                    needsRewrite = true;
                    LogRoutine($"[{modName}] Migrated legacy Preset 1/2 storage to personal JSON presets.");
                }
                catch (Exception exception)
                {
                    legacyMigrationPending = true;
                    DebugLogHelper.LogError(
                        log,
                        $"[{modName}] Could not publish legacy presets; the valid MessagePack data was retained for retry; " +
                        DescribeMigrationFailure(exception));
                }
            }

            if (payload == null || !payload.ContainsKey(SchemaVersionKey))
            {
                // The APIShared load before registration restored a legacy file here.
                // Capturing the ViewModel preserves those values and supplies defaults
                // for settings introduced after that file was written.
                preset1 = CaptureCurrentSettings();
                if (fileExists && !legacyMigrationPending)
                {
                    try
                    {
                        SaveLegacySnapshotAsPersonalPreset("legacy-preset-1", "Preset 1 (migrated)", preset1);
                        RefreshCatalog();
                        SetBasedOn(publishedPresets.FirstOrDefault(item =>
                            item.SourceKind == ModSettingsPresetSourceKind.Personal &&
                            string.Equals(item.Id, "legacy-preset-1", StringComparison.OrdinalIgnoreCase)), modified: false);
                        needsRewrite = true;
                    }
                    catch (Exception exception)
                    {
                        legacyMigrationPending = true;
                        DebugLogHelper.LogError(
                            log,
                            $"[{modName}] Could not publish the legacy lobby-settings preset; the original file was retained for retry; " +
                            DescribeMigrationFailure(exception));
                    }
                }
                LogRoutine(
                    fileExists && !legacyMigrationPending
                        ? $"[{modName}] Migrated legacy lobby settings to a personal preset."
                        : fileExists
                            ? $"[{modName}] Deferred legacy lobby-settings migration after a publication failure."
                        : $"[{modName}] Initialized editable settings from code defaults.");
            }

            // Materialize newly introduced/missing settings into the owned snapshot.
            // Otherwise a later metadata recovery could revive an obsolete top-level value.
            foreach (PresetPropertyAccessor property in persistedProperties)
                if (!preset1.ContainsKey(property.Name) && defaults.TryGetValue(property.Name, out byte[] defaultValue))
                {
                    preset1[property.Name] = defaultValue == null ? null : (byte[])defaultValue.Clone();
                    needsRewrite = true;
                }

            if (!legacyMigrationPending && !legacyPresetImportCompleted)
            {
                legacyPresetImportCompleted = MigrateStagedExports();
                RefreshCatalog();
                needsRewrite = true;
            }

            string storedBasedOnStableId = basedOnStableId;
            RestoreBasedOnMetadata();
            if (storedBasedOnStableId.Length != 0 && basedOnStableId.Length == 0)
                needsRewrite = true;
            LogRoutine($"[{modName}] Loaded editable lobby-settings working state; basedOn={SanitizeLogValue(basedOnStableId)}, modified={presetDirty}.");

            active = true;
            if (dynamicProvider != null)
            {
                // The external provider owns startup files; never revive a stale pre-restart working copy.
                preset1 = CaptureCurrentSettings();
                SetBasedOn(null, false);
            }
            ApplySnapshot(preset1, 0, writeLocalStorage: false);
            if (legacyPublishedToMaterialize != null)
                ApplyPublishedPreset(legacyPublishedToMaterialize, writeLocalStorage: false);

            // Persist the envelope immediately after reading a legacy top-level payload.
            // Otherwise an unchanged legacy file would be re-imported on every startup and
            // could never retain the stable identity of a subsequently selected public preset.
            if (needsRewrite && !legacyMigrationPending)
                WriteCombinedPayload();
        }

        private string DescribeMigrationFailure(Exception error) =>
            $"stage=legacy-preset-publication, destinationDirectory={personalPresetDirectory}, " +
            $"destinationDirectoryLength={personalPresetDirectory.Length}, {storage.DiagnosticPaths}, " +
            $"errorType={error.GetType().FullName}, hresult=0x{error.HResult:X8}: {error}";

        private void WriteCombinedPayload()
        {
            if (persistedProperties.Length == 0 || legacyMigrationPending)
                return;

            Dictionary<string, byte[]> payload = ComposeSafeTopLevelSnapshot();
            payload[SchemaVersionKey] = MessagePackSerializer.Serialize(SchemaVersion);
            payload[CurrentSettingsKey] = MessagePackSerializer.Serialize(preset1 ?? Clone(defaults));
            if (!string.IsNullOrEmpty(basedOnStableId))
                payload[BasedOnPresetKey] = MessagePackSerializer.Serialize(basedOnStableId);
            payload[PresetDirtyKey] = MessagePackSerializer.Serialize(presetDirty);
            payload[LegacyPresetImportCompletedKey] = MessagePackSerializer.Serialize(legacyPresetImportCompleted);

            storage.Write(payload);
        }

        private Dictionary<string, byte[]> ComposeSafeTopLevelSnapshot()
        {
            Dictionary<string, byte[]> snapshot =
                new Dictionary<string, byte[]>(StringComparer.Ordinal);
            Dictionary<string, byte[]> ownedPreset = preset1 ?? defaults;

            foreach (PresetPropertyAccessor property in persistedProperties)
            {
                bool mayCaptureLive = !owner.IsMissionPresetSelected &&
                    (IsClientProperty(property) || owner.IsLocalHost);
                if (mayCaptureLive)
                {
                    StoreProperty(snapshot, property);
                    continue;
                }

                // Preserve the user's own host preset instead of serializing a
                // remote host value or an externally owned Trail value.
                if (ownedPreset.TryGetValue(property.Name, out byte[] bytes))
                    snapshot[property.Name] = bytes == null ? null : (byte[])bytes.Clone();
                else if (defaults.TryGetValue(property.Name, out bytes))
                    snapshot[property.Name] = bytes == null ? null : (byte[])bytes.Clone();
            }

            return snapshot;
        }

        private Dictionary<string, byte[]> ReadSnapshot(
            Dictionary<string, byte[]> payload,
            string key)
        {
            if (!payload.TryGetValue(key, out byte[] bytes))
                return null;

            return MessagePackSerializer.Deserialize<Dictionary<string, byte[]>>(bytes);
        }

    }
}
