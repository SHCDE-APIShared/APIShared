using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Economy;
using Iced.Intel;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

// The engine monitor is the only external boundary this source-linked store needs.
// Production row validation/canonicalization/checksum generator execute unchanged.
namespace APIShared.Economy
{
    internal static class WorkerProductionWorldSafety
    { internal static readonly object EngineGate = new object(); }
}

internal static partial class Program
{
    private static unsafe void TestStateContracts()
    {
        Check(sizeof(WorkerProductionStateRow) == 32, "Sidecar wire/save ABI is32 bytes.");
        Check(Marshal.OffsetOf<WorkerProductionStateRow>(nameof(WorkerProductionStateRow.GlobalId)).ToInt32() == 0 &&
            Marshal.OffsetOf<WorkerProductionStateRow>(nameof(WorkerProductionStateRow.Denominator)).ToInt32() == 16 &&
            Marshal.OffsetOf<WorkerProductionStateRow>(nameof(WorkerProductionStateRow.Schema)).ToInt32() == 24,
            "Sidecar fields retain their native generator offsets.");
        WorkerProductionStateRow empty = default;
        Check(WorkerProductionStateStore.Validate(in empty, out _), "All-zero is the unique empty row.");
        foreach (var kind in WorkerProductionCompiler.WorkerTypes)
        {
            var row = WorkerProductionStateStore.Canonical(123, kind, WorkerProductionBatchFlags.EmptyBatch, 2, 6);
            Check(row.Numerator == 1 && row.Denominator == 3, "Fractions reduce independently of any compiled global denominator.");
            Check(WorkerProductionStateStore.Validate(in row, out _), "Every supported worker can preserve zero-base provenance.");
            var noFraction = WorkerProductionStateStore.Canonical(123, kind, WorkerProductionBatchFlags.None, 0, 17);
            Check(noFraction.IsEmpty, "No fraction and no provenance produces all-zero empty.");
            var zero = WorkerProductionStateStore.Canonical(123, kind, WorkerProductionBatchFlags.EmptyBatch, 0, 17);
            Check(zero.Denominator == 1 && !zero.IsEmpty, "Provenance-only rows retain canonical0/1.");
        }
        var good = WorkerProductionStateStore.Canonical(123, eChimps.CHIMP_TYPE_WOODCUTTER, WorkerProductionBatchFlags.None, 1, 2);
        for (int failure = 0; failure < 8; failure++)
        {
            var bad = good;
            switch (failure)
            {
                case 0: bad.GlobalId = 0; break;
                case 1: bad.Schema = 2; break;
                case 2: bad.Reserved = 1; break;
                case 3: bad.Numerator = 2; break;
                case 4: bad.Denominator = 0; break;
                case 5: bad.Denominator = ulong.MaxValue; break;
                case 6: bad.Flags = (WorkerProductionBatchFlags)8; break;
                case 7: bad.Numerator = 2; bad.Denominator = 4; break;
            }
            Check(!WorkerProductionStateStore.Validate(in bad, out _), "Invalid row fails closed before restore/transfer: " + failure);
        }
        // Native record bits and the separate 32-byte row can round-trip without UInt16
        // normalization; this is a byte-layout check, not an installed-game save test.
        byte[] bits = new byte[32]; fixed (byte* p = bits) *(WorkerProductionStateRow*)p = good;
        WorkerProductionStateRow restored; fixed (byte* p = bits) restored = *(WorkerProductionStateRow*)p;
        Check(WorkerProductionStateStore.MixChecksum(42, in restored) == WorkerProductionStateStore.MixChecksum(42, in good), "Canonical row checksum is invariant under byte round-trip.");
        Check(WorkerProductionStateStore.MixChecksum(42, in empty) == 42, "Empty row leaves legacy unit/category checksum unchanged.");
    }

    private static unsafe void TestStateChecksum()
    {
        const int hook = 0x22149, continuation = 0x22159, afterStore = 0x22176;
        Check(Read(hook, 16).AsSpan().SequenceEqual(Convert.FromHexString("8B9424A000000003F948638B50FA3600")), "Actual canonical CRC hook span.");
        var original = APIShared.Economy.WorkerProductionBonusNativeDefinition.Decode(Read(hook, 16), Address(hook));
        ulong rows = unchecked((ulong)WorkerProductionStateStore.RowsAddress.ToInt64());
        ulong control = unchecked((ulong)WorkerProductionStateStore.ControlAddress.ToInt64());
        const int manager = 0x8574320, units = 0x67E8400;
        const uint gid = 123;
        Marshal.WriteInt32(image + manager + 0x36FA50, 2);
        Marshal.WriteInt32(image + units + UnitStride + 0x6F0, unchecked((int)gid));
        Marshal.WriteInt16(image + units + UnitStride + 0x6E6, (short)eChimps.CHIMP_TYPE_WOODCUTTER);
        using var harness = new MachineHarness(a => {
            // External frame input: the original instruction reads category accumulator
            // from RSP+A0. All instructions through the real unit/category stores execute.
            a.mov(Iced.Intel.AssemblerRegisters.__dword_ptr[Iced.Intel.AssemblerRegisters.rsp + 0xA0], 100);
            WorkerProductionStateAssembly.GenerateChecksum(a, original, Address(continuation), rows, control, Address(units));
        }, new Dictionary<int, int> { [afterStore] = 1 });
        var good = WorkerProductionStateStore.Canonical(gid, eChimps.CHIMP_TYPE_WOODCUTTER, WorkerProductionBatchFlags.EmptyBatch, 1, 2);
        foreach (int scenario in new[] { 0, 1, 2, 3, 4, 5, 6, 7 })
        {
            WorkerProductionStateStore.Clear();
            if (scenario != 0) *WorkerProductionStateStore.RowPointer(1) = good;
            if (scenario != 1) WorkerProductionStateStore.Admit(scenario == 2 ? WorkerProductionWorldMode.Legacy : WorkerProductionWorldMode.Extended);
            if (scenario == 4) WorkerProductionStateStore.RowPointer(1)->GlobalId++;
            var currentKind = scenario == 5 ? eChimps.CHIMP_TYPE_PEASANT : eChimps.CHIMP_TYPE_WOODCUTTER;
            Marshal.WriteInt16(image + units + UnitStride + 0x6E6, (short)currentKind);
            if (scenario >= 6) *WorkerProductionStateStore.RowPointer(1) = WorkerProductionStateStore.Canonical(gid,
                eChimps.CHIMP_TYPE_MILLER, WorkerProductionBatchFlags.MillerBatch, scenario == 7 ? 0UL : 1UL, 2);
            harness.ResetInput(); harness.Set(12, 1); harness.Set(3, Address(manager)); harness.Set(7, 17); harness.Set(1, 5);
            var effective = WorkerProductionStateStore.ForCurrentKind(in *WorkerProductionStateStore.RowPointer(1), currentKind);
            uint expected = scenario == 3 || scenario >= 5 ? WorkerProductionStateStore.MixChecksum(22, in effective) : 22;
            harness.Run();
            Check(harness.Get(7) == expected && harness.Get(2) == unchecked(100U + expected), "Actual CRC continuation mixes unit and category equally: " + scenario);
            int checksumOffset = manager + 0x14F0CC + (2 * 10000 + 1) * 4;
            Check(unchecked((uint)Marshal.ReadInt32(image + checksumOffset)) == expected, "Actual native per-unit checksum store includes sidecar.");
            harness.CheckPreserved(0, 1, 2, 7, 12);
            if (scenario == 4) Check(WorkerProductionStateStore.RowPointer(1)->IsEmpty, "CRC retires a reused native identity without hashing stale data.");
            if (scenario == 5 || scenario == 6) Check(effective.Numerator == 1 && effective.Denominator == 2 && effective.Flags == 0,
                "Profession changes preserve per-unit fractions and discard only old-role batch provenance.");
            if (scenario == 7) Check(effective.IsEmpty, "A profession change retires a provenance-only zero-fraction row.");
        }
        WorkerProductionStateStore.Clear();
        TestWeaponStateChecksum();
    }

    private static unsafe void TestWeaponStateChecksum()
    {
        const int manager = 0x8574320, units = 0x67E8400, buildings = 0x64CCBB0;
        const int unitHook = 0x22149, buildingHook = 0x22236;
        const uint gid = 123;
        const eChimps workerKind = eChimps.CHIMP_TYPE_BLACKSMITH;
        IntPtr scratch = Marshal.AllocHGlobal(112);
        try
        {
            new Span<byte>((void*)scratch, 112).Clear();
            IntPtr weaponRows = scratch, buildingRows = scratch + 40, expectedKinds = scratch + 72, demand = scratch + 76;
            Marshal.WriteInt16(weaponRows, 20, 1);
            Marshal.WriteInt32(weaponRows, 22, (int)gid);
            Marshal.WriteByte(weaponRows, 32, 5);
            Marshal.WriteInt16(expectedKinds, 2, (short)workerKind);
            Marshal.WriteInt16(buildingRows, 16, 1);
            Marshal.WriteInt32(buildingRows, 18, (int)gid);
            Marshal.WriteByte(buildingRows, 24, 5);
            Marshal.WriteInt32(image + manager + 0x36FA50, 2);
            Marshal.WriteInt32(image + units + UnitStride + 0x6F0, (int)gid);
            Marshal.WriteInt16(image + units + UnitStride + 0x6E6, (short)workerKind);
            Marshal.WriteInt32(image + buildings + 0x32C + 0x134, (int)gid);

            var unitOriginal = WorkerProductionBonusNativeDefinition.Decode(Read(unitHook, 16), Address(unitHook));
            var buildingOriginal = WorkerProductionBonusNativeDefinition.Decode(Read(buildingHook, 16), Address(buildingHook));
            using var unit = new MachineHarness(a => {
                a.mov(__dword_ptr[rsp + 0xA0], 100);
                WorkerProductionStateAssembly.GenerateChecksum(a, unitOriginal, Address(unitHook + 16),
                    (ulong)WorkerProductionStateStore.RowsAddress, (ulong)WorkerProductionStateStore.ControlAddress,
                    Address(units), (ulong)weaponRows, (ulong)demand, (ulong)expectedKinds);
            }, new Dictionary<int, int> { [0x22176] = 1 });
            using var building = new MachineHarness(a =>
                WorkerProductionStateAssembly.GenerateBuildingChecksum(a, buildingOriginal, Address(buildingHook + 16),
                    (ulong)buildingRows, (ulong)demand, Address(buildings)), new Dictionary<int, int> { [0x2225D] = 1 });

            // Native category and entity stores must consume identical folded values. A
            // stale GID and a profession change on the same slot both leave native CRCs intact.
            for (int scenario = 0; scenario < 5; scenario++)
            {
                Marshal.WriteByte(demand, (byte)(scenario == 0 ? 0 : 1));
                Marshal.WriteInt32(weaponRows, 22, scenario == 2 ? (int)gid + 1 : (int)gid);
                Marshal.WriteInt16(image + units + UnitStride + 0x6E6,
                    (short)(scenario == 3 ? eChimps.CHIMP_TYPE_PEASANT : workerKind));
                Marshal.WriteInt32(buildingRows, 18, scenario == 2 ? (int)gid + 1 : (int)gid);
                Marshal.WriteByte(buildingRows, 24, (byte)(scenario == 4 ? 7 : 5));

                unit.ResetInput(); unit.Set(12, 1); unit.Set(3, Address(manager)); unit.Set(7, 17); unit.Set(1, 5);
                unit.Run();
                uint unitExpected = scenario == 1 || scenario == 4 ?
                    FoldWeaponBytes(22, new ReadOnlySpan<byte>((void*)(weaponRows + 20), 20)) : 22;
                Check(unit.Get(7) == unitExpected && unit.Get(2) == unchecked(100U + unitExpected) &&
                    unchecked((uint)Marshal.ReadInt32(image + manager + 0x14F0CC + (2 * 10000 + 1) * 4)) == unitExpected,
                    "Unit weapon CRC validates demand, GID and current role: " + scenario);
                unit.CheckPreserved(0, 1, 2, 7, 12);

                building.ResetInput(); building.Set(3, Address(manager)); building.Set(5, 1);
                building.Set(6, Address(buildings + 0x32C + 0x6C)); building.Set(0, 5);
                building.Set(2, 17); building.Set(12, 100);
                building.Run();
                uint buildingExpected = scenario == 0 || scenario == 2 ? 22 :
                    FoldWeaponBytes(22, new ReadOnlySpan<byte>((void*)(buildingRows + 16), 16));
                Check(building.Get(2) == buildingExpected && building.Get(12) == unchecked(100U + buildingExpected) &&
                    unchecked((uint)Marshal.ReadInt32(image + manager + 0x1A6F0C + (2 * 4000 + 1) * 4)) == buildingExpected,
                    "Building weapon CRC folds the desired option and rejects a recycled slot: " + scenario);
                building.CheckPreserved(0, 1, 2, 5, 6, 12);
            }
        }
        finally { Marshal.FreeHGlobal(scratch); }
    }

    private static uint FoldWeaponBytes(uint native, ReadOnlySpan<byte> bytes)
    {
        uint mixed = native ^ 2166136261U;
        foreach (byte value in bytes) mixed = unchecked((mixed ^ value) * 16777619U);
        return mixed;
    }
}
