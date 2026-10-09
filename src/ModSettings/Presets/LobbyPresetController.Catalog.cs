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
    /// <summary>Personal preset discovery, publication, deletion and migration; stable identities and format validation.</summary>
    internal sealed partial class LobbyPresetController
    {
        public void RefreshCatalog()
        {
            if (persistedProperties.Length == 0)
            {
                publishedPresets.Clear();
                return;
            }

            Directory.CreateDirectory(personalPresetDirectory);
            var refreshed = new List<PublishedModSettingsPreset>();
            foreach (PublishedModSettingsPreset preset in ModSettingsPresetCatalog.Discover(
                targetGuid,
                targetVersion,
                pluginDirectory,
                personalPresetDirectory,
                log))
            {
                try
                {
                    ValidatePublishedPreset(preset);
                    refreshed.Add(preset);
                }
                catch (Exception exception)
                {
                    DebugLogHelper.LogError(log, $"Published preset [{preset.SourcePath}] is incompatible with [{modName}]: {exception.Message}");
                }
            }
            publishedPresets.Clear();
            publishedPresets.AddRange(refreshed);
            activePublishedPreset = publishedPresets.FirstOrDefault(item =>
                string.Equals(item.StableId, basedOnStableId, StringComparison.OrdinalIgnoreCase));
            if (activePublishedPreset != null)
            {
                basedOnName = activePublishedPreset.Name;
                basedOnSource = DescribeSource(activePublishedPreset);
            }
            bool clearedMissingSource = active && basedOnStableId.Length != 0 && activePublishedPreset == null;
            if (clearedMissingSource)
            {
                LogRoutine($"[{modName}] Preset source [{SanitizeLogValue(basedOnStableId)}] is unavailable; retained the materialized working settings.");
                basedOnStableId = string.Empty;
                basedOnName = string.Empty;
                basedOnSource = string.Empty;
                presetDirty = false;
            }
            if (clearedMissingSource && active)
                WriteCombinedPayload();
        }

        public string CreateUniquePersonalPresetId(string name)
        {
            string baseId = CreatePersonalPresetId(name);
            string candidate = baseId;
            int suffix = 2;
            var occupied = new HashSet<string>(
                publishedPresets.Where(item => item.SourceKind == ModSettingsPresetSourceKind.Personal)
                    .Select(item => item.Id),
                StringComparer.OrdinalIgnoreCase);
            while (occupied.Contains(candidate) || File.Exists(Path.Combine(personalPresetDirectory, "preset_" + candidate + ".json")))
                candidate = baseId + "-" + suffix++;
            return candidate;
        }

        private static string CreatePersonalPresetId(string name)
        {
            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                throw new InvalidDataException("A preset name is required.");
            var result = new System.Text.StringBuilder(trimmed.Length);
            bool separator = false;
            foreach (char character in trimmed.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_')
                {
                    result.Append(character);
                    separator = false;
                }
                else if (!separator)
                {
                    result.Append('-');
                    separator = true;
                }
            }
            string id = result.ToString().Trim('-');
            if (id.Length == 0)
                throw new InvalidDataException("The preset name does not contain a usable id.");
            return id.Length <= 128 ? id : id.Substring(0, 128).TrimEnd('-');
        }

        private bool MigrateStagedExports()
        {
            string staged = Path.Combine(
                pluginDirectory,
                LobbyModSettingsStorage.STORAGE_FOLDER_NAME,
                "PresetExports",
                "Override",
                targetGuid);
            if (!Directory.Exists(staged))
                return true;
            Directory.CreateDirectory(personalPresetDirectory);
            bool succeeded = true;
            foreach (string source in Directory.GetFiles(staged, "preset_*.json", SearchOption.TopDirectoryOnly))
            {
                string destination = Path.Combine(personalPresetDirectory, Path.GetFileName(source));
                if (File.Exists(destination))
                    continue;
                try
                {
                    PublishPresetJson(destination, File.ReadAllText(source), overwrite: false);
                    LogRoutine($"[{modName}] Imported staged personal preset [{source}] to [{destination}].");
                }
                catch (Exception exception)
                {
                    succeeded = false;
                    DebugLogHelper.LogError(
                        log,
                        $"[{modName}] Could not import staged personal preset [{source}]: {exception.Message}");
                }
            }
            return succeeded;
        }

        private void SaveLegacySnapshotAsPersonalPreset(
            string id,
            string name,
            Dictionary<string, byte[]> snapshot)
        {
            Directory.CreateDirectory(personalPresetDirectory);
            string path = Path.Combine(personalPresetDirectory, "preset_" + id + ".json");
            var settings = new Dictionary<string, PublishedPresetSetting>(StringComparer.Ordinal);
            foreach (PresetPropertyAccessor property in persistedProperties)
            {
                if (snapshot == null || !snapshot.TryGetValue(property.Name, out byte[] bytes) || bytes == null)
                    continue;
                object value = MessagePackSerializer.Deserialize(property.PropertyType, bytes);
                settings[property.Name] = new PublishedPresetSetting
                {
                    Mode = PublishedPresetValueMode.Fixed,
                    Value = ModSettingsPresetJson.ToJsonValue(property.PropertyType, value),
                };
            }
            string json = ModSettingsPresetJson.Serialize(
                targetGuid,
                id,
                name,
                "Migrated from the previous local Preset 1/2 storage.",
                string.Empty,
                string.Empty,
                settings);
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path);
                if (string.Equals(existing, json, StringComparison.Ordinal))
                    return;
                throw new InvalidDataException(
                    $"The migration target [{path}] already exists with different contents.");
            }
            PublishPresetJson(path, json, overwrite: false);
        }

        private PresetPropertyAccessor[] descriptorOrder;
        public IReadOnlyList<PresetSettingDescriptor> GetSettingDescriptors()
        {
            if (descriptorOrder == null)
                descriptorOrder = persistedProperties.OrderBy(p => IsHostProperty(p) ? PresetSettingScope.Host :
                    p.GetCustomAttribute<SyncPerPlayerAttribute>() != null ? PresetSettingScope.Player : PresetSettingScope.Local)
                    .ThenBy(p => p.Name, StringComparer.Ordinal).ToArray();
            // Return fresh descriptors; capabilities remain live and callers cannot mutate the cache.
            return
            descriptorOrder.Select(property => new PresetSettingDescriptor
            {
                PropertyName = property.Name,
                PropertyType = property.PropertyType,
                Group = property.Group,
                DisplayName = property.DisplayName,
                RequiresRestart = property.RequiresRestart,
                Scope = IsHostProperty(property)
                    ? PresetSettingScope.Host
                    : property.GetCustomAttribute<SyncPerPlayerAttribute>() != null
                        ? PresetSettingScope.Player
                        : PresetSettingScope.Local,
            }).ToArray();
        }

        public string SavePersonalPreset(
            string id,
            string name,
            string description,
            IEnumerable<PresetSaveSelection> selections,
            bool overwrite)
        {
            PresetSaveSelection[] selectedSettings = (selections ?? Enumerable.Empty<PresetSaveSelection>()).ToArray();
            if (selectedSettings.Length == 0)
                throw new InvalidDataException("At least one setting must be selected for saving.");
            IGrouping<string, PresetSaveSelection> duplicate = selectedSettings
                .GroupBy(item => item?.PropertyName ?? string.Empty, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
                throw new InvalidDataException($"Setting [{duplicate.Key}] was selected more than once.");

            var exported = new Dictionary<string, PublishedPresetSetting>(StringComparer.Ordinal);
            foreach (PresetSaveSelection selection in selectedSettings)
            {
                if (selection == null || !persistedPropertiesByName.TryGetValue(selection.PropertyName, out PresetPropertyAccessor property))
                    throw new InvalidDataException($"Unknown persistent setting [{selection?.PropertyName}].");
                object value = null;
                if (selection.Mode == PublishedPresetValueMode.Fixed)
                {
                    object currentValue = property.GetValue(owner.SettingsTarget);
                    if (IsHostProperty(property) && !owner.IsLocalHost)
                    {
                        Dictionary<string, byte[]> ownedPreset = preset1 ?? defaults;
                        if (!ownedPreset.TryGetValue(property.Name, out byte[] ownedBytes) || ownedBytes == null)
                            throw new InvalidDataException($"Locally owned value for [{property.Name}] is unavailable.");
                        currentValue = MessagePackSerializer.Deserialize(property.PropertyType, ownedBytes);
                    }
                    value = ModSettingsPresetJson.ToJsonValue(property.PropertyType, currentValue);
                }
                exported.Add(property.Name, new PublishedPresetSetting { Mode = selection.Mode, Value = value });
            }

            string json = ModSettingsPresetJson.Serialize(targetGuid, id, name, description, string.Empty, string.Empty, exported);
            string safeId = string.Concat(id.Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(safeId))
                throw new InvalidDataException("Preset id has no safe filename characters.");
            string path = Path.Combine(
                personalPresetDirectory,
                "preset_" + safeId + ".json");
            PublishPresetJson(path, json, overwrite);
            RefreshCatalog();
            LogRoutine(
                $"[{modName}] {(overwrite ? "Overwrote" : "Saved")} personal preset [{id}] at [{path}].");
            return path;
        }

        public string ImportPresetJson(string json)
        {
            PublishedModSettingsPreset imported = ModSettingsPresetJson.Parse(json, targetGuid, modName, targetGuid, "");
            ValidatePublishedPreset(imported);
            string safeId = string.Concat(imported.Id.Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(safeId)) throw new InvalidDataException("Invalid preset id.");
            string path = Path.Combine(personalPresetDirectory, "preset_" + safeId + ".json");
            PublishPresetJson(path, ModSettingsPresetJson.Serialize(targetGuid, imported.Id, imported.Name,
                imported.Description, imported.MinimumTargetVersion, imported.MaximumTargetVersion, imported.Settings), false);
            RefreshCatalog();
            return path;
        }

        public void LogPresetOperationCancelled(string operation, string id)
        {
            LogRoutine(
                $"[{modName}] Personal preset operation [{operation}] for [{id ?? string.Empty}] was cancelled.");
        }

        public void LogPresetOperationFailure(string operation, string id, Exception exception)
        {
            DebugLogHelper.LogError(
                log,
                $"[{modName}] Personal preset operation [{operation}] for [{id ?? string.Empty}] failed: {exception}");
        }

        public void DeletePersonalPreset(PublishedModSettingsPreset preset)
        {
            if (preset == null ||
                preset.SourceKind != ModSettingsPresetSourceKind.Personal ||
                !publishedPresets.Contains(preset))
            {
                throw new InvalidDataException("Only an available personal preset can be deleted.");
            }

            string path = Path.GetFullPath(preset.SourcePath ?? string.Empty);
            ModSettingsPresetCatalog.ValidatePersonalWritePath(
                pluginDirectory,
                personalPresetDirectory,
                path);
            if (!File.Exists(path))
                throw new FileNotFoundException("The selected personal preset no longer exists.", path);

            string stableId = preset.StableId;
            File.Delete(path);

            bool metadataChanged = false;
            if (string.Equals(basedOnStableId, stableId, StringComparison.OrdinalIgnoreCase))
            {
                activePublishedPreset = null;
                basedOnStableId = string.Empty;
                basedOnName = string.Empty;
                basedOnSource = string.Empty;
                presetDirty = false;
                metadataChanged = true;
            }
            if (string.Equals(suspendedBasedOnStableId, stableId, StringComparison.OrdinalIgnoreCase))
            {
                suspendedPublishedPreset = null;
                suspendedBasedOnStableId = string.Empty;
                suspendedBasedOnName = string.Empty;
                suspendedBasedOnSource = string.Empty;
                suspendedPresetDirty = false;
                metadataChanged = true;
            }

            RefreshCatalog();
            if (metadataChanged && active)
                WriteCombinedPayload();
            LogRoutine($"[{modName}] Deleted personal preset [{SanitizeLogValue(stableId)}].");
        }

        private void PublishPresetJson(string path, string json, bool overwrite)
        {
            string directory = Path.GetDirectoryName(path);
            ModSettingsPresetCatalog.ValidatePersonalWritePath(
                pluginDirectory,
                personalPresetDirectory,
                path);
            Directory.CreateDirectory(directory);
            ModSettingsPresetCatalog.ValidatePersonalWritePath(
                pluginDirectory,
                personalPresetDirectory,
                path);
            string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporaryPath, json, new System.Text.UTF8Encoding(false));
                if (!overwrite)
                {
                    try
                    {
                        File.Move(temporaryPath, path);
                    }
                    catch (IOException)
                    {
                        if (File.Exists(path))
                            throw new PresetSaveFileExistsException(path);
                        throw;
                    }
                }
                else
                {
                    PresetAtomicPublishResult publish = PresetAtomicFilePublisher.Publish(temporaryPath, path);
                    if (!publish.Succeeded) throw publish.Error;
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

    }
}
