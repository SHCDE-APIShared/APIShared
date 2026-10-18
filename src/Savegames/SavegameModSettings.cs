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
    /// <summary>Persists compatible host settings and the mission permission context in saves.</summary>
    public static partial class SavegameModSettings
    {
        /// <summary>Excludes a plugin type and its derived types from automatic savegame settings capture and restoration.</summary>
        /// <remarks>Call before settings registration when an optional APIShared dependency prevents applying the exclusion attribute.
        /// This thread-safe, idempotent registration remains active until process exit; it does not disable presets or other providers.</remarks>
        /// <param name="pluginType">Plugin type owning a separate persistence protocol or non-mission settings.</param>
        /// <exception cref="ArgumentNullException">The plugin type is null.</exception>
        public static void ExcludePluginFromAutomaticSettings(Type pluginType) => SavegameSettingsParticipation.Exclude(pluginType);

        private const string Identifier = "APIShared-SavegameModSettings";
        private const string EntryName = "_SE_ModData_" + Identifier + ".msgpack";
        private const int SchemaVersion = 3;
        private const int MaximumBytes = 8 * 1024 * 1024;
        // One set of limits for capture, TryDeserialize and SavegameModSettingsRecordFormatter, so every
        // record the writer produces is readable. Lowering one changes which existing saves load.
        internal const int MaximumMods = 1024;
        internal const int MaximumPropertiesPerMod = 16384;
        internal const int MaximumPropertyBytes = 1024 * 1024;
        private static readonly object Sync = new object();
        private static ManualLogSource log;
        private static SavegameModSettingsRecord loaded;
        private static Dictionary<string, Dictionary<string, byte[]>> chosenCurrent;
        private static bool useChosenCurrent;
        private static bool legacyChoice;
        private static Dictionary<string, Dictionary<string, TrailCreatorRule>> activeCreatorRules;
        private static Func<Dictionary<string, Dictionary<string, TrailCreatorRule>>> creatorRulesProvider;
        private static Func<bool> trailCustomizeProvider;
        private static bool activeTrailCustomizeAllowed;
        private static string chosenPath;
        private static bool restoreFailed;
        private static bool initialized;
        private static readonly SavegameMissionPresetLease ActivePreset = new SavegameMissionPresetLease();
        private static IDisposable missionEndSubscription;

        internal static void Initialize(ManualLogSource logger)
        {
            lock (Sync)
            {
                if (initialized) return;
                APIShared.Internal.MissionEvents.SetOwner("APIShared_Serp");
                missionEndSubscription = APIShared.Internal.MissionEvents.Ended.Subscribe(OnMissionEnded);
                try
                {
                    if (!ModSaveDataAPI.Instance.RegisterModDataHandler(Identifier, Save, Load))
                        throw new InvalidOperationException("Savegame ModSettings handler already exists.");
                }
                catch
                {
                    missionEndSubscription.Dispose();
                    missionEndSubscription = null;
                    throw;
                }
                GameModeHelper.ReadSavedModeEvidence = () =>
                {
                    bool present = TryGetRestoredMode(out GameModeKind kind,
                        out GameModeLaunchVariant variant, out bool locked);
                    return new SavedModeEvidence(present, kind, variant, locked);
                };
                log = logger;
                initialized = true;
            }
            NativeApiLog.Debug(logger, "Savegame ModSettings handler registered after Script Extender startup cleanup.");
            try { SavegameLoadCoordinator.Initialize(logger); }
            catch (Exception error) { NativeApiLog.Error(logger, "Savegame load preparation could not initialize; multiplayer saves remain blocked: " + error); }
        }

        /// <summary>Reads only the selected save's own appended metadata for the load checkbox.</summary>
        public static bool CanUseCurrentSettings(string savePath)
        {
            if (!TryReadRecord(savePath, out SavegameModSettingsRecord record)) return false;
            return IsCurrentChoiceAllowed(record);
        }

        /// <summary>Describes the selected save's modsettings choice.</summary>
        public static SavegameLoadChoiceState GetLoadChoiceState(string savePath)
        {
            RecordReadState state = ReadRecord(savePath, out SavegameModSettingsRecord record);
            return state == RecordReadState.Missing ? SavegameLoadChoiceState.Legacy :
                state == RecordReadState.Invalid ? SavegameLoadChoiceState.Invalid :
                IsCurrentChoiceAllowed(record) ? SavegameLoadChoiceState.Selectable :
                SavegameLoadChoiceState.SavedOnly;
        }

        /// <summary>Registers the ExtendedData source of author rules for a newly launched Trail.</summary>
        public static void RegisterTrailCreatorRulesProvider(
            Func<Dictionary<string, Dictionary<string, TrailCreatorRule>>> provider,
            Func<bool> customizeProvider)
        {
            lock (Sync)
            {
                creatorRulesProvider = provider;
                trailCustomizeProvider = customizeProvider;
            }
        }

        internal static bool IsCurrentChoiceAllowed(SavegameModSettingsRecord record)
        {
            if (record == null || record.LockedByConflict) return false;
            GameModeKind kind = (GameModeKind)record.Kind;
            GameModeLaunchVariant variant = (GameModeLaunchVariant)record.Variant;
            if (kind == GameModeKind.CustomTrail || kind == GameModeKind.CoopTrail)
                return record.TrailCustomizeAllowed;
            return kind == GameModeKind.CustomGame ||
                (variant != GameModeLaunchVariant.Standard &&
                 (kind == GameModeKind.CustomTrail || kind == GameModeKind.CoopTrail ||
                  kind == GameModeKind.VanillaTrail || kind == GameModeKind.SandsOfTime));
        }

        /// <summary>Returns compatible settings endpoints keyed by the owning plugin GUID.</summary>
        public static IReadOnlyDictionary<string, IModSettingsPresetEndpoint> GetCompatibleParticipants() =>
            FindParticipants().ToDictionary(item => item.Key, item => item.Value.Endpoint, StringComparer.Ordinal);

        /// <summary>Records the confirmed host choice for exactly one selected save.</summary>
        public static void PrepareLoad(string savePath, bool useCurrentSettings)
        {
            ReleaseUnstartedPreparation();
            string path = Normalize(savePath);
            SavegameLoadChoiceState state = GetLoadChoiceState(path);
            Dictionary<string, Dictionary<string, byte[]>> current = CaptureCompatibleHostSettings();
            lock (Sync)
            {
                chosenPath = path.Length == 0 ? null : path;
                chosenCurrent = current;
                useChosenCurrent = state == SavegameLoadChoiceState.Legacy ||
                    state == SavegameLoadChoiceState.Selectable && useCurrentSettings;
                legacyChoice = state == SavegameLoadChoiceState.Legacy;
            }
        }

        private static Dictionary<string, byte[]> ResolveStartupSnapshot(string modId, CompatibleMod participant,
            SavegameModSettingsRecord record, Dictionary<string, Dictionary<string, byte[]>> values,
            Dictionary<string, Dictionary<string, byte[]>> current, bool locked)
        {
                    Dictionary<string, byte[]> snapshot =
                        participant.Endpoint.System_CreateDisabledMissionPresetSnapshot();
                    if (snapshot == null)
                    {
                        throw new InvalidDataException("Savegame baseline unavailable: " + modId);
                    }
                    // Player-owned and local values are outside the savegame host contract.
                    foreach (PropertyInfo property in participant.PersonalProperties)
                    {
                        try
                        {
                            object value = property.GetValue(participant.Endpoint);
                            if (value != null)
                                snapshot[property.Name] = MessagePackSerializer.Serialize(property.PropertyType, value);
                        }
                        catch (Exception error)
                        {
                            MarkRestoreFailed();
                            NativeApiLog.Error(log, "Could not preserve local setting " +
                                modId + "." + property.Name + ": " + error.Message);
                            throw new InvalidDataException("Cannot prepare a save without preserving personal setting " + modId + "." + property.Name, error);
                        }
                    }
                    if (values != null && values.TryGetValue(modId, out Dictionary<string, byte[]> stored))
                        OverlaySavedHostProperties(snapshot, participant.Properties, stored, modId);
                    bool strictTrail = record != null && !record.TrailCustomizeAllowed &&
                        (record.Kind == (int)GameModeKind.CustomTrail || record.Kind == (int)GameModeKind.CoopTrail);
                    if (strictTrail || locked)
                    {
                        Dictionary<string, byte[]> baseline = participant.Endpoint.System_CreateDisabledMissionPresetSnapshot();
                        foreach (PropertyInfo property in participant.Properties)
                            if (baseline.TryGetValue(property.Name, out byte[] disabled))
                                snapshot[property.Name] = disabled;
                    }
                    if (!locked && record?.CreatorRules != null &&
                        record.CreatorRules.TryGetValue(modId, out var rules))
                    {
                        ApplyCreatorRules(snapshot, participant.Properties, rules,
                            current != null && current.TryGetValue(modId, out var currentMod) ? currentMod : null,
                            strictTrail);
                    }
            return snapshot;
        }

        /// <summary>Validates required startup values before the original load callback runs.</summary>
        public static bool PrepareLoadWithRestartCheck(string savePath, bool useCurrentSettings)
            => PrepareLoadCore(savePath, useCurrentSettings, false);

        private static bool PrepareLoadCore(string savePath, bool useCurrentSettings, bool allParticipants)
        {
            PrepareLoad(savePath, useCurrentSettings);
            if (!GameNetworkAPI.IsLocalHost()) return ModSettingsApplication.PrepareLaunch();
            string path = Normalize(savePath);
            string fingerprint;
            using (var file = File.OpenRead(path))
            using (var sha = System.Security.Cryptography.SHA256.Create())
                fingerprint = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
            ModSettingsApplication.EnterContext("save:" + path + "|" + fingerprint);
            preparedApplicationContext = ModSettingsApplication.ContextId;
            if (ReadRecord(path, out var record) == RecordReadState.Invalid)
                throw new InvalidDataException("The selected savegame ModSettings archive is invalid.");
            if (record?.LockedByConflict == true)
                throw new InvalidDataException("The savegame has conflicting or incomplete settings; startup configuration cannot be verified.");
            var values = legacyChoice || useChosenCurrent && IsCurrentChoiceAllowed(record) ? chosenCurrent : record?.Mods;
            bool locked = record?.LockedByConflict == true;
            if (locked) values = null;
            var participants = FindParticipants();
            foreach (var item in participants)
            {
                var participant = item.Value;
                // Keep the public restart probe's established backend-only behavior.
                // Ordinary local-save settings enter after retirement of the old
                // mission; the multiplayer host gate explicitly prepares everyone.
                if (!allParticipants && (!(participant.Endpoint is PresetLobbyModSettingsViewModel model) || !model.System_HasApplicationBackend)) continue;
                var snapshot = ModSettingsApplication.ResumeSnapshot(item.Key) ??
                    ResolveStartupSnapshot(item.Key, participant, record, values, chosenCurrent, locked);
                EnterPreparedPreset(item.Key, participant.Endpoint, snapshot);
            }
            preparedPath = path;
            preparedRecord = record;
            preparedApplied = allParticipants || participants.Count == 0;
            return ModSettingsApplication.PrepareLaunch();
        }

        /// <summary>Discards an unconsumed load-dialog choice after cancellation or failure.</summary>
        public static void CancelLoadChoice()
        {
            if (!SavegameLoadCoordinator.CancelSelection()) return;
            ReleaseUnstartedPreparation();
            lock (Sync)
            {
                chosenPath = null;
                chosenCurrent = null;
                useChosenCurrent = false;
                legacyChoice = false;
            }
        }

        internal static void BeginLoad(string filePath, bool multiplayer)
        {
            SavegameLoadCoordinator.ValidateStart(filePath, multiplayer);
            Dictionary<string, Dictionary<string, byte[]>> fallback = null;
            lock (Sync) { if (chosenCurrent == null) fallback = CaptureCompatibleHostSettings(); }
            lock (Sync)
            {
                // Multiplayer saves use OnLoadMap, whose Extender LoadContext is
                // incorrectly marked as a map. The confirmed entry owns this read.
                if (multiplayer) loaded = PreparedRecordForStart;
                else
                {
                    RecordReadState read = ReadRecord(filePath, out var record);
                    if (read == RecordReadState.Invalid) throw new InvalidDataException("Invalid savegame ModSettings archive.");
                    loaded = record;
                    legacyChoice = read == RecordReadState.Missing;
                }
                restoreFailed = false;
                if (chosenCurrent == null) chosenCurrent = fallback;
                activeCreatorRules = loaded?.CreatorRules;
                activeTrailCustomizeAllowed = loaded?.TrailCustomizeAllowed == true;
            }
        }

        internal static bool TryGetRestoredMode(out GameModeKind kind,
            out GameModeLaunchVariant variant, out bool lockedByConflict)
        {
            lock (Sync)
            {
                kind = loaded == null ? GameModeKind.Unknown : (GameModeKind)loaded.Kind;
                variant = loaded == null ? GameModeLaunchVariant.Standard : (GameModeLaunchVariant)loaded.Variant;
                lockedByConflict = loaded == null ? !legacyChoice || restoreFailed :
                    loaded.LockedByConflict || restoreFailed;
                return loaded != null;
            }
        }

        internal static void MarkRestoreFailed()
        {
            lock (Sync) restoreFailed = true;
        }

        internal static void ApplyLoadedSettings(MissionContext context)
        {
            if (context == null || !context.IsSave) return;
            if (!preparedApplied) throw new InvalidOperationException("Savegame reached completion without settings preparation.");
            string applicationContext = ModSettingsApplication.ContextId;
            ActivePreset.TrackSession(context.SessionId,
                applicationContext.StartsWith("save:", StringComparison.Ordinal) ? applicationContext : null);
            foreach (var entry in preparedEndpoints)
                ActivePreset.Track(context.SessionId, entry.Key, entry.Value);
            preparedTransferred = true;
        }

        internal static void FinishLoad()
        {
            if (!preparedTransferred) ReleaseUnstartedPreparation();
            else ClearPreparation();
            SavegameLoadCoordinator.FinishLoad();
            lock (Sync)
            {
                loaded = null;
                chosenCurrent = null;
                chosenPath = null;
                useChosenCurrent = false;
                legacyChoice = false;
                restoreFailed = false;
            }
        }

        private static void OnMissionEnded(MissionLifecycleNotification notification)
        {
            if (notification?.Context == null) return;
            if (notification.ReplacementContext?.RestartedFromSessionId == notification.Context.SessionId)
            {
                ActivePreset.Transfer(notification.Context.SessionId, notification.ReplacementContext.SessionId);
                return;
            }
            ActivePreset.ReleaseOnEnd(notification.Context.SessionId,
                (modId, error) => NativeApiLog.Error(log,
                    "Could not leave savegame settings for " + modId + ": " + error),
                // Reloading the same save reuses its content identity. The new
                // preparation, not the retiring session, already owns that context.
                releaseApplicationContext: notification.ReplacementContext?.IsSave != true || preparedApplicationContext == null);
        }

        private static byte[] Save(SaveContext context)
        {
            if (context == null || !context.IsSaveFile || context.IsMapEditorSave) return null;
            MissionContext mission = MissionLifecycleService.ActiveContext;
            if (mission == null || (mission.Mode.IsRealMultiplayer && mission.IsHost != true)) return null;
            var record = new SavegameModSettingsRecord
            {
                Version = SchemaVersion,
                Kind = (int)mission.Mode.Kind,
                Variant = (int)mission.Mode.LaunchVariant,
                LockedByConflict = mission.Mode.HasConflictingCustomizedOrigin,
                Mods = CaptureCompatibleHostSettings(),
                CreatorRules = CaptureCreatorRules(mission),
                TrailCustomizeAllowed = CaptureTrailCustomizeAllowed(mission),
            };
            return SerializeWithinLimit(record);
        }

        private static Dictionary<string, Dictionary<string, TrailCreatorRule>> CaptureCreatorRules(MissionContext mission)
        {
            if (mission.Mode.Kind != GameModeKind.CustomTrail && mission.Mode.Kind != GameModeKind.CoopTrail)
                return new Dictionary<string, Dictionary<string, TrailCreatorRule>>(StringComparer.Ordinal);
            try
            {
                Dictionary<string, Dictionary<string, TrailCreatorRule>> rules;
                lock (Sync)
                {
                    rules = mission.IsSave || ActivePreset.Contains(mission.SessionId) ? activeCreatorRules : creatorRulesProvider?.Invoke();
                }
                return rules ?? new Dictionary<string, Dictionary<string, TrailCreatorRule>>(StringComparer.Ordinal);
            }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "Could not capture Trail creator rules: " + error);
                return new Dictionary<string, Dictionary<string, TrailCreatorRule>>(StringComparer.Ordinal);
            }
        }

        private static bool CaptureTrailCustomizeAllowed(MissionContext mission)
        {
            if (mission.Mode.Kind != GameModeKind.CustomTrail && mission.Mode.Kind != GameModeKind.CoopTrail)
                return false;
            try
            {
                lock (Sync) return mission.IsSave || ActivePreset.Contains(mission.SessionId) ? activeTrailCustomizeAllowed : trailCustomizeProvider?.Invoke() == true;
            }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "Could not capture Trail Customize origin: " + error.Message);
                return false;
            }
        }

        internal static void ApplyCreatorRules(Dictionary<string, byte[]> snapshot,
            IEnumerable<PropertyInfo> properties, Dictionary<string, TrailCreatorRule> rules,
            Dictionary<string, byte[]> current, bool strictTrail)
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
                catch (Exception error)
                {
                    NativeApiLog.Error(log, "Invalid Trail creator rule " + property.Name + ": " + error.Message);
                }
            }
        }

        private static void Load(byte[] bytes, LoadContext context)
        {
            if (context == null || !context.IsSaveFile || !TryDeserialize(bytes, out SavegameModSettingsRecord record)) return;
            lock (Sync)
            {
                if (chosenPath != null &&
                    !string.Equals(chosenPath, Normalize(context.FilePath), StringComparison.OrdinalIgnoreCase))
                    return;
                loaded = record;
            }
        }

        private enum RecordReadState { Invalid, Missing, Valid }

        private static bool TryReadRecord(string path, out SavegameModSettingsRecord record) =>
            ReadRecord(path, out record) == RecordReadState.Valid;

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
                NativeApiLog.Error(log, "Could not inspect savegame ModSettings: " + error.Message);
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

        private static Dictionary<string, Dictionary<string, byte[]>> CaptureCompatibleHostSettings()
        {
            var result = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
            Dictionary<string, CompatibleMod> participants;
            try { participants = FindParticipants(); }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "Could not enumerate savegame settings participants: " + error);
                return result;
            }
            foreach (KeyValuePair<string, CompatibleMod> participant in participants
                .OrderBy(item => item.Key, StringComparer.Ordinal).Take(MaximumMods))
            {
                if (participant.Key.Length > 256) continue;
                result[participant.Key] = CaptureHostProperties(participant.Value.Endpoint,
                    participant.Value.Properties.Take(MaximumPropertiesPerMod), participant.Key);
            }
            return result;
        }

        internal static Dictionary<string, byte[]> CaptureHostProperties(
            object endpoint, IEnumerable<PropertyInfo> hostProperties, string modId)
        {
            var properties = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (PropertyInfo property in hostProperties)
            {
                if (property.Name.Length > 256) continue;
                try
                {
                    byte[] bytes = MessagePackSerializer.Serialize(
                        property.PropertyType, property.GetValue(endpoint));
                    if (bytes != null && bytes.Length <= MaximumPropertyBytes)
                        properties[property.Name] = bytes;
                    else
                        NativeApiLog.Error(log, "Savegame setting exceeds the property limit: " +
                            modId + "." + property.Name);
                }
                catch (Exception error)
                {
                    NativeApiLog.Error(log, "Could not capture savegame setting " +
                        modId + "." + property.Name + ": " + error.Message);
                }
            }
            return properties;
        }

        internal static void OverlaySavedHostProperties(Dictionary<string, byte[]> snapshot,
            IEnumerable<PropertyInfo> hostProperties, Dictionary<string, byte[]> stored, string modId)
        {
            if (snapshot == null || stored == null) return;
            foreach (PropertyInfo property in hostProperties)
            {
                if (!stored.TryGetValue(property.Name, out byte[] bytes) || bytes == null) continue;
                try
                {
                    object value = MessagePackSerializer.Deserialize(property.PropertyType, bytes);
                    if (value != null) snapshot[property.Name] = (byte[])bytes.Clone();
                }
                catch (Exception error)
                {
                    NativeApiLog.Error(log, "Invalid saved setting " + modId + "." +
                        property.Name + ": " + error.Message);
                }
            }
        }

        internal static byte[] SerializeWithinLimit(SavegameModSettingsRecord record)
        {
            byte[] bytes = MessagePackSerializer.Serialize(record);
            if (bytes.Length <= MaximumBytes) return bytes;
            record.LockedByConflict = true; // A reduced payload must never admit an unverifiable startup configuration.
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

        private sealed class CompatibleMod
        {
            internal IModSettingsPresetEndpoint Endpoint;
            internal PropertyInfo[] Properties;
            internal PropertyInfo[] PersonalProperties;
        }

        private static Dictionary<string, CompatibleMod> FindParticipants()
        {
            var result = new Dictionary<string, CompatibleMod>(StringComparer.Ordinal);
            foreach (IGrouping<string, LobbyModSettingsEntry> group in GameXAMLManagerAPI.Instance.RegisteredModSettings
                .Where(entry => entry?.Plugin?.Info?.Metadata?.GUID != null)
                .GroupBy(entry => entry.Plugin.Info.Metadata.GUID, StringComparer.Ordinal))
            {
                if (group.Count() != 1 || group.Any(entry => IsOptedOut(entry.Plugin))) continue;
                LobbyModSettingsEntry registration = group.First();
                if (!(registration.ViewModel is IModSettingsPresetEndpoint endpoint)) continue;
                PropertyInfo[] properties = registration.ViewModel.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.CanRead && property.CanWrite &&
                        property.GetCustomAttributes(false).Any(attribute => attribute.GetType().Name == "SyncHostOnlyAttribute") &&
                        !property.GetCustomAttributes(false).Any(attribute => attribute.GetType().Name == "DoNotPersistAttribute"))
                    .OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
                PropertyInfo[] personalProperties = registration.ViewModel.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.CanRead && property.CanWrite &&
                        !property.GetCustomAttributes(false).Any(attribute => attribute.GetType().Name == "DoNotPersistAttribute") &&
                        property.GetCustomAttributes(false).Any(attribute =>
                            attribute.GetType().Name == "SyncPerPlayerAttribute" ||
                            attribute.GetType().Name == "PresetLocalAttribute"))
                    .ToArray();
                if (properties.Length == 0 || properties.Any(property =>
                    property.Name == "EnableMod" && property.PropertyType != typeof(bool)) ||
                    properties.GroupBy(property => property.Name, StringComparer.Ordinal).Any(items => items.Skip(1).Any()))
                    continue;
                try
                {
                    Dictionary<string, byte[]> disabled = endpoint.System_CreateDisabledMissionPresetSnapshot();
                    if (disabled == null) continue;
                    bool valid = true;
                    foreach (PropertyInfo property in properties)
                    {
                        if (!disabled.TryGetValue(property.Name, out byte[] value) || value == null)
                        {
                            valid = false;
                            break;
                        }
                        object disabledValue = MessagePackSerializer.Deserialize(property.PropertyType, value);
                        if (property.Name == "EnableMod" && disabledValue is bool enabled && enabled)
                        {
                            valid = false;
                            break;
                        }
                    }
                    if (!valid) continue;
                    result[group.Key] = new CompatibleMod
                    {
                        Endpoint = endpoint,
                        Properties = properties,
                        PersonalProperties = personalProperties,
                    };
                }
                catch (Exception error)
                {
                    NativeApiLog.Error(log, "Incompatible savegame settings for " + group.Key + ": " + error.Message);
                }
            }
            foreach (var registered in ModSettingsApplication.Endpoints)
            {
                if (!registered.Value.System_HasDynamicSettings) continue;
                if (result.ContainsKey(registered.Key)) throw new InvalidOperationException("Duplicate settings provider: " + registered.Key);
                result[registered.Key] = new CompatibleMod
                {
                    Endpoint = registered.Value,
                    Properties = ModSettingsApplication.GetHostProperties(registered.Value),
                    PersonalProperties = Array.Empty<PropertyInfo>()
                };
            }
            return result;
        }

        private static bool IsOptedOut(object plugin)
        {
            return SavegameSettingsParticipation.IsExcluded(plugin?.GetType());
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try { return Path.GetFullPath(path ?? string.Empty); }
            catch { return string.Empty; }
        }
    }

}
