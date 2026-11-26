using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using APIShared.Multiplayer;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Health
{
    internal static unsafe partial class HealthRuntime
    {
        // Audited usable native slots. Rows are sparse, but every slot can carry both a
        // record and pending work (for example after an early save before a simulation tick).
        private const int RepairUnitSlots = 9999, RepairBuildingSlots = 3999;
        private const int RepairSlotCount = RepairUnitSlots + RepairBuildingSlots;
        private const int PendingRowBytes = 13, ProxyRowBytes = 6;
        private const int RepairPrefixBytes = sizeof(byte) + sizeof(uint) + sizeof(int);
        private const int RepairCountBytes = 3 * sizeof(ushort);
        private const int RepairHeaderBytes = RepairPrefixBytes + HealthSaveState.HeaderBytes + RepairCountBytes;
        private const int MaximumRepairBytes = RepairHeaderBytes +
            RepairSlotCount * (HealthSaveState.RecordBytes + 1 + PendingRowBytes) + RepairBuildingSlots * ProxyRowBytes;
        private const byte RepairTracking = 1, RepairPrepared = 2, RepairScanned = 4, RepairMalformed = 8;
        private static bool resyncEnabled;

        internal static bool TryEnableMultiplayerResync(out string reason)
        {
            reason = string.Empty;
            if (resyncEnabled) return true;
            if (!IsAvailable) { reason = UnavailableReason; return false; }
            resyncEnabled = MultiplayerResyncData.TryRegister(APISharedPlugin.PluginGuid, "health-ledger", 1,
                MaximumRepairBytes, CaptureRepair, PrepareRepair, CommitRepair, out reason);
            return resyncEnabled;
        }

        private static byte[] CaptureRepair()
        {
            lock (Sync)
            {
                Ledger state = Volatile.Read(ref ledger);
                if (!state.Prepared) return new[] { state.IsTracking ? RepairTracking : (byte)0 };

                EnsureRepairCapacity();
                var archive = new HealthSaveState { FullBasis = state.FullBasis, LegacyLords = state.LegacyLords };
                foreach (HealthRecord record in state.Records.Values)
                    if (MatchesRepairIdentity(record.Kind, record.Id, record.GlobalId, record.Type))
                        archive.Records.Add(record);
                archive.Records.Sort((a,b) => a.Key.CompareTo(b.Key));
                byte[] archiveBytes = archive.Encode();

                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write((byte)(RepairTracking | RepairPrepared | (state.Scanned ? RepairScanned : 0) |
                        (state.Malformed ? RepairMalformed : 0)));
                    writer.Write(state.Generation);
                    writer.Write(archiveBytes.Length);
                    writer.Write(archiveBytes);
                    foreach (HealthRecord record in archive.Records)
                        writer.Write(state.IsApplied(record.Key, record.GlobalId));
                    WritePending(writer, state.Units, HealthObjectKind.Unit);
                    WritePending(writer, state.Buildings, HealthObjectKind.Building);
                    WriteProxies(writer, state);
                    return stream.ToArray();
                }
            }
        }

        private static Ledger PrepareRepair(ReadOnlyMemory<byte> bytes)
        {
            if (bytes.IsEmpty || bytes.Length > MaximumRepairBytes)
                throw new InvalidDataException("Invalid HP repair length.");
            if (bytes.Length == 1)
            {
                Ledger local = Volatile.Read(ref ledger);
                if (local.Prepared || bytes.Span[0] != (local.IsTracking ? RepairTracking : 0))
                    throw new InvalidDataException("Peers disagree on whether an HP ledger is active.");
                // The original archive, if any, is still buffered in this phase.
                return local;
            }
            if (mission == null || bytes.Length < RepairHeaderBytes)
                throw new InvalidDataException("HP repair requires an active mission and a complete header.");

            EnsureRepairCapacity();
            using (var stream = new MemoryStream(bytes.ToArray(), writable: false))
            using (var reader = new BinaryReader(stream))
            {
                int flags = reader.ReadByte();
                if ((flags & 3) != (RepairTracking | RepairPrepared) || flags > 15 ||
                    (flags & (RepairScanned | RepairMalformed)) == (RepairScanned | RepairMalformed))
                    throw new InvalidDataException("Invalid HP repair phase.");

                var state = new Ledger { Tracking = true, Prepared = true, Scanned = (flags & RepairScanned) != 0,
                    Malformed = (flags & RepairMalformed) != 0, Generation = reader.ReadUInt32() };
                int archiveLength = reader.ReadInt32();
                if (archiveLength < HealthSaveState.HeaderBytes || archiveLength > bytes.Length - RepairPrefixBytes - RepairCountBytes ||
                    archiveLength > HealthSaveState.HeaderBytes + RepairSlotCount * HealthSaveState.RecordBytes)
                    throw new InvalidDataException("Invalid HP repair archive length.");
                HealthSaveState archive = HealthSaveState.Decode(reader.ReadBytes(archiveLength));
                state.FullBasis = archive.FullBasis;
                state.LegacyLords = archive.LegacyLords;

                long previousKey = 0;
                foreach (HealthRecord record in archive.Records)
                {
                    byte applied = reader.ReadByte();
                    if (record.Key <= previousKey || record.Id > RepairSlots(record.Kind) ||
                        record.Generation > state.Generation || applied > 1 || state.Malformed)
                        throw new InvalidDataException("Invalid HP repair basis row.");
                    previousKey = record.Key;
                    if (!MatchesRepairIdentity(record.Kind, record.Id, record.GlobalId, record.Type)) continue;
                    state.Records.Add(record.Key, record);
                    if (applied == 1) state.Applied.Add(record.Key, record.GlobalId);
                }
                ReadPending(reader, state, state.Units, HealthObjectKind.Unit);
                ReadPending(reader, state, state.Buildings, HealthObjectKind.Building);
                ReadProxies(reader, state);
                if (stream.Position != stream.Length)
                    throw new InvalidDataException("Trailing HP repair bytes.");
                return state;
            }
        }

        private static void CommitRepair(Ledger state) => Volatile.Write(ref ledger, state);

        private static void WritePending(BinaryWriter writer, Dictionary<int,Pending> queue, HealthObjectKind kind)
        {
            var rows = new List<Pending>();
            foreach (Pending pending in queue.Values)
                if (MatchesRepairIdentity(kind, pending.Id, pending.Global, pending.Type))
                    rows.Add(pending);
            // Processing order allocates generation numbers to initial-scan rows.
            writer.Write((ushort)rows.Count);
            foreach (Pending pending in rows)
            {
                writer.Write((ushort)pending.Id);
                writer.Write(pending.Global);
                writer.Write((ushort)pending.Type);
                writer.Write((byte)pending.Reason);
                writer.Write(pending.Generation);
            }
        }

        private static void ReadPending(BinaryReader reader, Ledger state, Dictionary<int,Pending> queue, HealthObjectKind kind)
        {
            int count = reader.ReadUInt16();
            if (count > RepairSlots(kind) || state.Malformed && count != 0)
                throw new InvalidDataException("Invalid HP repair pending count.");
            var seen = new HashSet<int>();
            HealthInitializationReason nativeReason = kind == HealthObjectKind.Unit ?
                HealthInitializationReason.TypeInitialized : HealthInitializationReason.Created;
            for (int i = 0; i < count; i++)
            {
                var pending = new Pending { Id = reader.ReadUInt16(), Global = reader.ReadUInt32(),
                    Type = reader.ReadUInt16(), Reason = (HealthInitializationReason)reader.ReadByte(),
                    Generation = reader.ReadUInt32() };
                if (pending.Id <= 0 || pending.Id > RepairSlots(kind) || !seen.Add(pending.Id) || pending.Global == 0 ||
                    pending.Generation > state.Generation || pending.Reason !=
                    (pending.Generation == 0 ? HealthInitializationReason.MissionStart : nativeReason))
                    throw new InvalidDataException("Invalid HP repair pending identity.");
                if (MatchesRepairIdentity(kind, pending.Id, pending.Global, pending.Type)) queue.Add(pending.Id, pending);
            }
        }

        private static void WriteProxies(BinaryWriter writer, Ledger state)
        {
            var ids = new List<int>();
            foreach (KeyValuePair<long,uint> entry in state.Applied)
            {
                int id = (int)entry.Key;
                if ((HealthObjectKind)(entry.Key >> 32) == HealthObjectKind.Building &&
                    (!state.Records.TryGetValue(entry.Key, out var record) || record.GlobalId != entry.Value ||
                        record.Type != (int)eStructs.STRUCT_SIEGE_TOWER) &&
                    MatchesRepairIdentity(HealthObjectKind.Building, id, entry.Value, (int)eStructs.STRUCT_SIEGE_TOWER))
                    ids.Add(id);
            }
            ids.Sort();
            writer.Write((ushort)ids.Count);
            foreach (int id in ids)
            {
                writer.Write((ushort)id);
                writer.Write(state.Applied[HealthRecord.KeyFor(HealthObjectKind.Building, id)]);
            }
        }

        private static void ReadProxies(BinaryReader reader, Ledger state)
        {
            int count = reader.ReadUInt16();
            if (count > RepairBuildingSlots || state.Malformed && count != 0)
                throw new InvalidDataException("Invalid HP repair proxy count.");
            int previousId = 0;
            for (int i = 0; i < count; i++)
            {
                int id = reader.ReadUInt16();
                uint globalId = reader.ReadUInt32();
                if (id <= previousId || id > RepairBuildingSlots || globalId == 0)
                    throw new InvalidDataException("Invalid HP repair siege proxy identity.");
                previousId = id;
                if (MatchesRepairIdentity(HealthObjectKind.Building, id, globalId, (int)eStructs.STRUCT_SIEGE_TOWER))
                    state.Applied.Add(HealthRecord.KeyFor(HealthObjectKind.Building, id), globalId);
            }
        }

        private static int RepairSlots(HealthObjectKind kind) => kind == HealthObjectKind.Unit ? RepairUnitSlots : RepairBuildingSlots;

        private static void EnsureRepairCapacity()
        {
            if (GameUnitManagerAPI.Instance.GetUnitsAsSpan().Length > RepairUnitSlots ||
                GameBuildingManagerAPI.Instance.GetBuildingsAsSpan().Length > RepairBuildingSlots)
                throw new InvalidDataException("Native HP pools exceed the audited repair capacity.");
        }

        private static bool MatchesRepairIdentity(HealthObjectKind kind, int id, uint globalId, int type)
        {
            if (kind == HealthObjectKind.Unit)
                return UnitAccess.TryGetById(id, out GameUnit* unit, out _) && unit->r_GlobalId == globalId &&
                    (int)unit->r_UnitChimp == type && (UnitAccess.IsReallyAlive(unit) || unit->r_AliveState == AliveState.NeedsInit);
            return TryBuilding(id, out GameBuilding* building) && building->r_GlobalId == globalId &&
                (int)building->r_BuildingType == type &&
                (building->r_AliveState == AliveState.IsAlive || building->r_AliveState == AliveState.NeedsInit);
        }
    }
}
