using System;
using System.Collections.Generic;
using System.Threading;
using APIShared.Networking;
using SHCDESE.Interop;

namespace APIShared.Buildings
{
    /// <summary>Who may change one value of a <see cref="BuildingStateTable"/>.</summary>
    public enum BuildingStateValueAccess
    {
        /// <summary>The building's owner, through <see cref="BuildingStateTable.RequestChange"/>: a
        /// tick-synchronous authenticated Chore that every peer validates and applies in the same tick.</summary>
        OwnerRequest,
        /// <summary>Only the registering mod's deterministic simulation code: managed
        /// <see cref="BuildingStateTable.TrySetFromSimulation"/> or the mod's own native code through the
        /// documented table layout. Players cannot request it.</summary>
        Simulation
    }

    /// <summary>Declaration of one small integer value stored per building.</summary>
    public sealed class BuildingStateValue
    {
        /// <summary>Declares a value from 0 through <paramref name="maximum"/>; every entry starts at 0.</summary>
        /// <param name="name">Owner-local diagnostic name; nonempty.</param>
        /// <param name="maximum">Largest valid value, 1 through 255. Requests, simulation writes and
        /// saved records above it are rejected.</param>
        /// <param name="access">Who may change the value.</param>
        public BuildingStateValue(string name, byte maximum, BuildingStateValueAccess access)
        { Name = name; Maximum = maximum; Access = access; }
        /// <summary>Owner-local diagnostic name.</summary>
        public string Name { get; }
        /// <summary>Largest valid value.</summary>
        public byte Maximum { get; }
        /// <summary>Who may change the value.</summary>
        public BuildingStateValueAccess Access { get; }
    }

    /// <summary>Describes one owner-bound per-building value table for <see cref="BuildingStates.TryRegister"/>.</summary>
    /// <remarks>The instance is read once during registration; later changes have no effect, except that
    /// <see cref="AcceptsOwnerRequests"/> is retained and evaluated on every request.</remarks>
    public sealed class BuildingStateRegistration
    {
        /// <summary>Creates a registration.</summary>
        /// <param name="ownerGuid">The registering plugin's GUID; at most 128 characters.</param>
        /// <param name="registrationId">Stable owner-local table ID; at most 128 characters. Peers match
        /// requests by owner GUID and this ID, never by registration order.</param>
        /// <param name="saveDataIdentifier">Identifier of the table's <see cref="Savegames.VersionedSaveData"/>
        /// record: unique in the process and file-name safe. The owner GUID is the record owner.</param>
        /// <param name="buildingTypes">Building types whose owner may request changes; at least one.
        /// Simulation writes and reads are not restricted by type.</param>
        /// <param name="values">One to 12 value declarations. Their order is the value index, the save
        /// format and the native layout; append new values only at the end.</param>
        public BuildingStateRegistration(string ownerGuid, string registrationId, string saveDataIdentifier,
            IEnumerable<eStructs> buildingTypes, IEnumerable<BuildingStateValue> values)
        {
            OwnerGuid = ownerGuid;
            RegistrationId = registrationId;
            SaveDataIdentifier = saveDataIdentifier;
            BuildingTypes = buildingTypes == null ? null : new List<eStructs>(buildingTypes).AsReadOnly();
            Values = values == null ? null : new List<BuildingStateValue>(values).AsReadOnly();
        }

        /// <summary>The registering plugin's GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable owner-local table ID.</summary>
        public string RegistrationId { get; }
        /// <summary>Versioned save record identifier.</summary>
        public string SaveDataIdentifier { get; }
        /// <summary>Building types whose owner may request changes.</summary>
        public IReadOnlyList<eStructs> BuildingTypes { get; }
        /// <summary>Value declarations in index order.</summary>
        public IReadOnlyList<BuildingStateValue> Values { get; }
        /// <summary>Optional logical activation of owner requests. Checked before submission on Unity and
        /// again in the executing Chore on every peer, where its result must be identical (for example a
        /// host-synchronized setting). Null accepts requests. Exceptions count as false.</summary>
        public Func<bool> AcceptsOwnerRequests { get; set; }
    }

    /// <summary>A registered per-building value table: small values per building identity, changed by the
    /// owning player through a tick-synchronous Chore or by the owner mod's simulation code, saved with the
    /// game and readable from the simulation, from Unity and from native code.</summary>
    /// <remarks>
    /// <para>Identity: an entry belongs to one one-based building ID together with its GlobalId. Every read
    /// for another identity returns 0, and the first write for a new identity clears all earlier values, so
    /// a destroyed building's entry is inert and a new building on a reused slot starts with zeros. Entries
    /// are not removed when a building dies; they stay inert until the slot is written for a new identity.</para>
    /// <para>Lifetime: process-long. Mission starts without an archive load clear the table; every archive
    /// load replaces it with the saved record or clears it (no record, out of scope, damaged).</para>
    /// <para>Native layout, fixed for the process: <see cref="Capacity"/> entries of <see cref="EntrySize"/>
    /// bytes (8 or 16) at <see cref="NativeAddress"/>, indexed by the building ID. Each entry holds the
    /// GlobalId as a little-endian uint32 at <see cref="GlobalIdOffset"/> and one byte per value at
    /// <see cref="ValuesOffset"/> plus the value index. Native code must compare the entry's GlobalId with
    /// the live building first, may read values from the simulation thread and may write only
    /// <see cref="BuildingStateValueAccess.Simulation"/> values of a matching entry, deterministically.</para>
    /// </remarks>
    public sealed class BuildingStateTable
    {
        private readonly HashSet<eStructs> types;
        private readonly BuildingStateValue[] values;
        private int lastSerial, pendingSerial;

        internal BuildingStateTable(BuildingStateRegistration registration, BuildingStateMemory memory)
        {
            OwnerGuid = registration.OwnerGuid;
            RegistrationId = registration.RegistrationId;
            AcceptsOwnerRequests = registration.AcceptsOwnerRequests;
            types = new HashSet<eStructs>(registration.BuildingTypes);
            values = new BuildingStateValue[registration.Values.Count];
            for (int i = 0; i < values.Length; i++) values[i] = registration.Values[i];
            Memory = memory;
        }

        internal BuildingStateMemory Memory { get; }
        internal Func<bool> AcceptsOwnerRequests { get; }
        internal bool LoadedSinceStart;

        /// <summary>The registering plugin's GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Owner-local table ID.</summary>
        public string RegistrationId { get; }
        /// <summary>Number of values per entry.</summary>
        public int ValueCount => values.Length;
        /// <summary>Number of entries; valid building IDs are 1 through Capacity - 1. Larger IDs are not stored.</summary>
        public int Capacity => BuildingStateMemory.Capacity;
        /// <summary>Bytes per entry: 8 for up to 4 values, otherwise 16.</summary>
        public int EntrySize => Memory.EntrySize;
        /// <summary>Offset of the entry's uint32 GlobalId.</summary>
        public int GlobalIdOffset => BuildingStateMemory.GlobalIdOffset;
        /// <summary>Offset of value 0; value i is at ValuesOffset + i.</summary>
        public int ValuesOffset => BuildingStateMemory.ValuesOffset;
        /// <summary>Process-lifetime address of entry 0; never freed or moved.</summary>
        public ulong NativeAddress => Memory.Address;

        /// <summary>Whether the owner of a building of this type may request changes.</summary>
        public bool AppliesTo(eStructs buildingType) => types.Contains(buildingType);

        /// <summary>Reads one value; 0 for another building identity, an ID outside the table or an invalid index.</summary>
        /// <remarks>Any thread. The simulation reads the state of its current tick; other threads may see a
        /// value from just before or after a tick. No logging, no allocation.</remarks>
        public byte GetValue(int buildingId, uint globalId, int valueIndex) => Memory.Get(buildingId, globalId, valueIndex);

        /// <summary>Writes a <see cref="BuildingStateValueAccess.Simulation"/> value for one building identity.</summary>
        /// <remarks>Only from the owner mod's deterministic simulation code, so every peer writes the same value
        /// in the same tick. False for an owner-request value, a value above its maximum, a zero GlobalId or an
        /// ID outside the table. The building itself is not looked up.</remarks>
        public bool TrySetFromSimulation(int buildingId, uint globalId, int valueIndex, byte value) =>
            (uint)valueIndex < (uint)values.Length && values[valueIndex].Access == BuildingStateValueAccess.Simulation &&
            Memory.Set(buildingId, globalId, valueIndex, value);

        /// <summary>Asks every peer to set an <see cref="BuildingStateValueAccess.OwnerRequest"/> value of a
        /// building owned by the local player. Requires the initialized Unity thread.</summary>
        /// <remarks>Nothing changes locally on submission: the Chore reaches every peer including this one, and
        /// each applies it in the same tick only if the request is still valid there: activation, value index and
        /// maximum, a living building with this ID and GlobalId, one of the registered types, and the Chore's
        /// verified sender owns it. Rejections are silent. Requests apply in submission order. A submitted result
        /// only means that the Chore was queued; see <see cref="HasPendingRequest"/>.</remarks>
        public NetworkSendResult RequestChange(int buildingId, uint globalId, int valueIndex, byte value)
        {
            if (!Internal.UnityMainThreadDispatch.IsMainThread)
                return ChoreSendPolicy.Result(NetworkSendStatus.Unavailable, "Building state requests require the initialized Unity thread.");
            if (!IsOwnerValue(valueIndex, value) || !BuildingStateMemory.IsValidId(buildingId) || globalId == 0)
                return ChoreSendPolicy.Result(NetworkSendStatus.Invalid, "Invalid building state request.");
            if (!Accepts()) return ChoreSendPolicy.Result(NetworkSendStatus.Disabled, "Building state requests are disabled.");
            int serial = lastSerial == int.MaxValue ? 1 : lastSerial + 1;
            lastSerial = serial;
            // Marked before queueing: the Chore can execute on the simulation thread before Send returns.
            // A failed submission stops waiting altogether, so nothing can stay pending for good.
            Volatile.Write(ref pendingSerial, serial);
            NetworkSendResult result = BuildingStates.Submit(new BuildingStatePacket(OwnerGuid, RegistrationId,
                buildingId, globalId, valueIndex, value, serial));
            if (!result.Submitted) Interlocked.CompareExchange(ref pendingSerial, 0, serial);
            return result;
        }

        /// <summary>Whether this peer's latest submitted request has neither been applied nor rejected yet.</summary>
        /// <remarks>Presentation only, for example to keep a slider under the player's hand until the Chore
        /// arrives. Cleared when that Chore executes (applied or rejected), at mission starts and by
        /// <see cref="ForgetPendingRequest"/>. Any thread.</remarks>
        public bool HasPendingRequest => Volatile.Read(ref pendingSerial) != 0;

        /// <summary>Stops waiting for this peer's latest request, for example when its panel closes; a request
        /// that is never executed must not hold the next presentation back. Does not cancel the request.</summary>
        public void ForgetPendingRequest() => Volatile.Write(ref pendingSerial, 0);

        internal bool IsOwnerValue(int valueIndex, byte value) =>
            (uint)valueIndex < (uint)values.Length && values[valueIndex].Access == BuildingStateValueAccess.OwnerRequest &&
            value <= values[valueIndex].Maximum;

        internal bool Accepts()
        {
            try { return AcceptsOwnerRequests == null || AcceptsOwnerRequests(); }
            catch (Exception error) { BuildingStates.Report(this, "activation predicate failed", error); return false; }
        }

        internal void Acknowledge(int serial) => Interlocked.CompareExchange(ref pendingSerial, 0, serial);
    }
}
