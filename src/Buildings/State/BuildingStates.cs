using System;
using System.Collections.Generic;
using APIShared.Internal;
using APIShared.Networking;
using APIShared.Savegames;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Buildings
{
    /// <summary>Owner-bound per-building value tables that are synchronized, saved and native-readable.</summary>
    /// <remarks>
    /// <para>Register each table once at plugin startup on the initialized Unity thread after Script Extender
    /// readiness (for example in <c>LibraryLoaded</c>), unconditionally and in the same order on every peer,
    /// like any Chore channel: the first registration in the process also registers APIShared's single
    /// building-state Chore. Registrations, their memory and their save records live for the process and
    /// cannot be removed; control a feature through <see cref="BuildingStateRegistration.AcceptsOwnerRequests"/>
    /// and the consumer's own logic instead.</para>
    /// <para>Each table owns one <see cref="VersionedSaveData"/> record (version 1, entry <c>values</c>,
    /// <see cref="VersionedSaveDataRegistration.SimulationRelevant"/>) under the consumer's GUID and identifier,
    /// so multiplayer saves are compared before loading. Its Loaded callback restores or clears the table on
    /// the load thread before the mission starts.</para>
    /// <para>No tick, frame or polling work: requests run in their Chore, reads are memory reads.</para>
    /// </remarks>
    public static class BuildingStates
    {
        internal const string SaveEntry = "values";
        private const int MaximumPayloadBytes = 1024;
        private static readonly object Gate = new object();
        private static BuildingStateTable[] tables = Array.Empty<BuildingStateTable>();
        private static AuthenticatedChoreChannel<BuildingStatePacket> channel;
        private static IMissionLifecycleCapability missions;
        private static readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Registers a table; false with a reason for invalid or duplicate registrations, a save
        /// identifier VersionedSaveData rejects, or a missing transport.</summary>
        public static bool TryRegister(BuildingStateRegistration registration, out BuildingStateTable table, out string reason)
        {
            table = null;
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Building state tables must be registered on the initialized Unity thread."; return false; }
            if (!TryValidate(registration, out byte[] maxima, out reason)) return false;
            lock (Gate)
            {
                foreach (BuildingStateTable existing in tables)
                    if (StringComparer.Ordinal.Equals(existing.OwnerGuid, registration.OwnerGuid) &&
                        StringComparer.Ordinal.Equals(existing.RegistrationId, registration.RegistrationId))
                    { reason = "Duplicate building state table: " + registration.OwnerGuid + "/" + registration.RegistrationId; return false; }
                if (!EnsureShared(out reason)) return false;

                var memory = new BuildingStateMemory(maxima);
                var created = new BuildingStateTable(registration, memory);
                var save = new VersionedSaveDataRegistration(registration.OwnerGuid, registration.SaveDataIdentifier, 1, 1,
                    new[] { SaveEntry }, writer => { writer.Write(SaveEntry, memory.Encode()); return true; },
                    load => OnLoaded(created, load))
                { SimulationRelevant = true };
                // The memory of a refused registration stays allocated; registration happens once per table.
                if (!VersionedSaveData.TryRegister(save, out _, out reason)) return false;

                var next = new BuildingStateTable[tables.Length + 1];
                Array.Copy(tables, next, tables.Length);
                next[tables.Length] = created;
                tables = next;
                table = created;
                reason = "";
                return true;
            }
        }

        internal static bool TryValidate(BuildingStateRegistration registration, out byte[] maxima, out string reason)
        {
            maxima = null;
            if (registration == null) { reason = "A registration is required."; return false; }
            if (!BuildingStatePacket.IsName(registration.OwnerGuid) || !BuildingStatePacket.IsName(registration.RegistrationId))
            { reason = "A nonempty owner GUID and table ID of at most 128 characters are required."; return false; }
            if (string.IsNullOrWhiteSpace(registration.SaveDataIdentifier))
            { reason = "A save data identifier is required."; return false; }
            if (registration.BuildingTypes == null || registration.BuildingTypes.Count == 0)
            { reason = "At least one building type is required."; return false; }
            var values = registration.Values;
            if (values == null || values.Count < 1 || values.Count > BuildingStateMemory.MaximumValueCount)
            { reason = "One to " + BuildingStateMemory.MaximumValueCount + " values are required."; return false; }
            maxima = new byte[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                BuildingStateValue value = values[i];
                if (value == null || string.IsNullOrWhiteSpace(value.Name) || value.Maximum == 0 ||
                    (value.Access != BuildingStateValueAccess.OwnerRequest && value.Access != BuildingStateValueAccess.Simulation))
                { maxima = null; reason = "Value " + i + " needs a name, a maximum of at least 1 and a defined access."; return false; }
                maxima[i] = value.Maximum;
            }
            reason = "";
            return true;
        }

        // Caller holds Gate. The Chore is registered once, at the first table, so processes without
        // tables keep their packet IDs; consumers register their tables unconditionally on every peer.
        private static bool EnsureShared(out string reason)
        {
            reason = "";
            if (channel != null) return true;
            if (missions == null)
            {
                if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out var lifecycle, out var diagnostic))
                { reason = "Building state tables need the mission lifecycle: " + diagnostic; return false; }
                if (!lifecycle.TryRegisterObserver("building-state/start", OnMissionStart, OnMissionEnd, OnMissionInitialization, out diagnostic))
                { reason = "Building state tables could not observe mission starts: " + diagnostic; return false; }
                missions = lifecycle;
            }
            try
            {
                channel = ApiShared.ForMod(APISharedPlugin.PluginGuid).Network.RegisterAuthenticatedChore<BuildingStatePacket>(
                    "building-state/request", Execute, new AuthenticatedChoreChannelOptions<BuildingStatePacket>
                    { MaximumPayloadBytes = MaximumPayloadBytes, ValidatePayload = packet => packet.IsValid });
                return true;
            }
            catch (Exception error) { reason = "Building state Chore registration failed: " + error.Message; return false; }
        }

        internal static NetworkSendResult Submit(BuildingStatePacket packet)
        {
            AuthenticatedChoreChannel<BuildingStatePacket> current = channel;
            return current == null
                ? ChoreSendPolicy.Result(NetworkSendStatus.Unavailable, "Building state Chore is not registered.")
                : current.Send(packet);
        }

        internal static BuildingStateTable Find(string owner, string id)
        {
            foreach (BuildingStateTable table in tables)
                if (StringComparer.Ordinal.Equals(table.OwnerGuid, owner) && StringComparer.Ordinal.Equals(table.RegistrationId, id))
                    return table;
            return null;
        }

        // Runs synchronously in the native simulation context on every peer, including the sender.
        private static unsafe void Execute(AuthenticatedChoreMessage<BuildingStatePacket> message)
        {
            BuildingStatePacket packet = message.Packet;
            BuildingStateTable table = Find(packet.Owner, packet.Id);
            if (table == null) return;
            try
            {
                if (!table.IsOwnerValue(packet.ValueIndex, packet.Value) || !table.Accepts()) return;
                var manager = GameBuildingManagerAPI.Instance;
                if (manager == null || !BuildingAccess.TryGetById(manager, packet.BuildingId, out GameBuilding* building, out _) ||
                    building == null || building->r_AliveState != AliveState.IsAlive || building->r_GlobalId != packet.GlobalId ||
                    !table.AppliesTo(building->r_BuildingType) || building->r_PlayerIdOwner != message.SenderPlayerId)
                    return;
                table.Memory.Set(packet.BuildingId, packet.GlobalId, packet.ValueIndex, packet.Value);
            }
            finally
            {
                // Every peer counts its own serials, so another player's request may carry the same number.
                if (message.SenderPlayerId == PlayerPerspectiveAPI.GetControlledPlayerId()) table.Acknowledge(packet.Serial);
            }
        }

        private static void OnLoaded(BuildingStateTable table, VersionedSaveLoad load)
        {
            table.LoadedSinceStart = true;
            if (load.Status != VersionedSaveLoadStatus.Restored || !load.Entries.TryGetValue(SaveEntry, out byte[] data))
            {
                table.Memory.Clear();
                return;
            }
            if (!table.Memory.TryRestore(data))
                Report(table, "found a damaged saved record; its buildings start with zeros", null);
        }

        // A load belongs to one attempt: a new attempt (BeforeLoad) or an ended one, including a load
        // that failed before its start, forgets it, so a later fresh mission clears the table.
        private static void OnMissionInitialization(MissionLifecycleNotification notification)
        {
            if (notification.Phase == MissionInitializationPhase.BeforeLoad) ForgetLoads();
        }

        private static void OnMissionEnd(MissionLifecycleNotification notification) => ForgetLoads();

        private static void ForgetLoads()
        {
            foreach (BuildingStateTable table in tables) table.LoadedSinceStart = false;
        }

        // A mission without an archive load must not inherit the table of an earlier one.
        private static void OnMissionStart(MissionLifecycleNotification notification)
        {
            if (notification.IsReplay) return;
            foreach (BuildingStateTable table in tables)
            {
                if (!table.LoadedSinceStart) table.Memory.Clear();
                table.LoadedSinceStart = false;
                table.ForgetPendingRequest();
            }
        }

        // One error per table and kind; consumers see their GUID and table ID in the line.
        internal static void Report(BuildingStateTable table, string what, Exception error)
        {
            string key = table.OwnerGuid + "/" + table.RegistrationId + "/" + what;
            lock (reported) if (!reported.Add(key)) return;
            NativeApiLog.Error(ApiSharedRuntime.ProcessInstance.Logger,
                "Building state table " + table.OwnerGuid + "/" + table.RegistrationId + " " + what + (error == null ? "." : ": " + error));
        }
    }
}
