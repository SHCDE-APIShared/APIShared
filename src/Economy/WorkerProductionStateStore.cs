using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    [Flags]
    internal enum WorkerProductionBatchFlags : ushort
    {
        None = 0, EmptyBatch = 1, StoneBatch = 2, MillerBatch = 4
    }

    // Persisted and Chore56 wire ABI, independently versioned from the native unit record.
    // Fraction identity is the one-based native slot AND GlobalId. ProducerKind belongs
    // to batch provenance: a profession change retains credit and clears old flags. The native
    // generator owns writes under EngineMonitor. Rows are never rebased to a plan denominator.
    [StructLayout(LayoutKind.Explicit, Size = 32, Pack = 1)]
    internal struct WorkerProductionStateRow
    {
        [FieldOffset(0)] internal uint GlobalId;
        [FieldOffset(4)] internal eChimps ProducerKind;
        [FieldOffset(6)] internal WorkerProductionBatchFlags Flags;
        [FieldOffset(8)] internal ulong Numerator;
        [FieldOffset(16)] internal ulong Denominator;
        [FieldOffset(24)] internal uint Schema;
        [FieldOffset(28)] internal uint Reserved;
        internal bool IsEmpty => GlobalId == 0 && ProducerKind == 0 && Flags == 0 &&
            Numerator == 0 && Denominator == 0 && Schema == 0 && Reserved == 0;
    }

    internal enum WorkerProductionWorldMode { Legacy = 0, Extended = 1 }

    internal static unsafe class WorkerProductionStateStore
    {
        internal const int RowSize = 32, RowCapacity = 10000, WireSchema = 1;
        // These allocations are process-lived, including a failed or partial publication.
        internal static readonly IntPtr RowsAddress = Allocate(RowCapacity * RowSize);
        internal static readonly IntPtr ControlAddress = Allocate(8);
        internal static WorkerProductionWorldMode WorldMode => (WorkerProductionWorldMode)Volatile.Read(ref *(int*)ControlAddress);
        internal static bool Ready => Volatile.Read(ref *(int*)(ControlAddress + 4)) != 0;

        private static IntPtr Allocate(int bytes)
        {
            IntPtr memory = Marshal.AllocHGlobal(bytes);
            new Span<byte>((void*)memory, bytes).Clear();
            return memory;
        }

        internal static WorkerProductionStateRow* RowPointer(int unitId)
        {
            if (unitId <= 0 || unitId >= RowCapacity) throw new ArgumentOutOfRangeException(nameof(unitId));
            return (WorkerProductionStateRow*)((byte*)RowsAddress + unitId * RowSize);
        }

        // Admission/end only, while the real engine monitor excludes every native reader.
        internal static void Clear()
        {
            Volatile.Write(ref *(int*)(ControlAddress + 4), 0);
            new Span<byte>((void*)RowsAddress, RowCapacity * RowSize).Clear();
            Volatile.Write(ref *(int*)ControlAddress, (int)WorkerProductionWorldMode.Legacy);
        }
        internal static void Admit(WorkerProductionWorldMode mode)
        {
            if (mode != WorkerProductionWorldMode.Legacy && mode != WorkerProductionWorldMode.Extended)
                throw new ArgumentOutOfRangeException(nameof(mode));
            Volatile.Write(ref *(int*)ControlAddress, (int)mode);
            Volatile.Write(ref *(int*)(ControlAddress + 4), 1);
        }

        internal static bool Validate(in WorkerProductionStateRow row, out string reason)
        {
            reason = string.Empty;
            if (row.IsEmpty) return true;
            if (row.GlobalId == 0 || row.Schema != WireSchema || row.Reserved != 0)
                reason = "Invalid production row identity or schema.";
            else if (row.ProducerKind == eChimps.CHIMP_TYPE_NULL || !Enum.IsDefined(typeof(eChimps), row.ProducerKind))
                reason = "The production row has an invalid native unit kind.";
            else if (row.Flags != 0 && !WorkerProductionCompiler.IsWorker(row.ProducerKind) && row.ProducerKind != eChimps.CHIMP_TYPE_QUARRY_OX)
                reason = "Batch provenance belongs to an audited producing role.";
            else if ((row.Flags & ~(WorkerProductionBatchFlags.EmptyBatch | WorkerProductionBatchFlags.StoneBatch |
                WorkerProductionBatchFlags.MillerBatch)) != 0)
                reason = "The production row has unknown batch flags.";
            else if (row.Denominator == 0 || row.Denominator > long.MaxValue || row.Numerator >= row.Denominator ||
                Gcd(row.Numerator, row.Denominator) != 1)
                reason = "The production row fraction is not canonical or representable.";
            else if (row.Numerator == 0 && row.Flags == WorkerProductionBatchFlags.None)
                reason = "A zero production fraction without batch provenance must be an empty row.";
            else if ((row.Flags & WorkerProductionBatchFlags.MillerBatch) != 0 && row.ProducerKind != eChimps.CHIMP_TYPE_MILLER)
                reason = "Miller provenance belongs to a miller.";
            else if ((row.Flags & WorkerProductionBatchFlags.StoneBatch) != 0 &&
                row.ProducerKind != eChimps.CHIMP_TYPE_QUARRY_OX)
                reason = "Stone-batch provenance belongs to the carrying ox.";
            return reason.Length == 0;
        }

        internal static ulong Gcd(ulong a, ulong b)
        { while (b != 0) { ulong next = a % b; a = b; b = next; } return a; }

        internal static WorkerProductionStateRow Canonical(uint globalId, eChimps kind,
            WorkerProductionBatchFlags flags, ulong numerator, ulong denominator)
        {
            if (denominator == 0 || numerator >= denominator) throw new InvalidDataException("Invalid production fraction.");
            if (numerator == 0 && flags == 0) return default;
            ulong gcd = Gcd(numerator, denominator);
            var row = new WorkerProductionStateRow { GlobalId = globalId, ProducerKind = kind, Flags = flags,
                Numerator = numerator / gcd, Denominator = denominator / gcd, Schema = WireSchema };
            if (!Validate(in row, out string reason)) throw new InvalidDataException(reason);
            return row;
        }
        internal static WorkerProductionStateRow ForCurrentKind(in WorkerProductionStateRow row, eChimps currentKind) =>
            row.IsEmpty || row.ProducerKind == currentKind ? row :
                Canonical(row.GlobalId, currentKind, WorkerProductionBatchFlags.None, row.Numerator, row.Denominator);

        internal static ulong[] CaptureDenominators()
        {
            // The compiler calls its provider BEFORE its registry lock. This reentrant
            // engine lock also protects a profile edit made from the Unity thread.
            lock (WorkerProductionWorldSafety.EngineGate)
            {
                var seen = new SortedSet<ulong>();
                for (int id = 1; id < RowCapacity; id++)
                {
                    var row = *RowPointer(id);
                    if (!Validate(in row, out string reason)) throw new InvalidDataException(reason);
                    if (!row.IsEmpty) seen.Add(row.Denominator);
                }
                var result = new ulong[seen.Count]; seen.CopyTo(result); return result;
            }
        }

        // Managed reference of the mix the native checksum stub performs. Production never calls it;
        // the native tests compare the stub's result against it, so it is not dead code.
        internal static uint MixChecksum(uint checksum, in WorkerProductionStateRow row)
        {
            if (row.IsEmpty) return checksum;
            // Stable little-endian FNV-1a over all 32 bytes. Empty rows leave legacy CRC
            // exactly unchanged; a category checksum receives the same mixed unit result.
            uint hash = checksum ^ 2166136261U;
            var copy = row; byte* bytes = (byte*)&copy;
            for (int n = 0; n < RowSize; n++) hash = unchecked((hash ^ bytes[n]) * 16777619U);
            return hash;
        }
    }
}
