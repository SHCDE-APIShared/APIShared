using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using MessagePack;

namespace APIShared.ModSettings
{
    /// <summary>The value-resolution and editability contexts of a mission settings application.</summary>
    public enum MissionModSettingsApplicationKind
    {
        /// <summary>Disabled mission baseline, personal Player values and fixed overrides; read-only.</summary>
        Playable,
        /// <summary>Like Playable, but participants absent from the document retain their personal context.</summary>
        Preview,
        /// <summary>Like Playable, with editable mission settings.</summary>
        Editable,
        /// <summary>Current working baseline, fixed overrides and backend-owned Player values; editable.</summary>
        Authoring,
        /// <summary>Like Authoring, but read-only for a test launch.</summary>
        AuthoringTest,
        /// <summary>Current working values without resolving document values again; read-only.</summary>
        Materialized,
    }

    /// <summary>Owns one consumer's temporary mission settings and their cleanup, without a provider registry or hooks.</summary>
    /// <remarks>Retain this object for the lifetime of its context. Call on the Unity thread after registration,
    /// without concurrent endpoint mutation. The caller owns authorization, source documents, restart context identity
    /// and lifecycle subscriptions. Apply returning is preparation, not proof of a successful mission start.
    /// Do not share endpoints between independently active coordinators. Nested Apply/Exit calls are rejected.</remarks>
    public sealed class MissionModSettingsSession
    {
        private readonly Dictionary<string, IModSettingsPresetEndpoint> active =
            new Dictionary<string, IModSettingsPresetEndpoint>(StringComparer.Ordinal);
        private bool changing;
        private bool hasPreparedContext;

        /// <summary>Snapshot of owned GUIDs, including endpoints whose cleanup failed and must be retried.</summary>
        public IReadOnlyList<string> ActiveModGuids => Array.AsReadOnly(active.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());

        /// <summary>Actual lifecycle session confirmed by the caller, or null while only prepared or after cleanup.</summary>
        public long? MissionSessionId { get; private set; }

        /// <summary>Marks a prepared context as adopted by an actual successful lifecycle start. Never call this
        /// from a dialog return or early initialization. A positive ID is required; replacement/restart may bind a new ID.</summary>
        public void ConfirmStarted(long sessionId)
        {
            if (sessionId <= 0) throw new ArgumentOutOfRangeException(nameof(sessionId));
            if (changing) throw new InvalidOperationException("Mission settings are changing.");
            if (!hasPreparedContext) throw new InvalidOperationException("No prepared mission settings context exists.");
            MissionSessionId = sessionId;
        }

        /// <summary>Replaces the temporary context. Requires a schema-valid document and compatible registered endpoints.
        /// GUID identity is ordinal regardless of caller dictionary comparers. Missing mentioned providers are rejected
        /// before leaving the previous context. Captures authoring values before
        /// exit; resolves personal Player values after exit. Prunes unknown settings in the supplied document and returns
        /// their names. Optional starting snapshots apply to Authoring/AuthoringTest/Materialized and are copied.
        /// Restart snapshots retain precedence except for Materialized. beforeApply runs once after all value preparation,
        /// before any new endpoint entry; a failure blocks application. Only Authoring/Editable are editable.
        /// On failure after exit the previous mission context is not re-entered: all attempted entries are cleaned up
        /// toward personal state. Cleanup attempts every endpoint, aggregates failures and retains failed owners for retry.
        /// Incomplete cleanup blocks another Apply until Exit succeeds.
        /// Endpoints must support their existing Exit operation after a partially failed Enter.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Apply(MissionModSettingsDocument document,
            IReadOnlyDictionary<string, IModSettingsPresetEndpoint> participants,
            MissionModSettingsApplicationKind kind, string label = "Mission",
            IReadOnlyDictionary<string, Dictionary<string, byte[]>> startingSnapshots = null,
            Action<MissionModSettingsDocument> beforeApply = null)
        {
            if (document?.Mods == null) throw new ArgumentNullException(nameof(document));
            if (participants == null) throw new ArgumentNullException(nameof(participants));
            if (!Enum.IsDefined(typeof(MissionModSettingsApplicationKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (changing) throw new InvalidOperationException("Mission settings are changing.");
            if (!hasPreparedContext && active.Count != 0)
                throw new InvalidOperationException("Retry incomplete mission cleanup before applying another context.");
            var endpoints = participants.ToArray();
            if (endpoints.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value == null))
                throw new ArgumentException("Participants require a GUID and endpoint.", nameof(participants));
            if (endpoints.Where((item, index) => endpoints.Take(index).Any(previous => ReferenceEquals(previous.Value, item.Value))).Any())
                throw new ArgumentException("One endpoint cannot represent multiple participants.", nameof(participants));
            // Caller comparers must not make a missing GUID appear registered or apply another GUID's rules.
            var endpointIds = new HashSet<string>(endpoints.Select(item => item.Key), StringComparer.Ordinal);
            var entries = document.Mods.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            foreach (string id in entries.Keys)
                if (!endpointIds.Contains(id))
                    throw new InvalidDataException("Required Map/Trail settings provider is missing or incompatible: " + id);
            bool current = kind == MissionModSettingsApplicationKind.Authoring ||
                kind == MissionModSettingsApplicationKind.AuthoringTest || kind == MissionModSettingsApplicationKind.Materialized;
            bool materialized = kind == MissionModSettingsApplicationKind.Materialized;
            bool editable = kind == MissionModSettingsApplicationKind.Authoring || kind == MissionModSettingsApplicationKind.Editable;
            var startingById = current ? startingSnapshots?.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal) : null;
            changing = true;
            try
            {
                // All non-mutating work that can be performed before exit stays outside the replacement boundary.
                if (!materialized)
                    foreach (var item in endpoints)
                        if (entries.TryGetValue(item.Key, out var entry))
                            MissionModSettingsPreparation.ValidateEntry(item.Value, entry);
                var captured = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
                if (current)
                    foreach (var item in endpoints)
                        captured[item.Key] = item.Value is IModSettingsWorkingCopyEndpoint working
                            ? MissionModSettingsPreparation.CopySnapshot(working.System_CreateCurrentWorkingSnapshot())
                            : MissionModSettingsPreparation.CaptureCurrentSnapshot(item.Value);
                ExitCore();
                try
                {
                    var prepared = new List<Tuple<string, IModSettingsPresetEndpoint, Dictionary<string, byte[]>, bool>>();
                    var removed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                    foreach (var item in endpoints)
                    {
                        bool backend = item.Value is PresetLobbyModSettingsViewModel model && model.System_HasApplicationBackend;
                        if (backend) ((PresetLobbyModSettingsViewModel)item.Value).System_RefreshOwnConfiguration();
                        var properties = ModSettingsApplication.GetHostProperties(item.Value).ToDictionary(property => property.Name, StringComparer.Ordinal);
                        MissionModSettingsEntry entry = null;
                        var removedNames = Array.Empty<string>();
                        if (entries.ContainsKey(item.Key))
                        {
                            removedNames = MissionModSettingsJson.RemoveUnknownSettings(document, item.Key, properties.Keys);
                            document.Mods.TryGetValue(item.Key, out entry);
                        }
                        removed[item.Key] = Array.AsReadOnly(removedNames);
                        if (kind == MissionModSettingsApplicationKind.Preview && entry == null) continue;
                        Dictionary<string, byte[]> snapshot;
                        if (current)
                        {
                            if (startingById == null || !startingById.TryGetValue(item.Key, out snapshot)) snapshot = captured[item.Key];
                            snapshot = MissionModSettingsPreparation.CopySnapshot(snapshot);
                        }
                        else snapshot = MissionModSettingsPreparation.CopySnapshot(item.Value.System_CreateDisabledMissionPresetSnapshot());
                        if (entry != null && !materialized)
                        {
                            if (!current || backend)
                            {
                                // Resolve Player from owned values on a local/host model.
                                // A real client instead retains its authenticated live host state.
                                var own = item.Value is PresetLobbyModSettingsViewModel playerModel &&
                                    playerModel.CanRestorePersonalHostSettings
                                    ? playerModel.System_CreatePlayerMissionPresetSnapshot() : null;
                                foreach (string name in entry.PlayerSettings ?? Array.Empty<string>())
                                    if (properties.TryGetValue(name, out PropertyInfo property))
                                        snapshot[name] = own != null && own.TryGetValue(name, out byte[] playerBytes)
                                            ? (byte[])playerBytes.Clone()
                                            : MessagePackSerializer.Serialize(property.PropertyType, property.GetValue(item.Value));
                            }
                            if (entry.Overrides != null)
                                foreach (var setting in entry.Overrides)
                                    if (properties.TryGetValue(setting.Key, out PropertyInfo property))
                                        snapshot[setting.Key] = MessagePackSerializer.Serialize(property.PropertyType,
                                            ModSettingsPresetJson.ConvertValue(setting.Value, property.PropertyType));
                        }
                        if (!materialized)
                        {
                            var resumed = ModSettingsApplication.ResumeSnapshot(item.Key);
                            if (resumed != null) snapshot = MissionModSettingsPreparation.CopySnapshot(resumed);
                        }
                        prepared.Add(Tuple.Create(item.Key, item.Value, snapshot, entry != null));
                    }
                    beforeApply?.Invoke(document);
                    foreach (var item in prepared)
                    {
                        // Root ownership before any mutation, including an Enter which changes state and then throws.
                        active.Add(item.Item1, item.Item2);
                        if (item.Item2 is IModSettingsMissionSourceEndpoint source) source.System_SetExplicitMissionSettings(item.Item4);
                        item.Item2.System_EnterMissionPreset(item.Item3, label, editable);
                        ModSettingsApplication.RestorePreparedSources(item.Item1);
                    }
                    hasPreparedContext = true;
                    return new ReadOnlyDictionary<string, IReadOnlyList<string>>(removed);
                }
                catch (Exception failure)
                {
                    try { ExitCore(); }
                    catch (Exception cleanup) { throw new AggregateException("Mission settings application and cleanup failed.", failure, cleanup); }
                    throw;
                }
            }
            finally { changing = false; }
        }

        /// <summary>Restores personal contexts using the exact endpoints that were entered, even if discovery changed.
        /// Every endpoint is attempted. Successful owners are removed; failed owners remain for a later retry.
        /// Multiple failures are aggregated. Does not exit the global restart/application context or uninstall anything.</summary>
        public void Exit()
        {
            if (changing) throw new InvalidOperationException("Mission settings are changing.");
            changing = true;
            try { ExitCore(); }
            finally { changing = false; }
        }

        private void ExitCore()
        {
            MissionSessionId = null;
            hasPreparedContext = false;
            var failures = new List<Exception>();
            foreach (var item in active.ToArray())
            {
                try
                {
                    item.Value.System_ExitMissionPreset();
                    if (item.Value is IModSettingsMissionSourceEndpoint source) source.System_SetExplicitMissionSettings(false);
                    active.Remove(item.Key);
                }
                catch (Exception error) { failures.Add(new InvalidOperationException("Could not leave mission settings for " + item.Key, error)); }
            }
            if (failures.Count != 0) throw new AggregateException("Mission settings cleanup failed.", failures);
        }
    }
}
