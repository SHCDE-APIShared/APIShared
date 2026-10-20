using APIShared.GameModes;
using APIShared.ModSettings;
using MessagePack;
using SHCDESE.API;
using SHCDESE.API.Components.ModManager;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace APIShared
{
    public static partial class SavegameModSettings
    {
        private static readonly Dictionary<string, IModSettingsPresetEndpoint> preparedEndpoints =
            new Dictionary<string, IModSettingsPresetEndpoint>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<string, byte[]>> preparedSnapshots =
            new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
        private static string preparedPath;
        private static string preparedApplicationContext;
        private static SavegameModSettingsRecord preparedRecord;
        private static bool preparedApplied, preparedTransferred;
        internal static SavegameModSettingsRecord PreparedRecordForStart => preparedRecord;

        private static void EnterPreparedPreset(string id, IModSettingsPresetEndpoint endpoint, Dictionary<string, byte[]> snapshot)
        {
            // Re-entering would replace suspended preset attribution with the
            // temporary source. Keep one entry per participant and transaction.
            if (preparedEndpoints.Values.Contains(endpoint) && endpoint.IsMissionPresetActive) return;
            endpoint.System_ExitMissionPreset();
            // Register before setters run so a partially failed entry is rolled back.
            preparedEndpoints[id] = endpoint;
            preparedSnapshots[id] = snapshot.ToDictionary(x => x.Key, x => x.Value == null ? null : (byte[])x.Value.Clone(), StringComparer.Ordinal);
            endpoint.System_EnterMissionPreset(snapshot, "Savegame", false);
            if (!endpoint.IsMissionPresetActive) throw new InvalidOperationException("Savegame preset did not become active.");
            foreach (var property in ModSettingsApplication.GetHostProperties(endpoint))
            {
                if (!snapshot.TryGetValue(property.Name, out var bytes)) throw new InvalidDataException("Missing prepared host value: " + id + "." + property.Name);
                var expected = MessagePackSerializer.Deserialize(property.PropertyType, bytes);
                var expectedBytes = MessagePackSerializer.Serialize(property.PropertyType, expected);
                var actualBytes = MessagePackSerializer.Serialize(property.PropertyType, property.GetValue(endpoint));
                if (!expectedBytes.SequenceEqual(actualBytes)) throw new InvalidDataException("Host setter did not apply the prepared value: " + id + "." + property.Name);
            }
            preparedApplied = true;
        }

        internal static void ApplyBeforeLoad(bool multiplayer)
        {
            if (preparedApplied)
            {
                // Beginning a new attempt retires the previous mission lease. If
                // that lease used this same model, it can have restored its own
                // preset; reapply the already agreed snapshot, not a new source.
                foreach (var entry in preparedEndpoints.ToArray())
                    EnterPreparedPreset(entry.Key, entry.Value, preparedSnapshots[entry.Key]);
                foreach (var id in preparedEndpoints.Keys) ModSettingsApplication.RestorePreparedSources(id);
                return;
            }
            if (multiplayer) throw new InvalidOperationException("Multiplayer save settings have not been prepared.");
            var participants = FindParticipants();
            var values = legacyChoice || useChosenCurrent && IsCurrentChoiceAllowed(loaded) ? chosenCurrent : loaded?.Mods;
            bool locked = loaded?.LockedByConflict == true;
            if (locked) values = null;
            // This is local save restoration, not a client edit or network receive.
            // The installed internal scope is signature-checked; callback errors
            // are rethrown because the Extender otherwise logs and swallows them.
            RunLocalRestore(() =>
            {
                foreach (var entry in participants)
                    EnterPreparedPreset(entry.Key, entry.Value.Endpoint, ResolveStartupSnapshot(entry.Key,
                        entry.Value, loaded, values, chosenCurrent, locked));
            });
            preparedPath = chosenPath;
            preparedRecord = loaded;
            preparedApplied = true;
            activeCreatorRules = loaded?.CreatorRules;
            activeTrailCustomizeAllowed = loaded?.TrailCustomizeAllowed == true;
            if (!ModSettingsApplication.PrepareLaunch())
                throw new InvalidOperationException("Savegame configuration requires restart or network preparation.");
            foreach (var id in preparedEndpoints.Keys) ModSettingsApplication.RestorePreparedSources(id);
        }

        internal static SavegameModSettingsRecord PrepareHostSave(string path)
        {
            if (!string.Equals(preparedPath, Normalize(path), StringComparison.OrdinalIgnoreCase) || !preparedApplied)
            {
                if (!string.Equals(chosenPath, Normalize(path), StringComparison.OrdinalIgnoreCase)) PrepareLoad(path, true);
                if (!PrepareLoadCore(path, useChosenCurrent, true))
                    throw new InvalidOperationException("Savegame configuration is not ready; restart or backend synchronization is required.");
            }
            if (!ModSettingsApplication.PrepareLaunch()) throw new InvalidOperationException("Savegame backend is not ready.");
            // Values use the existing Extender/backend transports. Only provenance
            // is carried by the readiness protocol, never a second settings copy.
            return preparedRecord == null ? null : new SavegameModSettingsRecord
            {
                Version = preparedRecord.Version, Kind = preparedRecord.Kind, Variant = preparedRecord.Variant,
                LockedByConflict = preparedRecord.LockedByConflict,
                TrailCustomizeAllowed = preparedRecord.TrailCustomizeAllowed,
                Mods = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal),
                CreatorRules = new Dictionary<string, Dictionary<string, TrailCreatorRule>>(StringComparer.Ordinal)
            };
        }

        internal static void PublishPreparedHostValues()
        {
            foreach (var participant in FindParticipants())
            {
                if (!(participant.Value.Endpoint is SHCDESE.ViewModels.LobbyModSettingsBaseViewModel model)) continue;
                if (participant.Value.Endpoint is PresetLobbyModSettingsViewModel dynamicModel && dynamicModel.System_HasDynamicSettings) continue;
                foreach (var property in participant.Value.Properties) model.System_TriggerUpdate(property.Name);
            }
        }

        internal static string CaptureHostFingerprint()
        {
            var values = CaptureCompatibleHostSettings();
            var participants = FindParticipants();
            if (values.Count != participants.Count || participants.Any(p =>
                !values.TryGetValue(p.Key, out var properties) || properties.Count != p.Value.Properties.Length))
                throw new InvalidDataException("Incomplete host settings snapshot.");
            return SavegameLoadAgreement.Fingerprint(values);
        }

        internal static void AcceptPreparedClient(string path, SavegameModSettingsRecord record)
        {
            if (preparedApplied) return;
            foreach (var entry in FindParticipants())
            {
                // The fingerprint was checked after authenticated host sync.
                // Capture all host values plus this client's own personal values.
                var snapshot = ResolveStartupSnapshot(entry.Key, entry.Value, null,
                    CaptureCompatibleHostSettings(), null, false);
                EnterPreparedPreset(entry.Key, entry.Value.Endpoint, snapshot);
            }
            preparedPath = Normalize(path);
            preparedRecord = record;
            preparedApplied = true;
            loaded = record;
            legacyChoice = record == null;
            activeCreatorRules = record?.CreatorRules;
            activeTrailCustomizeAllowed = record?.TrailCustomizeAllowed == true;
        }

        internal static void ReleaseUnstartedPreparation()
        {
            if (preparedTransferred) return;
            foreach (var entry in preparedEndpoints.ToArray())
            {
                try { entry.Value.System_ExitMissionPreset(); }
                catch (Exception error) { NativeApiLog.Error(log, "Could not restore own preset for " + entry.Key + ": " + error); }
            }
            if (preparedEndpoints.Count != 0 || preparedApplicationContext != null &&
                ModSettingsApplication.ContextId == preparedApplicationContext)
                ModSettingsApplication.ExitContext();
            ClearPreparation();
        }

        private static void ClearPreparation()
        {
            preparedEndpoints.Clear(); preparedPath = null; preparedRecord = null;
            preparedApplicationContext = null;
            preparedSnapshots.Clear();
            preparedApplied = false; preparedTransferred = false;
        }

        private static void RunLocalRestore(Action action)
        {
            var method = typeof(GameXAMLManagerAPI).GetMethod("RunWithChangeOrigin", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(LobbyModSettingsChangeOrigin), typeof(Action) }, null);
            if (method == null || method.ReturnType != typeof(void)) throw new MissingMethodException("Extender restore-origin scope is unavailable.");
            ExceptionDispatchInfo failure = null;
            Action guarded = () => { try { action(); } catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); } };
            method.Invoke(GameXAMLManagerAPI.Instance, new object[] { LobbyModSettingsChangeOrigin.InitialLoad, guarded });
            failure?.Throw();
        }
    }
}
