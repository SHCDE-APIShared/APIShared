using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Buildings;
using APIShared.Economy;
using APIShared.Wildlife;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

// Executes the corpse lifetime stubs, the build-menu availability wrapper and the chicken target bridge
// against the private image copy: the stubs behave exactly like Vanilla code with the same constant or
// decision, Vanilla's own function body answers every query that no decision changes, and all registers
// Vanilla relies on stay as it leaves them.
internal static partial class Program
{
    private const int CamelCorpseRva = 0x1584BE, CamelCorpseSpan = 19, ChickenCorpseRva = 0x16346B, ChickenCorpseSpan = 20;
    private const int MapperEntryRva = 0xFFC70, MapperEntrySpan = 14;
    private const int GameTypeRva = 0x3665F28, NetworkModeRva = 0x8574B90, StartPreyRva = 0x37EF97C;
    private const int ChickenSpanRva = 0xD2AB4, ChickenSpanLength = 17, ManyChickensRva = 0x3665FE0;
    private const ulong ArithmeticFlags = 0x8D5;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MapperQuery(IntPtr record, int mapper);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ChickenCallback(int player, int vanilla, int count);

    private static unsafe void TestCorpseLifetimeSites()
    {
        Check(CorpseLifetimeNativeAssembly.EncodeThreshold(1800, 1) == 1800, "1800 ticks keep ImprovedHunters' former constant.");
        Check(CorpseLifetimeNativeAssembly.EncodeThreshold(900, 2) == 1800, "Chickens count their fading twice per tick.");
        Check(CorpseLifetimeNativeAssembly.EncodeThreshold(32, 1) == 0x20 && CorpseLifetimeNativeAssembly.EncodeThreshold(16, 2) == 0x20 &&
            CorpseLifetimeNativeAssembly.EncodeThreshold(0, 1) == 0x20, "A request at or below Vanilla keeps Vanilla.");
        Check(CorpseLifetimeNativeAssembly.EncodeThreshold(33, 1) == 264 && CorpseLifetimeNativeAssembly.EncodeThreshold(1801, 1) == 2056,
            "Longer lifetimes round up to 256 * n + 8.");
        Check(CorpseLifetimeNativeAssembly.EncodeThreshold(CorpseLifetimeNativeAssembly.MaximumLifetimeTicks, 2) <= CorpseLifetimeNativeAssembly.MaximumThreshold,
            "The longest lifetime fits the signed counter for every type.");
        for (int ticks = 33; ticks < 20000; ticks += 97)
        {
            int threshold = CorpseLifetimeNativeAssembly.EncodeThreshold(ticks, 1);
            Check((threshold & 0xFF) == CorpseLifetimeNativeAssembly.LongLifetimeFadeCap && threshold >= ticks && threshold - ticks < 256,
                "Encoded thresholds keep the fade cap and stay within one block of the request.");
        }

        foreach (var (rva, span, hex) in new[] {
            (CamelCorpseRva, CamelCorpseSpan, "FE842B86090000B920000000388C2B86090000"),
            (ChickenCorpseRva, ChickenCorpseSpan, "FE842B86090000B92000000066FF842B20090000") })
        {
            Check(Convert.ToHexString(Read(rva, span)) == hex, "Corpse span is the audited one at 0x" + rva.ToString("X") + ".");
            var original = WorkerProductionBonusNativeDefinition.Decode(Read(rva, span), Address(rva));
            IntPtr cell = Marshal.AllocHGlobal(8);
            IntPtr unit = Marshal.AllocHGlobal(0x1000);
            try
            {
                foreach (int threshold in new[] { 0x20, 1800, 0x7F08 })
                foreach (byte fade in new byte[] { 0, 7, 8, 31, 32 })
                {
                    Marshal.WriteInt32(cell, threshold);
                    // Reference: Vanilla's span with the constant itself, the former immediate patch.
                    var reference = RunCorpseSpan(rva, span, unit, fade, a =>
                    {
                        foreach (var decoded in original)
                        {
                            if (decoded.Code == Iced.Intel.Code.Mov_r32_imm32) { a.mov(ecx, threshold); continue; }
                            var instruction = decoded; instruction.IP = 0; a.AddInstruction(instruction);
                        }
                    });
                    var stub = RunCorpseSpan(rva, span, unit, fade, a =>
                        CorpseLifetimeNativeAssembly.EmitThreshold(a, original, unchecked((ulong)cell.ToInt64())));
                    Check(reference.Fade == stub.Fade && reference.Counter == stub.Counter && reference.Rcx == stub.Rcx &&
                        (reference.Flags & ArithmeticFlags) == (stub.Flags & ArithmeticFlags),
                        "Corpse stub equals Vanilla's span with constant " + threshold + " at 0x" + rva.ToString("X") + ".");
                    Check(unchecked((uint)stub.Rcx) == (uint)threshold, "Corpse stub loads the cell's threshold.");
                }
            }
            finally { Marshal.FreeHGlobal(cell); Marshal.FreeHGlobal(unit); }
        }
    }

    private static (byte Fade, short Counter, ulong Rcx, ulong Flags) RunCorpseSpan(int rva, int span, IntPtr unit, byte fade, Action<Assembler> body)
    {
        for (int i = 0; i < 0x1000; i++) Marshal.WriteByte(unit, i, 0);
        Marshal.WriteByte(unit, 0x986, fade);
        Marshal.WriteInt16(unit, 0x920, 40);
        using var harness = new MachineHarness(a =>
        {
            body(a);
            a.AddUnrestrictedJmp(Address(rva + span));
        }, new Dictionary<int, int> { [rva + span] = 1 });
        harness.ResetInput();
        // The updates address the unit as [rbx + rbp + field].
        harness.Set(3, unchecked((ulong)unit.ToInt64()));
        harness.Set(5, 0);
        harness.Run();
        Check(harness.Marker == 1, "Corpse span reaches its continuation.");
        harness.CheckPreserved(1);
        return (Marshal.ReadByte(unit, 0x986), Marshal.ReadInt16(unit, 0x920), harness.Get(1), harness.Flags);
    }

    private static void TestMapperAvailability()
    {
        Check(Convert.ToHexString(Read(MapperEntryRva, MapperEntrySpan)) == "8D824EFFFFFF4C8BC183F8017614", "Mapper entry is the audited one.");
        var original = WorkerProductionBonusNativeDefinition.Decode(Read(MapperEntryRva, MapperEntrySpan), Address(MapperEntryRva));
        IntPtr table = Marshal.AllocHGlobal(BuildMenuAvailabilityAssembly.MapperCapacity);
        IntPtr record = Marshal.AllocHGlobal(0x1000);
        try
        {
            for (int i = 0; i < BuildMenuAvailabilityAssembly.MapperCapacity; i++) Marshal.WriteByte(table, i, 0);
            for (int i = 0; i < 0x1000; i++) Marshal.WriteByte(record, i, 0);
            using var stub = new Code(a =>
            {
                BuildMenuAvailabilityAssembly.EmitEntry(a, original, Address(MapperEntryRva + MapperEntrySpan), unchecked((ulong)table.ToInt64()));
                a.AddUnrestrictedJmp(Address(MapperEntryRva + MapperEntrySpan));
            });
            var wrapped = Marshal.GetDelegateForFunctionPointer<MapperQuery>(stub.Address);
            var vanilla = Marshal.GetDelegateForFunctionPointer<MapperQuery>(image + MapperEntryRva);
            const int hunter = 0x4E, beyondTable = 0x1C0;

            int Query(MapperQuery query, int mapper, byte decision, int word, int networkMode, int startPrey)
            {
                Put(GameTypeRva, 0);
                Put(NetworkModeRva, networkMode);
                Put(StartPreyRva, startPrey);
                if (mapper < BuildMenuAvailabilityAssembly.MapperCapacity) Marshal.WriteByte(table, mapper, decision);
                if (mapper < BuildMenuAvailabilityAssembly.MissionTableMappers)
                    Marshal.WriteInt16(record, BuildMenuAvailabilityAssembly.MissionTableOffset + mapper * 2, unchecked((short)word));
                return query(record, mapper);
            }

            Check(Query(vanilla, hunter, 0, 1, 99, 0) == 0, "Vanilla hides the hut on a skirmish map without deer and goats.");
            Check(Query(wrapped, hunter, 0, 1, 99, 0) == 0, "Without a decision the wrapper keeps Vanilla's refusal.");
            Check(Query(wrapped, hunter, 0, 1, 99, 3) == Query(vanilla, hunter, 0, 1, 99, 3) && Query(vanilla, hunter, 0, 1, 99, 3) == 1,
                "Without a decision the wrapper keeps Vanilla's availability.");
            Check(Query(wrapped, hunter, BuildMenuAvailabilityAssembly.Allow, 1, 99, 0) == 1, "Allow waives the start-prey rule.");
            Check(Query(vanilla, hunter, 0, 0, 0, 3) == 0 && Query(wrapped, hunter, BuildMenuAvailabilityAssembly.Allow, 0, 0, 3) == 0,
                "Allow keeps the mission's own table refusal.");
            Check(Query(wrapped, hunter, BuildMenuAvailabilityAssembly.Allow, -1, 99, 0) == 0, "A negative table entry stays unavailable.");
            Check(Query(wrapped, hunter, BuildMenuAvailabilityAssembly.Allow, 5, 0, 3) == 5, "Allow keeps Vanilla's positive value.");
            Check(Query(wrapped, hunter, BuildMenuAvailabilityAssembly.Veto, 1, 0, 3) == 0, "Veto refuses an available mapper.");
            Check(Query(vanilla, beyondTable, 0, 0, 0, 0) == 0 && Query(wrapped, beyondTable, BuildMenuAvailabilityAssembly.Allow, 0, 0, 0) == 1,
                "Allow makes a mapper beyond the mission table available.");
            Check(Query(wrapped, 600, 0, 0, 0, 0) == Query(vanilla, 600, 0, 0, 0, 0), "Mappers beyond the decision table run Vanilla.");
            for (int mapper = 1; mapper < BuildMenuAvailabilityAssembly.MissionTableMappers; mapper += 7)
                foreach (int word in new[] { 0, 1, 3 })
                    Check(Query(wrapped, mapper, 0, word, 99, 0) == Query(vanilla, mapper, 0, word, 99, 0),
                        "Without a decision mapper " + mapper + " answers like Vanilla.");
        }
        finally { Marshal.FreeHGlobal(table); Marshal.FreeHGlobal(record); }
    }

    private static int chickenCalls, chickenPlayer, chickenVanilla, chickenCount, chickenResult;
    private static readonly ChickenCallback ChickenTarget = (player, vanilla, count) =>
    {
        chickenCalls++; chickenPlayer = player; chickenVanilla = vanilla; chickenCount = count;
        return chickenResult;
    };

    private static void TestChickenTarget()
    {
        Check(Convert.ToHexString(Read(ChickenSpanRva, ChickenSpanLength)) == "833D2535590300410F45C53B8748200000", "Chicken target span is the audited one.");
        var original = WorkerProductionBonusNativeDefinition.Decode(Read(ChickenSpanRva, ChickenSpanLength), Address(ChickenSpanRva));
        IntPtr demand = Marshal.AllocHGlobal(8);
        IntPtr record = Marshal.AllocHGlobal(0x3000);
        ulong callback = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(ChickenTarget).ToInt64());
        try
        {
            foreach (bool many in new[] { false, true })
            foreach (bool active in new[] { false, true })
            foreach (int replaced in new[] { 0, 5, 7 })
            {
                Put(ManyChickensRva, many ? 1 : 0);
                Marshal.WriteInt64(demand, active ? 1 : 0);
                Marshal.WriteInt32(record, ChickenPopulationNativeAssembly.CountOffset, 5);
                chickenCalls = 0; chickenResult = replaced;
                using var harness = new MachineHarness(a =>
                {
                    ChickenPopulationNativeAssembly.EmitTarget(a, original, unchecked((ulong)demand.ToInt64()), callback);
                    a.AddUnrestrictedJmp(Address(ChickenSpanRva + ChickenSpanLength));
                }, new Dictionary<int, int> { [ChickenSpanRva + ChickenSpanLength] = 1 });
                harness.ResetInput();
                harness.Set(0, 3);
                harness.Set(3, 2);
                harness.Set(7, unchecked((ulong)record.ToInt64()));
                harness.Set(13, 100);
                harness.Run();
                Check(harness.Marker == 1, "Chicken target reaches the jle.");
                int vanilla = many ? 100 : 3;
                int target = active ? replaced : vanilla;
                Check(harness.Get(0) == (ulong)target, "Chicken target in EAX is " + target + ".");
                Check(chickenCalls == (active ? 1 : 0), "The callback runs only with demand.");
                if (active)
                    Check(chickenPlayer == 2 && chickenVanilla == vanilla && chickenCount == 5, "The callback sees player, Vanilla's final target and the count.");
                bool zero = target == 5, sign = target - 5 < 0;
                Check(((harness.Flags & 0x40) != 0) == zero && ((harness.Flags & 0x80) != 0) == sign && (harness.Flags & 0x800) == 0,
                    "The flags are those of cmp target, count.");
                harness.CheckPreserved(0);
            }
        }
        finally { Marshal.FreeHGlobal(demand); Marshal.FreeHGlobal(record); }
    }
}
