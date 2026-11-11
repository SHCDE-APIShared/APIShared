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
        // chosenCurrent/chosenPath: every writer and every reader outside Sync runs on the Unity thread.
        private static Dictionary<string, Dictionary<string, byte[]>> chosenCurrent;
        private static bool useChosenCurrent;
        private static bool legacyChoice;
        private static Dictionary<string, Dictionary<string, TrailCreatorRule>> activeCreatorRules;
        private static Func<Dictionary<string, Dictionary<string, TrailCreatorRule>>> creatorRulesProvider;
        private static Func<bool> trailCustomizeProvider;
        private static bool activeTrailCustomizeAllowed;
        private static string chosenPath;
        private static bool initialized;
        private static readonly SavegameMissionPresetLease ActivePreset = new SavegameMissionPresetLease();
        private static IDisposable missionEndSubscription;

        internal static void Initialize(ManualLogSource logger)
        {
            lock (Sync)
            {
                if (initialized) return;
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
                        out GameModeLaunchVariant variant);
                    return new SavedModeEvidence(present, kind, variant);
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
            if (record == null) return false;
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

        /// <summary>Inspects the existing lobby registrations and dynamic configuration endpoints,
        /// returning read-only metadata for accepted and rejected participants, ordered by GUID.</summary>
        /// <remarks>Call on the Unity thread after LibraryLoaded and settings registration. No caching,
        /// provider registration, replay, persistence or settings application occurs. The existing contract
        /// calls each otherwise eligible lobby endpoint's disabled-snapshot factory once; factories must
        /// not apply settings. Metadata/snapshot failures reject only that participant. Discovery source
        /// failures propagate. Conflicting accepted providers are reported here; automatic operations and
        /// GetCompatibleParticipants continue to throw for that conflict. Dynamic endpoints retain their
        /// existing explicit registration contract, independent of an optional lobby panel.</remarks>
        public static IReadOnlyList<ModSettingsParticipantInfo> GetParticipantDiagnostics() =>
            GetParticipantDiagnostics(ModSettingsParticipantPurpose.AutomaticSavegame);

        /// <summary>Inspects participants for automatic savegames or Map/Trail authoring using
        /// the same caller-thread, snapshot-factory and read-only metadata contract as the default query.</summary>
        /// <remarks>MissionAuthoring does not apply savegame-only exclusions. For those excluded
        /// endpoints not represented by an explicit dynamic provider, it runs the
        /// authoring current-value serialization probes before checking the disabled snapshot.
        /// Consumer-specific authoring opt-outs remain with consumers.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">The purpose is not supported.</exception>
        public static IReadOnlyList<ModSettingsParticipantInfo> GetParticipantDiagnostics(ModSettingsParticipantPurpose purpose)
        {
            if (purpose != ModSettingsParticipantPurpose.AutomaticSavegame && purpose != ModSettingsParticipantPurpose.MissionAuthoring)
                throw new ArgumentOutOfRangeException(nameof(purpose));
            return Array.AsReadOnly(InspectParticipants(purpose).OrderBy(item => item.ModGuid, StringComparer.Ordinal).ToArray());
        }

        /// <summary>Records the confirmed host choice for exactly one selected save.</summary>
        public static void PrepareLoad(string savePath, bool useCurrentSettings)
        {
            ReleaseUnstartedPreparation();
            string path = Normalize(savePath);
            SavegameLoadChoiceState state = GetLoadChoiceState(path);
            // An unreadable record loads like a legacy save (see ReadRecordForLoad).
            bool legacy = state == SavegameLoadChoiceState.Legacy || state == SavegameLoadChoiceState.Invalid;
            Dictionary<string, Dictionary<string, byte[]>> current = CaptureCompatibleHostSettings();
            lock (Sync)
            {
                chosenPath = path.Length == 0 ? null : path;
                chosenCurrent = current;
                useChosenCurrent = SavegameLoadDecision.UseCurrent(state, useCurrentSettings);
                legacyChoice = legacy;
            }
            NativeApiLog.Info(log, "Savegame settings source selected: file=" + path + ",recordState=" + state +
                ",requestedCurrent=" + useCurrentSettings + ",effectiveSource=" +
                (legacy || SavegameLoadDecision.UseCurrent(state, useCurrentSettings) ? "current-host-settings" : "saved-host-settings") +
                ",choice=" + (legacy ? "forced-no-usable-record" : "host-choice-subject-to-author-rules") +
                "; existing Trail author rules and current personal/client settings remain applicable.");
        }

        private static Dictionary<string, byte[]> ResolveStartupSnapshot(string modId, CompatibleMod participant,
            SavegameModSettingsRecord record, Dictionary<string, Dictionary<string, byte[]>> values,
            Dictionary<string, Dictionary<string, byte[]>> current)
        {
                    Dictionary<string, byte[]> snapshot =
                        participant.Endpoint.System_CreateDisabledMissionPresetSnapshot();
                    if (snapshot == null)
                    {
                        throw new InvalidDataException("Savegame baseline unavailable: " + modId);
                    }
                    // Personal values are never captured into a save-owned mission snapshot.
                    // Stored values and author rules are raw data of the mod version that wrote them:
                    // move entries under declared former names before they are matched by property name.
                    Type settingsType = participant.Endpoint.GetType();
                    if (values != null && values.TryGetValue(modId, out Dictionary<string, byte[]> stored))
                        OverlaySavedHostProperties(snapshot, participant.Properties,
                            ModSettingsMigration.ApplyRenames(settingsType, stored), modId);
                    bool strictTrail = record != null && !record.TrailCustomizeAllowed &&
                        (record.Kind == (int)GameModeKind.CustomTrail || record.Kind == (int)GameModeKind.CoopTrail);
                    if (strictTrail)
                    {
                        Dictionary<string, byte[]> baseline = participant.Endpoint.System_CreateDisabledMissionPresetSnapshot();
                        foreach (PropertyInfo property in participant.Properties)
                            if (baseline.TryGetValue(property.Name, out byte[] disabled))
                                snapshot[property.Name] = disabled;
                    }
                    if (record?.CreatorRules != null &&
                        record.CreatorRules.TryGetValue(modId, out var rules))
                    {
                        ApplyCreatorRules(snapshot, participant.Properties,
                            ModSettingsMigration.ApplyRenames(settingsType, rules),
                            current != null && current.TryGetValue(modId, out var currentMod) ? currentMod : null,
                            strictTrail);
                    }
                    // A change of meaning cannot be declared as a rename. The owner converts such values
                    // before its setters run, so the later verification compares against what those
                    // setters are meant to keep.
                    if (participant.Endpoint is IModSettingsSnapshotMigration migration)
                        snapshot = migration.System_MigrateMissionSnapshot(snapshot) ??
                            throw new InvalidDataException("Savegame snapshot migration returned nothing: " + modId);
            return snapshot;
        }

        /// <summary>Validates required startup values before the original load callback runs.</summary>
        public static bool PrepareLoadWithRestartCheck(string savePath, bool useCurrentSettings)
            => PrepareLoadCore(savePath, useCurrentSettings, false);

        // Absence of a usable dialog choice means current settings, but PrepareLoad still
        // enforces SavedOnly and creator rules. Explicit public Boolean calls retain their contract.
        internal static bool PrepareAutomaticLoadWithRestartCheck(string savePath, bool? choice)
            => PrepareLoadCore(savePath, choice ?? true, false);

        private static bool PrepareLoadCore(string savePath, bool useCurrentSettings, bool allParticipants)
        {
            PrepareLoad(savePath, useCurrentSettings);
            if (!GameNetworkAPI.IsLocalHost()) return ModSettingsApplication.PrepareLaunch();
            string path = Normalize(savePath);
            string fingerprint;
            using (var file = File.OpenRead(path))
                fingerprint = Sha256Hex.Of(file);
            ModSettingsApplication.EnterContext("save:" + path + "|" + fingerprint);
            preparedApplicationContext = ModSettingsApplication.ContextId;
            var record = ReadRecordForLoad(path, out _);
            var values = legacyChoice || useChosenCurrent && IsCurrentChoiceAllowed(record) ? chosenCurrent : record?.Mods;
            var participants = FindParticipants();
            foreach (var item in participants)
            {
                var participant = item.Value;
                // Keep the public restart probe's established backend-only behavior.
                // Ordinary local-save settings enter after retirement of the old
                // mission; the multiplayer host gate explicitly prepares everyone.
                if (!allParticipants && (!(participant.Endpoint is PresetLobbyModSettingsViewModel model) || !model.System_HasApplicationBackend)) continue;
                var snapshot = ModSettingsApplication.ResumeSnapshot(item.Key) ??
                    ResolveStartupSnapshot(item.Key, participant, record, values, chosenCurrent);
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
            if (!multiplayer && !string.Equals(chosenPath, Normalize(filePath), StringComparison.OrdinalIgnoreCase))
                PrepareLoad(filePath, true);
            Dictionary<string, Dictionary<string, byte[]>> fallback = null;
            lock (Sync) { if (chosenCurrent == null) fallback = CaptureCompatibleHostSettings(); }
            lock (Sync)
            {
                // Multiplayer saves use OnLoadMap. The confirmed preparation owns
                // this read, including an authoritative absence of metadata.
                if (multiplayer) loaded = PreparedRecordForStart;
                else
                {
                    loaded = ReadRecordForLoad(filePath, out bool legacy);
                    legacyChoice = legacy;
                }
                if (chosenCurrent == null) chosenCurrent = fallback;
                activeCreatorRules = loaded?.CreatorRules;
                activeTrailCustomizeAllowed = loaded?.TrailCustomizeAllowed == true;
            }
        }

        // A record's legacy LockedByConflict flag and a missing record are no evidence about the mode.
        internal static bool TryGetRestoredMode(out GameModeKind kind, out GameModeLaunchVariant variant)
        {
            lock (Sync)
            {
                kind = loaded == null ? GameModeKind.Unknown : (GameModeKind)loaded.Kind;
                variant = loaded == null ? GameModeLaunchVariant.Standard : (GameModeLaunchVariant)loaded.Variant;
                return loaded != null;
            }
        }

        // A failed restore changes neither the mission mode nor any mod permission: the mode still
        // comes from the save's record. What it leaves undecided are the settings themselves, so the
        // consequence is stated once, in full, instead of locking mods on this peer only.
        internal static void ReportRestoreFailure(Exception error)
        {
            try
            {
                NativeApiLog.Error(log, "SAVEGAME_SETTINGS_RESTORE_FAILED: the saved mod settings of this mission could " +
                    "not be applied after loading. The mission mode and every mod permission stay as recorded in the " +
                    "save; mods keep the settings that were active when the failure occurred, which can differ from " +
                    "the saved ones. In multiplayer, players whose restore succeeded use the saved settings, so " +
                    "gameplay settings can differ between players and desynchronize the game. Reload the save to retry. " +
                    "Cause: " + error);
            }
            catch { /* Called from the lifecycle's cleanup path; diagnostics must not replace its outcome. */ }
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
                // Earlier builds persisted a launch-evidence conflict here, which locked the save for
                // every later load and re-save. Every capture now decides its mode, so Kind and Variant
                // above are the complete record; the flag is only set for a size-reduced payload.
                LockedByConflict = false,
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
                // Checked, no deadlock: the only provider (ExtendedData) reads synchronously and takes
                // no lock that waits on Sync; Monitor locks are reentrant.
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
            => MissionModSettingsCreatorRules.Apply(snapshot, properties, rules, current, strictTrail,
                (name, error) => NativeApiLog.Error(log, "Invalid Trail creator rule " + name + ": " + error.Message));

        private static void Load(byte[] bytes, LoadContext context)
        {
            if (context == null || !context.IsSaveFile || !TryDeserialize(bytes, out SavegameModSettingsRecord record)) return;
            lock (Sync)
            {
                if (chosenPath != null &&
                    !string.Equals(chosenPath, Normalize(context.FilePath), StringComparison.OrdinalIgnoreCase))
                    return;
                // Host provenance (including a missing record) remains authoritative
                // through both archive callbacks and a delayed client start. Local
                // Coop uses this same confirmed load phase without real networking.
                if (SavegameLoadCoordinator.IsLoadingMultiplayerSave) return;
                loaded = record;
            }
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try { return Path.GetFullPath(path ?? string.Empty); }
            catch { return string.Empty; }
        }
    }

}
