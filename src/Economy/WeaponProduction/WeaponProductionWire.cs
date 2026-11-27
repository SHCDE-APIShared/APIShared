using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal static unsafe partial class WeaponProductionRuntime
    {
        internal const int BuildingWireBytes = 16, WorkerWireBytes = 20;
        internal static readonly IntPtr BuildingWireAddress = AllocateWire(MaxBuildingId, BuildingWireBytes);
        internal static readonly IntPtr WorkerWireAddress = AllocateWire(MaxUnitId, WorkerWireBytes);
        internal static readonly IntPtr WorkerWireKindsAddress = AllocateWire(MaxUnitId, sizeof(ushort));
        internal static readonly IntPtr TransferDemandAddress = AllocateWire(0, 1);
        internal static bool HasTransferDemand => frozen.HasOptions;
        private const int OptionMask = 31, PhaseShift = 5, TickMask = 1;
        private enum BatchPhase : byte { Manufacturing, LocalProduct, Cargo }

        private static IntPtr AllocateWire(int maximumId, int stride)
        {
            int length = checked((maximumId + 1) * stride);
            IntPtr storage = Marshal.AllocHGlobal(length);
            new Span<byte>((void*)storage, length).Clear();
            return storage;
        }

        private static Span<byte> BuildingWire(int id) => new Span<byte>((void*)(BuildingWireAddress + id * BuildingWireBytes), BuildingWireBytes);
        private static Span<byte> WorkerWire(int id) => new Span<byte>((void*)(WorkerWireAddress + id * WorkerWireBytes), WorkerWireBytes);

        private static void ClearWire()
        {
            Marshal.WriteByte(TransferDemandAddress, 0);
            new Span<byte>((void*)BuildingWireAddress, (MaxBuildingId + 1) * BuildingWireBytes).Clear();
            new Span<byte>((void*)WorkerWireAddress, (MaxUnitId + 1) * WorkerWireBytes).Clear();
            new Span<ushort>((void*)WorkerWireKindsAddress, MaxUnitId + 1).Clear();
        }

        private static void PublishBuilding(int id, BuildingState state)
        {
            Span<byte> row = BuildingWire(id);
            row.Clear();
            if (state == null || state.Next == null && state.Current == null) return;
            BinaryPrimitives.WriteUInt16LittleEndian(row, (ushort)id);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(2), state.GlobalId);
            BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(6), (ushort)state.Type);
            row[8] = (byte)OptionIndex(state.Next);
            var batch = state.Current;
            if (batch == null) return;
            row[9] = (byte)(OptionIndex(batch.Option) | ((int)batch.Phase << PhaseShift));
            BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(10), (ushort)batch.UnitId);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(12), batch.UnitGlobalId);
        }

        private static void PublishWorker(Batch batch)
        {
            Span<byte> row = WorkerWire(batch.UnitId);
            BinaryPrimitives.WriteUInt16LittleEndian(row, (ushort)batch.UnitId);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(2), batch.UnitGlobalId);
            BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(6), (ushort)batch.BuildingId);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(8), batch.BuildingGlobalId);
            row[12] = (byte)OptionIndex(batch.Option);
            BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(13), (ushort)batch.Elapsed);
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(15), batch.LastTick);
            row[19] = (byte)(((int)batch.Phase << 1) | (batch.HasTick ? TickMask : 0));
            ((ushort*)WorkerWireKindsAddress)[batch.UnitId] = (ushort)ExpectedWorkerRole(batch.Option.BuildingType);
        }

        internal static void WriteBuildingWire(int id, Span<byte> target)
        {
            if (id <= 0 || id > MaxBuildingId || target.Length != BuildingWireBytes)
                throw new ArgumentOutOfRangeException(nameof(id));
            var row = BuildingWire(id);
            if (TryBuilding(id, BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(2)), out _))
            {
                row.CopyTo(target);
                if (buildings.TryGetValue(id, out var state) && !HasCurrentBatch(state))
                {
                    target.Slice(9).Clear();
                    if (state.Next == null) target.Clear();
                }
            }
            else target.Clear();
        }

        internal static void WriteWorkerWire(int id, Span<byte> target)
        {
            if (id <= 0 || id > MaxUnitId || target.Length != WorkerWireBytes)
                throw new ArgumentOutOfRangeException(nameof(id));
            var row = WorkerWire(id);
            if (workers.TryGetValue(id, out var batch) && IsBatchActorCurrent(batch))
                row.CopyTo(target);
            else target.Clear();
        }

        internal static bool ValidateBuildingWire(int id, uint nativeGid, eStructs nativeType, ReadOnlySpan<byte> source)
        {
            if (id <= 0 || id > MaxBuildingId || source.Length != BuildingWireBytes) return false;
            if (IsZero(source)) return true;
            var type = (eStructs)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(6));
            int next = source[8], current = source[9] & OptionMask;
            int phase = source[9] >> PhaseShift;
            int unitId = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(10));
            uint unitGid = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12));
            return type == nativeType && BinaryPrimitives.ReadUInt16LittleEndian(source) == id && nativeGid != 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(2)) == nativeGid &&
                (next != 0 || current != 0) && OptionMatches(next, type) && OptionMatches(current, type) &&
                (current == 0 ? unitId == 0 && unitGid == 0 && phase == 0 :
                    unitId > 0 && unitId <= MaxUnitId && unitGid != 0 && phase <= (int)BatchPhase.Cargo);
        }

        private static bool OptionMatches(int index, eStructs type)
        {
            var option = frozen.At(index);
            return index == 0 || option != null && !option.IsVanilla && option.BuildingType == type;
        }

        internal static bool ValidateWorkerWire(int id, uint nativeGid, eChimps kind, ReadOnlySpan<byte> source)
        {
            if (id <= 0 || id > MaxUnitId || source.Length != WorkerWireBytes) return false;
            if (IsZero(source)) return true;
            var option = frozen.At(source[12]);
            int buildingId = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(6));
            int elapsed = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(13));
            return BinaryPrimitives.ReadUInt16LittleEndian(source) == id && nativeGid != 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(2)) == nativeGid &&
                buildingId > 0 && buildingId <= MaxBuildingId && BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8)) != 0 &&
                option != null && !option.IsVanilla && MatchesWorkerRole(option.BuildingType, kind) && elapsed <= (option.WorkTicks ?? 0) &&
                ((source[19] & TickMask) != 0) == (elapsed != 0) && (source[19] >> 1) <= (int)BatchPhase.Cargo;
        }

        internal static void ReadBuildingWire(int id, ReadOnlySpan<byte> source)
        {
            if (IsZero(source))
            {
                buildings.Remove(id);
                UpdateDemand(id, null);
                return;
            }
            uint gid = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(2));
            var type = (eStructs)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(6));
            if (!TryBuilding(id, gid, out var building) || !ValidateBuildingWire(id, gid, building->r_BuildingType, source))
                throw new InvalidDataException("Workshop transfer does not match the restored native building.");
            var current = frozen.At(source[9] & OptionMask);
            Batch batch = null;
            if (current != null)
            {
                int unitId = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(10));
                uint unitGid = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12));
                var phase = (BatchPhase)(source[9] >> PhaseShift);
                batch = FindBatch(id, gid, unitId, unitGid);
                if (batch?.Option != current || batch.Phase != phase)
                    batch = new Batch
                    {
                        BuildingId = id, BuildingGlobalId = gid, UnitId = unitId, UnitGlobalId = unitGid,
                        Option = current, Phase = phase
                    };
            }
            var state = new BuildingState { GlobalId = gid, Type = type, Next = frozen.At(source[8]), Current = batch };
            buildings[id] = state;
            UpdateDemand(id, state);
        }

        internal static void ReadWorkerWire(int id, ReadOnlySpan<byte> source)
        {
            if (IsZero(source))
            {
                workers.Remove(id);
                WorkerWire(id).Clear();
                ((ushort*)WorkerWireKindsAddress)[id] = 0;
                return;
            }
            uint gid = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(2));
            if (!TryUnitKind(id, gid, out var kind) || !ValidateWorkerWire(id, gid, kind, source))
                throw new InvalidDataException("Workshop transfer does not match the restored native worker.");
            var batch = new Batch
            {
                UnitId = id, UnitGlobalId = gid,
                BuildingId = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(6)),
                BuildingGlobalId = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8)),
                Option = frozen.At(source[12]),
                Elapsed = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(13)),
                LastTick = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(15)),
                HasTick = (source[19] & TickMask) != 0,
                Phase = (BatchPhase)(source[19] >> 1)
            };
            workers[id] = batch;
            var state = FindState(batch.BuildingId, batch.BuildingGlobalId, batch.Option.BuildingType);
            if (state?.Current?.UnitId == id && state.Current.UnitGlobalId == gid && state.Current.Option == batch.Option &&
                state.Current.Phase == batch.Phase)
                state.Current = batch;
            PublishWorker(batch);
        }

        private static bool MatchesWorkerRole(eStructs type, eChimps kind) => ExpectedWorkerRole(type) == kind;

        private static eChimps ExpectedWorkerRole(eStructs type)
        {
            foreach (var role in WeaponProductionNativeDefinition.Roles)
                if (role.Building == type) return role.Worker;
            return eChimps.CHIMP_TYPE_NULL;
        }

        private static bool IsZero(ReadOnlySpan<byte> source)
        {
            foreach (byte value in source)
                if (value != 0) return false;
            return true;
        }
    }
}
