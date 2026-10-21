using SHCDESE.API.Components.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace APIShared.ModSettings
{
    // Reference identity also covers the period before Steam assigns the lobby ID.
    internal readonly struct SettingsBroadcastLobby
    {
        internal readonly object Instance;
        internal readonly ulong? Id;
        internal SettingsBroadcastLobby(object instance, ulong? id)
        { Instance = instance; Id = id; }

        internal bool Matches(SettingsBroadcastLobby other) => Instance != null &&
            ReferenceEquals(Instance, other.Instance) && (!Id.HasValue || !other.Id.HasValue || Id == other.Id);
    }

    // Only the outbound boundary is delayed. Property setters, UI and personal storage
    // run normally; pending names are re-read by the original sender at publication.
    internal sealed class HostLobbySettingsPublisher
    {
        private readonly HashSet<string> properties;
        private readonly HashSet<string> pending = new HashSet<string>(StringComparer.Ordinal);
        private readonly Func<SettingsBroadcastLobby> captureLobby;
        private readonly Func<bool> isHost;
        private readonly Func<int, bool> transportReady;
        private readonly Action<string> publish;
        private SettingsBroadcastLobby pendingLobby, observedLobby;
        private int localPlayerId;
        private bool active, suspended;

        internal HostLobbySettingsPublisher(Type settingsType, Func<SettingsBroadcastLobby> captureLobby,
            Func<bool> isHost, Func<int, bool> transportReady, Action<string> publish)
        {
            properties = new HashSet<string>(settingsType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetCustomAttribute<SyncHostOnlyAttribute>() != null)
                .Select(property => property.Name), StringComparer.Ordinal);
            this.captureLobby = captureLobby;
            this.isHost = isHost;
            this.transportReady = transportReady;
            this.publish = publish;
        }

        internal bool HasSettings => properties.Count != 0;
        internal void Activate() { active = true; }
        internal void Deactivate() { active = false; Suspend(); }
        internal void Suspend()
        {
            suspended = true;
            observedLobby = default;
            ClearPending();
        }

        internal bool TryDefer(string propertyName, out bool defer)
        {
            defer = false;
            if (!properties.Contains(propertyName)) return false;
            defer = true;
            SettingsBroadcastLobby current = captureLobby();
            RetainCurrentLobby(current);
            if (!active || suspended || current.Instance == null || !isHost())
            {
                ClearPending();
                return true;
            }
            if (CanPublish(current))
            {
                pending.Remove(propertyName);
                if (pending.Count == 0) pendingLobby = default;
                defer = false;
                return true;
            }
            if (pending.Count == 0 || current.Id.HasValue) pendingLobby = current;
            pending.Add(propertyName);
            return true;
        }

        internal void InvalidateObservation()
        {
            observedLobby = default;
            RetainCurrentLobby(captureLobby());
        }

        internal void Observe(ulong? lobbyId, int playerId, bool localPlayerInRoster)
        {
            SettingsBroadcastLobby current = captureLobby();
            RetainCurrentLobby(current);
            suspended = false;
            observedLobby = lobbyId.HasValue && current.Id == lobbyId ? current : default;
            localPlayerId = localPlayerInRoster ? playerId : 0;
            if (!isHost()) { ClearPending(); return; }
            if (pending.Count == 0 || !CanPublish(current)) return;

            // Remove each name before notifying: reentrant changes remain independent.
            string[] names = pending.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            for (int index = 0; index < names.Length; index++)
            {
                SettingsBroadcastLobby latest = captureLobby();
                RetainCurrentLobby(latest);
                if (!isHost()) { ClearPending(); return; }
                if (!current.Matches(latest) || !CanPublish(latest)) return;
                if (!pending.Remove(names[index])) continue;
                try { publish(names[index]); }
                catch
                {
                    if (current.Matches(captureLobby()) && !suspended) pending.Add(names[index]);
                    throw;
                }
            }
            if (pending.Count == 0) pendingLobby = default;
        }

        private bool CanPublish(SettingsBroadcastLobby current) => active && !suspended &&
            current.Id.HasValue && current.Matches(observedLobby) &&
            localPlayerId > 0 && transportReady(localPlayerId);

        private void RetainCurrentLobby(SettingsBroadcastLobby current)
        {
            if (pending.Count == 0) { pendingLobby = default; return; }
            if (!pendingLobby.Matches(current)) ClearPending();
            else if (current.Id.HasValue) pendingLobby = current;
        }

        private void ClearPending() { pending.Clear(); pendingLobby = default; }
    }
}
