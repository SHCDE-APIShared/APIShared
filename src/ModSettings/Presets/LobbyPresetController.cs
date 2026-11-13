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
    /// <summary>Owns one participant's preset protocol and state through an explicit settings host; it has no ViewModel dependency.</summary>
    internal sealed partial class LobbyPresetController
    {
        internal const string SchemaVersionKey = "__SerpPresetSchemaVersion";
        internal const string ActivePresetKey = "__SerpActivePreset";
        internal const string Preset1Key = "__SerpPreset1";
        internal const string Preset2Key = "__SerpPreset2";
        internal const string PublishedPresetKey = "__SerpPublishedPreset";
        internal const string CurrentSettingsKey = "__SerpCurrentSettings";
        internal const string BasedOnPresetKey = "__SerpBasedOnPreset";
        internal const string PresetDirtyKey = "__SerpPresetDirty";
        internal const string LegacyPresetImportCompletedKey = "__SerpLegacyPresetImportCompleted";

        private const int SchemaVersion = 3;

        private readonly ILobbyPresetHost owner;
        private readonly Type settingsType;
        private readonly ManualLogSource log;
        private readonly string modName;
        private readonly bool routineLoggingEnabled;
        private readonly LobbyPresetStorage storage;
        private readonly string pluginDirectory;
        private readonly string personalPresetDirectory;
        private readonly string legacyPersonalPresetDirectory;
        private readonly string personalStorageRoot;
        private readonly string targetGuid;
        private readonly Version targetVersion;
        private readonly IDynamicPresetSettingsProvider dynamicProvider;
        private readonly PresetPropertyAccessor[] persistedProperties;
        private readonly PresetPropertyAccessor[] hostProperties;
        private readonly PresetPropertyAccessor[] clientProperties;
        private readonly PresetPropertyAccessor hostSettingsActivationProperty;
        private readonly PresetPropertyAccessor clientSettingsActivationProperty;
        private readonly Dictionary<string, PresetPropertyAccessor> persistedPropertiesByName;
        private readonly Dictionary<string, bool> missionIndependentByName;
        private readonly List<PublishedModSettingsPreset> publishedPresets =
            new List<PublishedModSettingsPreset>();

        private Dictionary<string, byte[]> defaults;
        private Dictionary<string, byte[]> preset1;
        private Dictionary<string, byte[]> missionPreset;
        private bool active;
        private bool applying;
        private bool applyingMissionSnapshot;
        private PublishedModSettingsPreset activePublishedPreset;
        private PublishedModSettingsPreset suspendedPublishedPreset;
        private string basedOnStableId = string.Empty;
        private string basedOnName = string.Empty;
        private string basedOnSource = string.Empty;
        private bool presetDirty;
        private bool legacyPresetImportCompleted;
        private bool legacyMigrationPending;
        private string suspendedBasedOnStableId = string.Empty;
        private string suspendedBasedOnName = string.Empty;
        private string suspendedBasedOnSource = string.Empty;
        private bool suspendedPresetDirty;
        private string missionPresetLabel = string.Empty;
        private string missionContextLabel = string.Empty;
        private PublishedModSettingsPreset missionBasedOnPreset;
        private bool missionPresetDirty;
#if API_SHARED_PRESET_TESTS
        internal int TestWriteCount => storage.TestWriteCount;
#endif

        public LobbyPresetController(
            ILobbyPresetHost owner,
            ManualLogSource log,
            string pluginAssemblyLocation,
            string modName,
            string targetGuid,
            Version targetVersion,
            bool logRoutineActivity)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            this.log = log;
            this.modName = modName ?? throw new ArgumentNullException(nameof(modName));
            routineLoggingEnabled = logRoutineActivity;

            pluginDirectory = Path.GetDirectoryName(pluginAssemblyLocation)
                ?? throw new ArgumentException(
                    $"Cannot determine the plugin directory for [{pluginAssemblyLocation}].",
                    nameof(pluginAssemblyLocation));
            this.targetGuid = ModSettingsPresetCatalog.ValidateTargetGuid(
                targetGuid ?? throw new ArgumentNullException(nameof(targetGuid)));
            this.targetVersion = targetVersion;
            string safeFileName = string.Concat(modName.Split(Path.GetInvalidFileNameChars()));
            string filePath = Path.Combine(
                pluginDirectory,
                LobbyModSettingsStorage.STORAGE_FOLDER_NAME,
                safeFileName + LobbyModSettingsStorage.FILE_EXTENSION);
            var paths = new PersonalSettingsPaths(pluginAssemblyLocation, this.targetGuid);
            personalStorageRoot = paths.DocumentsDirectory;
            storage = new LobbyPresetStorage(paths.SettingsFile, log, this.modName, filePath, personalStorageRoot);
            personalPresetDirectory = paths.Presets;
            legacyPersonalPresetDirectory = Path.Combine(
                pluginDirectory,
                LobbyModSettingsStorage.STORAGE_FOLDER_NAME,
                "Presets",
                "Override",
                this.targetGuid);

            dynamicProvider = owner.DynamicSettingsProvider;
            // Dynamic settings have keys, not properties; only a settings class can declare renames.
            settingsType = dynamicProvider != null ? null : owner.SettingsTarget.GetType();
            persistedProperties = dynamicProvider != null
                ? dynamicProvider.GetSettings().Select(item => new PresetPropertyAccessor(item, dynamicProvider)).ToArray()
                : owner.SettingsTarget.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(item => new PresetPropertyAccessor(item)).Where(IsPersistedProperty).ToArray();
            if (persistedProperties.Length > ModSettingsPresetJson.MaximumSettings)
                throw new InvalidDataException("Too many preset settings.");
            if (persistedProperties.Any(x => x.RequiresRestart) && owner.SettingsApplicationBackend == null)
                throw new InvalidOperationException("RequiresRestart settings need a configuration application backend.");
            persistedPropertiesByName = persistedProperties
                .ToDictionary(property => property.Name, StringComparer.Ordinal);
            missionIndependentByName = persistedProperties.ToDictionary(property => property.Name, property => property.MissionIndependent, StringComparer.Ordinal);
            if (dynamicProvider == null)
                foreach (var property in owner.SettingsTarget.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (!missionIndependentByName.ContainsKey(property.Name))
                        missionIndependentByName[property.Name] = property.GetCustomAttribute<SyncHostOnlyAttribute>() == null ||
                            property.IsDefined(typeof(MissionIndependentAttribute), true);
            hostProperties = persistedProperties.Where(IsHostProperty).ToArray();
            clientProperties = persistedProperties.Where(IsClientProperty).ToArray();
            // The separated persistence interface is optional: backends without
            // IModSettingsPersonalPersistenceBackend store personal values only in the preset storage.
            if (owner.SettingsApplicationBackend != null && persistedProperties.Any(p => p.MissionIndependent) &&
                !(owner.SettingsApplicationBackend is IModSettingsPersonalPersistenceBackend))
                NativeApiLog.Warning(log, $"[{modName}] Backend has personal settings but no IModSettingsPersonalPersistenceBackend; " +
                    "personal values are persisted in the preset storage only.");
            hostSettingsActivationProperty = FindSettingsActivationProperty(hostProperties, "EnableMod");
            clientSettingsActivationProperty = FindSettingsActivationProperty(clientProperties, "EnableClientFeatures", "EnableMod");
            if (persistedProperties.Length != 0)
                RefreshCatalog();
        }

        // Restart preparations store the raw mission source state, never a rendered status line:
        // rendering it again after the restart keeps the current language and avoids nesting.
        internal bool TryCaptureMissionStatus(out string sourceLabel, out string presetId, out bool modified)
        {
            sourceLabel = presetId = string.Empty;
            modified = false;
            if (!owner.IsMissionPresetSelected) return false;
            sourceLabel = missionBasedOnPreset?.Name ?? missionPresetLabel;
            presetId = missionBasedOnPreset?.StableId ?? string.Empty;
            modified = missionPresetDirty;
            return true;
        }

        // Runs after EnterMissionPreset has restored the prepared snapshot of the same context.
        internal void RestoreMissionStatus(string sourceLabel, string presetId, bool modified)
        {
            if (!owner.IsMissionPresetSelected) return;
            PublishedModSettingsPreset preset = string.IsNullOrEmpty(presetId) ? null : publishedPresets.FirstOrDefault(item =>
                string.Equals(item.StableId, presetId, StringComparison.OrdinalIgnoreCase));
            missionBasedOnPreset = preset;
            // An unavailable preset keeps its captured name as a plain source label.
            if (preset == null && !string.IsNullOrWhiteSpace(sourceLabel)) missionPresetLabel = sourceLabel;
            missionPresetDirty = modified;
        }

        // Journals written before the raw restart status contain only the English status line
        // rendered by CaptureRestartSources. Its fixed " (modified)" suffix becomes the dirty state.
        internal void RestorePreparationLabel(string label)
        {
            const string legacyModified = " (modified)";
            missionBasedOnPreset = null;
            if (label.EndsWith(legacyModified, StringComparison.Ordinal))
            {
                label = label.Substring(0, label.Length - legacyModified.Length);
                missionPresetDirty = true;
            }
            missionPresetLabel = label;
        }

        public IReadOnlyList<PublishedModSettingsPreset> PublishedPresets => publishedPresets;
        public string TargetGuid => targetGuid;
        public bool HasPersistentSettings => persistedProperties.Length != 0;

        public string GetStatusText(
            string basedOnText,
            string modifiedText,
            string personalSourceText,
            string bundledSourceText,
            string externalSourceText)
        {
            bool missionSelected = owner.IsMissionPresetSelected;
            if (missionSelected && missionBasedOnPreset == null)
            {
                string context = missionContextLabel;
                if (!string.Equals(context, missionPresetLabel, StringComparison.Ordinal) && missionPresetLabel.Length != 0)
                    context = context.Length != 0 && missionPresetLabel.StartsWith(context + " · ", StringComparison.Ordinal)
                        ? missionPresetLabel // A legacy restart journal already captured the full status.
                        : context + " · " + (basedOnText ?? "Based on") + ": " + missionPresetLabel;
                string modified = " (" + (modifiedText ?? "modified") + ")";
                return context + (missionPresetDirty && !context.EndsWith(modified, StringComparison.Ordinal) ? modified : "");
            }
            string sourceName = missionSelected ? missionBasedOnPreset.Name : basedOnName;
            if (string.IsNullOrWhiteSpace(sourceName))
                return string.Empty;
            string status = (basedOnText ?? "Based on") + ": " + sourceName;
            PublishedModSettingsPreset sourcePreset = missionSelected ? missionBasedOnPreset : activePublishedPreset;
            string localizedSource = sourcePreset == null
                ? basedOnSource
                : DescribeSource(
                    sourcePreset,
                    personalSourceText,
                    bundledSourceText,
                    externalSourceText);
            if (!string.IsNullOrWhiteSpace(localizedSource))
                status += " · " + localizedSource;
            if (missionSelected ? missionPresetDirty : presetDirty)
                status += " (" + (modifiedText ?? "modified") + ")";
            return missionSelected && missionContextLabel.Length != 0 ? missionContextLabel + " · " + status : status;
        }

        internal bool IsPersistentSetting(string name) => name != null && persistedPropertiesByName.ContainsKey(name);

        public int MissionPresetIndex => 1;

        public bool HasHostSettings => hostProperties.Length != 0;

        public bool HasClientSettings => clientProperties.Length != 0;

        public bool HasHostSettingsActivation => hostSettingsActivationProperty != null;

        public bool HasClientSettingsActivation => clientSettingsActivationProperty != null;

        public bool HostSettingsEnabled => ReadSettingsActivation(hostSettingsActivationProperty);

        public bool ClientSettingsEnabled => ReadSettingsActivation(clientSettingsActivationProperty);

        public void SetHostSettingsEnabled(bool value) =>
            WriteSettingsActivation(hostSettingsActivationProperty, value);

        public void SetClientSettingsEnabled(bool value) =>
            WriteSettingsActivation(clientSettingsActivationProperty, value);

        public bool IsHostSettingsActivationProperty(string propertyName) =>
            IsSettingsActivationProperty(hostSettingsActivationProperty, propertyName);

        public bool IsClientSettingsActivationProperty(string propertyName) =>
            IsSettingsActivationProperty(clientSettingsActivationProperty, propertyName);

        public bool IsApplyingSnapshot => applying;
        public bool IsApplyingMissionSnapshot => applyingMissionSnapshot;

        public bool IsMissionIndependentPropertyName(string propertyName) =>
            propertyName != null && missionIndependentByName.TryGetValue(propertyName, out bool independent) && independent;

        public bool HostActivationMissionIndependent => hostSettingsActivationProperty?.MissionIndependent == true;

        private static bool IsMissionProperty(PresetPropertyAccessor property) => !property.MissionIndependent;

        public bool IsHostPropertyName(string propertyName) =>
            !string.IsNullOrEmpty(propertyName) &&
            persistedPropertiesByName.TryGetValue(propertyName, out PresetPropertyAccessor property) &&
            IsHostProperty(property);

#if API_SHARED_PRESET_TESTS
        public int NormalizeSelection(int selected, bool missionContext)
        {
            if (missionContext && selected == MissionPresetIndex)
                return selected;
            return 0;
        }
#endif

        public void CaptureDefaults()
        {
            defaults = dynamicProvider == null ? CaptureCurrentSettings() : persistedProperties.ToDictionary(
                item => item.Name, item => MessagePackSerializer.Serialize(item.PropertyType, item.DefaultValue), StringComparer.Ordinal);
        }

        public void SetDefaultValue<T>(string propertyName, T value)
        {
            if (string.IsNullOrWhiteSpace(propertyName) ||
                !persistedPropertiesByName.TryGetValue(propertyName, out PresetPropertyAccessor property))
            {
                throw new InvalidDataException($"Unknown persistent setting [{propertyName}].");
            }
            if (property.PropertyType != typeof(T))
            {
                throw new InvalidDataException(
                    $"Default setting [{propertyName}] expects [{property.PropertyType.FullName}], not [{typeof(T).FullName}].");
            }
            if (value == null && property.PropertyType.IsValueType &&
                Nullable.GetUnderlyingType(property.PropertyType) == null)
            {
                throw new InvalidDataException($"Default setting [{propertyName}] cannot be null.");
            }

            byte[] serialized = MessagePackSerializer.Serialize(property.PropertyType, value);
            MessagePackSerializer.Deserialize(property.PropertyType, serialized);
            defaults[property.Name] = serialized;
        }

        private void RestoreBasedOnMetadata()
        {
            activePublishedPreset = publishedPresets.FirstOrDefault(item =>
                string.Equals(item.StableId, basedOnStableId, StringComparison.OrdinalIgnoreCase));
            if (activePublishedPreset == null)
            {
                if (basedOnStableId.Length != 0)
                    LogRoutine($"[{modName}] Preset source [{SanitizeLogValue(basedOnStableId)}] is unavailable; retained the materialized working settings.");
                basedOnStableId = string.Empty;
                basedOnName = string.Empty;
                basedOnSource = string.Empty;
                return;
            }
            basedOnName = activePublishedPreset.Name;
            basedOnSource = DescribeSource(activePublishedPreset);
        }

        private PublishedModSettingsPreset FindLegacyPublishedPreset(string legacyStableId)
        {
            if (string.IsNullOrWhiteSpace(legacyStableId))
                return null;
            return publishedPresets.FirstOrDefault(item =>
                string.Equals(item.ProviderGuid + "\n" + item.TargetGuid + "\n" + item.Id,
                    legacyStableId,
                    StringComparison.OrdinalIgnoreCase));
        }

        private void SetBasedOn(PublishedModSettingsPreset preset, bool modified)
        {
            activePublishedPreset = preset;
            basedOnStableId = preset?.StableId ?? string.Empty;
            basedOnName = preset?.Name ?? string.Empty;
            basedOnSource = preset == null ? string.Empty : DescribeSource(preset);
            presetDirty = preset != null && modified;
        }

        private static string DescribeSource(PublishedModSettingsPreset preset)
        {
            return DescribeSource(preset, "Personal presets", "Bundled with this mod", "External presets");
        }

        private static string DescribeSource(
            PublishedModSettingsPreset preset,
            string personalSourceText,
            string bundledSourceText,
            string externalSourceText)
        {
            switch (preset.SourceKind)
            {
                case ModSettingsPresetSourceKind.Personal: return personalSourceText;
                case ModSettingsPresetSourceKind.Bundled: return bundledSourceText;
                case ModSettingsPresetSourceKind.External: return externalSourceText + ": " + preset.ProviderName;
                default: return preset.ProviderName;
            }
        }

        private static int NormalizePreset(int selected)
        {
            return selected == 1 ? 1 : 0;
        }

        private static bool IsPersistedProperty(PresetPropertyAccessor property)
        {
            return property.GetCustomAttribute<DoNotPersistAttribute>() == null &&
                (property.GetCustomAttribute<SyncPerPlayerAttribute>() != null ||
                property.GetCustomAttribute<SyncHostOnlyAttribute>() != null ||
                property.GetCustomAttribute<PresetLocalAttribute>() != null);
        }

        private static bool IsHostProperty(PresetPropertyAccessor property) =>
            property.GetCustomAttribute<SyncHostOnlyAttribute>() != null;

        private static bool IsClientProperty(PresetPropertyAccessor property) =>
            property.GetCustomAttribute<SyncHostOnlyAttribute>() == null &&
            (property.GetCustomAttribute<SyncPerPlayerAttribute>() != null ||
                property.GetCustomAttribute<PresetLocalAttribute>() != null);

        private static PresetPropertyAccessor FindSettingsActivationProperty(
            IEnumerable<PresetPropertyAccessor> properties,
            params string[] preferredNames)
        {
            foreach (string name in preferredNames)
            {
                PresetPropertyAccessor property = properties.FirstOrDefault(item =>
                    item.Name == name &&
                    item.PropertyType == typeof(bool) &&
                    item.CanRead &&
                    item.CanWrite);
                if (property != null)
                    return property;
            }

            return null;
        }

        private bool ReadSettingsActivation(PresetPropertyAccessor property) =>
            property != null && (bool)property.GetValue(owner.SettingsTarget);

        private void WriteSettingsActivation(PresetPropertyAccessor property, bool value)
        {
            if (property == null || ReadSettingsActivation(property) == value)
                return;

            property.SetValue(owner.SettingsTarget, value);
        }

        private static bool IsSettingsActivationProperty(
            PresetPropertyAccessor property,
            string propertyName) =>
            property != null && string.Equals(property.Name, propertyName, StringComparison.Ordinal);

        public static bool IsNetworkSyncInProgress()
        {
            return GameXAMLManagerAPI.Instance != null &&
                GameXAMLManagerAPI.Instance.CurrentLobbyModSettingsChangeOrigin ==
                LobbyModSettingsChangeOrigin.IncomingNetwork;
        }

        private static Dictionary<string, byte[]> CopyProperties(
            Dictionary<string, byte[]> source,
            IEnumerable<PresetPropertyAccessor> properties)
        {
            Dictionary<string, byte[]> result =
                new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (source == null)
                return result;

            foreach (PresetPropertyAccessor property in properties)
            {
                if (source.TryGetValue(property.Name, out byte[] bytes))
                    result[property.Name] = bytes == null ? null : (byte[])bytes.Clone();
            }
            return result;
        }

        private void LogRoutine(string message)
        {
            if (routineLoggingEnabled)
                NativeApiLog.Debug(log, message);
        }

        private static Dictionary<string, byte[]> Clone(
            Dictionary<string, byte[]> source)
        {
            Dictionary<string, byte[]> clone =
                new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (source == null)
                return clone;

            foreach (KeyValuePair<string, byte[]> entry in source)
                clone[entry.Key] = entry.Value == null ? null : (byte[])entry.Value.Clone();
            return clone;
        }
    }
}
