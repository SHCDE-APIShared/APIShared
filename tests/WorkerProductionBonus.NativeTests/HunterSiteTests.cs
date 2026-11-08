using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Wildlife;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

// Executes the two Hunter stubs of APIShared.Wildlife.HunterNativeSites in the private image copy:
// the record they write, register and flag preservation, and the replayed original bytes.
internal static partial class Program
{
    private const int HunterQueryRva = 0x18AF50, HunterQueryLength = 14;
    private const int CorpseArrivalRva = 0x1306AE, CorpseArrivalLength = 15;
    private const int HunterManagerRva = 0x67E8400;

    private static void TestHunterSites()
    {
        Check(Convert.ToHexString(Read(HunterQueryRva, HunterQueryLength)) == "48895C2418555657415441554156",
            "Hunter query entry bytes are the audited span.");
        Check(Convert.ToHexString(Read(CorpseArrivalRva, CorpseArrivalLength)) == "664283BC31E60600002CB8E8030000",
            "Corpse arrival bytes are the audited span.");
        TestHunterQueryEntry();
        TestCorpseArrival();
    }

    private static void TestHunterQueryEntry()
    {
        IntPtr slot = Marshal.AllocHGlobal(16);
        try
        {
            const ulong returnAddress = 0x7FF612345678UL;
            // The production replay pushes registers; here an empty original isolates the record, and the
            // fixture pushes and removes a known return address itself.
            using var harness = new MachineHarness(a =>
            {
                a.mov(r11, returnAddress); a.push(r11);
                HunterNativeAssembly.EmitQueryEntry(a, ReadOnlySpan<Instruction>.Empty, unchecked((ulong)slot.ToInt64()));
                a.lea(rsp, __[rsp + 8]);
                a.AddUnrestrictedJmp(Address(HunterQueryRva + HunterQueryLength));
            }, new Dictionary<int, int> { [HunterQueryRva + HunterQueryLength] = 1 });
            Marshal.WriteInt64(slot, 0); Marshal.WriteInt64(slot, 8, 0);
            harness.ResetInput(); harness.Set(2, 0xFFFF000000000007UL); harness.Run();
            Check(harness.Marker == 1, "Hunter query entry reaches its continuation.");
            Check(Marshal.ReadInt32(slot) == 7 && unchecked((ulong)Marshal.ReadInt64(slot, 8)) == returnAddress,
                "Hunter query entry records EDX and the caller's return address.");
            harness.CheckPreserved(0, 10, 11);
        }
        finally { Marshal.FreeHGlobal(slot); }
    }

    private static unsafe void TestCorpseArrival()
    {
        IntPtr demand = Marshal.AllocHGlobal(8), table = Marshal.AllocHGlobal(HunterNativeAssembly.UnitCapacity * HunterNativeAssembly.EntrySize);
        IntPtr flags = Marshal.AllocHGlobal(HunterNativeAssembly.TypeCount);
        try
        {
            const int hunter = 3, corpse = 5;
            Put(HunterManagerRva + hunter * 0x490 + HunterNativeAssembly.NativeGlobalOffset, 0x1234);
            var original = WorkerProductionBonusNativeDefinition.Decode(Read(CorpseArrivalRva, CorpseArrivalLength), Address(CorpseArrivalRva));
            Action<Assembler> stub = a =>
            {
                HunterNativeAssembly.EmitCorpseArrival(a, original, unchecked((ulong)demand.ToInt64()), unchecked((ulong)table.ToInt64()),
                    unchecked((ulong)flags.ToInt64()));
                a.AddUnrestrictedJmp(Address(CorpseArrivalRva + CorpseArrivalLength));
            };
            Action<Assembler> vanilla = a =>
            {
                WorkerProductionBonusNativeAssembly.Replay(a, original);
                a.AddUnrestrictedJmp(Address(CorpseArrivalRva + CorpseArrivalLength));
            };

            foreach (int type in new[] { 0x2C, 0x2E })
            foreach (int hunterId in new[] { hunter, 0, HunterNativeAssembly.UnitCapacity })
            foreach (byte active in new byte[] { 0, 1 })
            foreach (bool remove in new[] { false, true })
            {
                new Span<byte>((void*)flags, HunterNativeAssembly.TypeCount).Clear();
                if (remove) Marshal.WriteByte(flags, type, HunterNativeAssembly.RemoveCorpseFlag);
                Word(HunterManagerRva + corpse * 0x490 + HunterNativeAssembly.NativeAliveOffset, 2);
                Word(HunterManagerRva + corpse * 0x490 + HunterNativeAssembly.NativeTypeOffset, type);
                Marshal.WriteByte(demand, active);
                new Span<byte>((void*)table, HunterNativeAssembly.UnitCapacity * HunterNativeAssembly.EntrySize).Clear();
                // Both fixtures exit at the same continuation, so they run one after the other.
                var expected = RunCorpseArrival(vanilla, hunterId, hunter, corpse);
                Word(HunterManagerRva + corpse * 0x490 + HunterNativeAssembly.NativeAliveOffset, 2);
                var actual = RunCorpseArrival(stub, hunterId, hunter, corpse);
                Check(Word(HunterManagerRva + corpse * 0x490 + HunterNativeAssembly.NativeAliveOffset) == (remove ? 3 : 2),
                    "Corpse arrival marks the corpse for deletion exactly when its type is flagged.");
                // Same registers (EAX = 1000 from the replayed mov) and the replayed comparison's flags.
                Check(actual.Rax == 0x3E8 && expected.Rax == 0x3E8, "The replayed original instructions ran.");
                Check((actual.Flags & 0x8D5) == (expected.Flags & 0x8D5), "Corpse arrival keeps the replayed comparison's flags.");
                byte* entry = (byte*)table + hunter * HunterNativeAssembly.EntrySize;
                bool written = active == 1 && hunterId == hunter;
                Check(*(int*)(entry + 12) == (written ? 1 : 0), "Corpse arrival records only with demand and a valid Hunter id.");
                if (written)
                    Check(*(uint*)entry == 0x1234 && *(uint*)(entry + 4) == 0xABCD && *(int*)(entry + 8) == type,
                        "Corpse arrival records Hunter global, corpse global and corpse type.");
                for (int other = 0; other < 4; other++)
                    Check(*(long*)((byte*)table + other * HunterNativeAssembly.EntrySize) == 0 || other == hunter,
                        "Corpse arrival writes no other entry.");
            }
        }
        finally { Marshal.FreeHGlobal(demand); Marshal.FreeHGlobal(table); Marshal.FreeHGlobal(flags); Word(HunterManagerRva + 5 * 0x490 + HunterNativeAssembly.NativeAliveOffset, 0); Word(HunterManagerRva + 5 * 0x490 + HunterNativeAssembly.NativeTypeOffset, 0); Put(HunterManagerRva + 3 * 0x490 + HunterNativeAssembly.NativeGlobalOffset, 0); }
    }

    // Every other register, the SIMD sentinels and the stack must come back unchanged.
    private static (ulong Rax, ulong Flags) RunCorpseArrival(Action<Assembler> emit, int hunterId, int hunter, int corpse)
    {
        using var harness = new MachineHarness(emit, new Dictionary<int, int> { [CorpseArrivalRva + CorpseArrivalLength] = 1 });
        harness.ResetInput();
        harness.Set(0, 0xABCD); harness.Set(3, unchecked((ulong)hunterId)); harness.Set(7, (ulong)(hunter * 0x490));
        harness.Set(1, (ulong)(corpse * 0x490)); harness.Set(14, Address(HunterManagerRva));
        harness.Run();
        Check(harness.Marker == 1, "Corpse arrival reaches its continuation.");
        harness.CheckPreserved(0);
        return (harness.Get(0), harness.Flags);
    }
}
