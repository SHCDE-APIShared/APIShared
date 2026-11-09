using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Modifiers;
using Iced.Intel;
using RedBird.X64.Hooks;
using SHCDESE.Interop;

internal static partial class Program
{
    private sealed class HotConfiguration : IDisposable
    {
        internal readonly IntPtr Slot = Marshal.AllocHGlobal(8), Header = Marshal.AllocHGlobal(WorkerProductionHotAssembly.HeaderSize),
            Fault = Marshal.AllocHGlobal(32), Ai = Marshal.AllocHGlobal(36), OriginalUnit = Marshal.AllocHGlobal(4);
        internal IntPtr Table, FearTable;
        internal WorkerProductionHotAddresses Addresses => new WorkerProductionHotAddresses(
            (ulong)Slot.ToInt64(), (ulong)WorkerProductionStateStore.RowsAddress.ToInt64(),
            (ulong)WorkerProductionStateStore.ControlAddress.ToInt64(), (ulong)Fault.ToInt64(), (ulong)Ai.ToInt64(), (ulong)OriginalUnit.ToInt64());
        internal HotConfiguration()
        {
            Marshal.WriteInt64(Slot, Header.ToInt64());
            Marshal.WriteInt64(Header, WorkerProductionHotAssembly.FearTableOffset, 0);
            Marshal.WriteInt32(Header, WorkerProductionHotAssembly.FearHasNegativeOffset, 0);
            Marshal.WriteInt32(OriginalUnit, 0);
            for (int n = 0; n < 36; n += 4) Marshal.WriteInt32(Ai, n, 0);
            ResetFault();
        }
        internal void ResetFault()
        { for (int n = 0; n < 32; n += 4) Marshal.WriteInt32(Fault, n, 0); Marshal.WriteInt32(Fault, 24, 1); }
        internal void Set(WorkerProductionCompiledRules rules, ulong mask, bool includeBaseTable = true)
        {
            if (Table != IntPtr.Zero) Marshal.FreeHGlobal(Table);
            Table = includeBaseTable ? Marshal.AllocHGlobal(rules.Entries.Count * WorkerProductionHotAssembly.EntrySize) : IntPtr.Zero;
            if (includeBaseTable)
            {
                for (int n = 0; n < rules.Entries.Count; n++)
                {
                    int offset = n * WorkerProductionHotAssembly.EntrySize;
                    var entry = rules.Entries[n]; Marshal.WriteInt64(Table, offset, entry.Whole);
                    Marshal.WriteInt64(Table, offset + 8, (long)entry.Numerator);
                    Marshal.WriteInt64(Table, offset + 16, (long)entry.Denominator);
                }
            }
            if (FearTable != IntPtr.Zero) Marshal.FreeHGlobal(FearTable);
            FearTable = IntPtr.Zero;
            if (rules.HasFearChanges)
            {
                FearTable = Marshal.AllocHGlobal(rules.FearBonusByLevel.Count * sizeof(int));
                for (int n = 0; n < rules.FearBonusByLevel.Count; n++)
                    Marshal.WriteInt32(FearTable, n * sizeof(int), rules.FearBonusByLevel[n]);
            }
            Marshal.WriteInt64(Header, WorkerProductionHotAssembly.CommonDenominatorOffset, (long)rules.CommonDenominator);
            Marshal.WriteInt64(Header, WorkerProductionHotAssembly.TableOffset, Table.ToInt64());
            Marshal.WriteInt64(Header, WorkerProductionHotAssembly.ActiveRoleMaskOffset, unchecked((long)mask));
            Marshal.WriteInt64(Header, WorkerProductionHotAssembly.FearTableOffset, FearTable.ToInt64());
            Marshal.WriteInt32(Header, WorkerProductionHotAssembly.FearHasNegativeOffset, rules.HasNegativeFearBonus ? 1 : 0);
        }
        public void Dispose()
        {
            if (Table != IntPtr.Zero) Marshal.FreeHGlobal(Table);
            if (FearTable != IntPtr.Zero) Marshal.FreeHGlobal(FearTable);
            Marshal.FreeHGlobal(Slot); Marshal.FreeHGlobal(Header); Marshal.FreeHGlobal(Fault); Marshal.FreeHGlobal(Ai); Marshal.FreeHGlobal(OriginalUnit);
        }
    }

    private static unsafe void TestHotProductionCore()
    {
        var profiles = new Dictionary<eChimps, WorkerProductionValues>();
        foreach (var worker in WorkerProductionCompiler.WorkerTypes)
            profiles.Add(worker, new WorkerProductionValues(ValueModifierContribution.Factor(0.25m), ValueModifierContribution.Factor(2)));
        var compiled = CompileProfiles(new WorkerProductionProfile(profiles)).Rules;
        using var configuration = new HotConfiguration();
        configuration.Set(compiled, (1UL << 34) - 1);
        IntPtr percent = Marshal.AllocHGlobal(8); Marshal.WriteInt32(percent, 0); Marshal.WriteInt32(percent, 4, 1);
        var site = WorkerProductionBonusNativeDefinition.Sites.Single(s => s.Kind == WorkerProductionBonusNativeKind.Core);
        try
        {
            using var hook = new X64InlineHook(Address(site.Rva), site.Length);
            hook.Generate((a, original, back) => WorkerProductionBonusNativeAssembly.Generate(a, original, back, site,
                (ulong)image.ToInt64(), (ulong)percent.ToInt64(), Address(ModeRva),
                Address(WorkerProductionBonusNativeDefinition.UnitManagerRva), Address(WorkerProductionBonusNativeDefinition.CurrentUnitRva), hot: configuration.Addresses));
            hook.Enable();
            foreach (var kind in WorkerProductionCompiler.WorkerTypes.Where(k => k != eChimps.CHIMP_TYPE_MILLER))
            {
                var actualKind = kind == eChimps.CHIMP_TYPE_QUARRY_GRUNT ? eChimps.CHIMP_TYPE_QUARRY_OX : kind;
                foreach (int ai in new[] { 0, 1 })
                foreach (int bonus in new[] { -100, -50, 0, 50, 1000 })
                foreach (int productivity in new[] { 100, 150 })
                foreach (int mode in new[] { 0, 99 })
                foreach (int eligible in new[] { 0, 1 })
                {
                    WorkerProductionStateStore.Clear(); WorkerProductionStateStore.Admit(WorkerProductionWorldMode.Extended);
                    Marshal.WriteInt32(configuration.Ai, 4, ai); configuration.ResetFault(); Marshal.WriteInt32(percent, bonus);
                    Marshal.WriteInt32(image + WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitStride + 0x6F0, 123);
                    Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitStride + 0x6E6, (int)actualKind);
                    Put(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitStride + 0x918, 0x5006D);
                    ResetYield(productivity, mode, 0);
                    int nativeRemainder = 0;
                    for (int cycle = 1; cycle <= 8; cycle++)
                    {
                        int baseAmount = ai == 1 ? 2 : cycle % 4 == 0 ? 1 : 0;
                        var expected = ReferenceYield(productivity, mode, eligible, baseAmount, nativeRemainder, bonus);
                        int actual = YieldFunction(UnitId, 1, eligible);
                        Check(actual == expected.Amount, "Actual hot Core amount: " + kind + "/" + ai + "/" + cycle);
                        Check(Word(RemainderRva + UnitStride) == expected.Remainder, "Native fixed/Fear fractional carry remains separate from base credit.");
                        nativeRemainder = expected.Remainder;
                        Check(Marshal.ReadInt32(configuration.Fault) == 0, "Valid compiled inputs never latch a fault.");
                        var row = *WorkerProductionStateStore.RowPointer(1);
                        Check(WorkerProductionStateStore.Validate(in row, out _), "Native writes canonical, serializable sidecar rows.");
                        if (!row.IsEmpty) Check(row.ProducerKind == actualKind, "Ox identity records actual carrier kind rather than Grunt rule kind.");
                    }
                }
            }
            // Model the audited SE Pre UnitId change at the native entry boundary:
            // its original actor context is1, but the helper receives target2. Real
            // original arithmetic must keep productivity/bonus words at target2.
            WorkerProductionStateStore.Clear(); WorkerProductionStateStore.Admit(WorkerProductionWorldMode.Extended);
            configuration.ResetFault(); Marshal.WriteInt32(configuration.OriginalUnit, 1);
            Marshal.WriteInt32(configuration.Ai, 4, 0); Marshal.WriteInt32(configuration.Ai, 8, 1);
            Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitStride + 0x6E6, (int)eChimps.CHIMP_TYPE_WOODCUTTER);
            Put(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitStride + 0x6F0, 123);
            Word(OwnerRva + UnitStride, 1); Word(RemainderRva + UnitStride, 37); Word(ExtraRva + UnitStride, 9);
            Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + 2 * UnitStride + 0x6E6, (int)eChimps.CHIMP_TYPE_WOODCUTTER);
            Put(WorkerProductionBonusNativeDefinition.UnitManagerRva + 2 * UnitStride + 0x6F0, 456);
            ResetYield(150, 99, 0, 2, 2); Marshal.WriteInt32(percent, 50);
            for (int cycle = 1; cycle <= 4; cycle++)
            {
                Check(YieldFunction(2, 1, 1) == (cycle == 4 ? 2 : 0), "Redirected native target retains its own Fear/fixed arithmetic while base uses original human actor.");
                Check(WorkerProductionStateStore.RowPointer(2)->IsEmpty, "Redirected target never acquires the physical actor's base credits.");
                Check(Word(RemainderRva + UnitStride) == 37 && Word(ExtraRva + UnitStride) == 9,
                    "Original actor's native bonus words remain untouched by SE's redirected helper.");
                var row = *WorkerProductionStateStore.RowPointer(1);
                if (cycle < 4) Check(row.GlobalId == 123 && row.Numerator * 4 == (ulong)cycle * row.Denominator,
                    "Original actor owns the exact fractional base sequence.");
            }
            Marshal.WriteInt32(configuration.OriginalUnit, 0);
            // Invalid upstream numbers stop at the native helper's leaf RET, before its
            // old IMUL/Int16 narrowing can execute. Native carry/extra remain untouched.
            foreach (var bad in new[] { (Input: 101, P: 100, Code: 1), (Input: -1, P: 100, Code: 1), (Input: 1, P: 160, Code: 2) })
            {
                configuration.ResetFault(); ResetYield(bad.P, 99, 17);
                int result = YieldFunction(1, bad.Input, 1);
                Check(result == 0 && Marshal.ReadInt32(configuration.Fault) == bad.Code, "Invalid hot input takes terminal safe return.");
                Check(Word(RemainderRva + UnitStride) == 17 && Word(ExtraRva + UnitStride) == 0x1234, "Fault does not execute original narrowed writes.");
                Check(Marshal.ReadInt32(configuration.Fault, 4) == 1 && Marshal.ReadInt32(configuration.Fault, 8) == bad.Input,
                    "Fault metadata describes the actual native invocation.");
            }
            // An inactive role without historical credits retains the old native result.
            configuration.Set(CompileProfiles().Rules, 0); configuration.ResetFault();
            WorkerProductionStateStore.Clear(); WorkerProductionStateStore.Admit(WorkerProductionWorldMode.Extended);
            ResetYield(150, 99, 0); Marshal.WriteInt32(percent, 50);
            Check(YieldFunction(1, 12, 1) == 24 && WorkerProductionStateStore.RowPointer(1)->IsEmpty,
                "No-base inactive role follows native Fear/fixed output without creating sidecar.");
            TestFearHotCore(configuration, percent);
            configuration.Set(compiled, (1UL << 34) - 1); Marshal.WriteInt32(configuration.Ai, 4, 0);
            Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitStride + 0x6E6, (int)eChimps.CHIMP_TYPE_WOODCUTTER);
            var originalCore = WorkerProductionBonusNativeDefinition.Decode(site.OriginalBytes, Address(site.Rva));
            using var harness = new MachineHarness((a) => WorkerProductionBonusNativeAssembly.Generate(a, originalCore,
                Address(site.Rva + site.Length), site, (ulong)image.ToInt64(), (ulong)percent.ToInt64(), Address(ModeRva),
                Address(WorkerProductionBonusNativeDefinition.UnitManagerRva), Address(WorkerProductionBonusNativeDefinition.CurrentUnitRva),
                hot: configuration.Addresses), new Dictionary<int, int> { [0x18D9E1] = 1 });
            foreach (int productivity in new[] { 100, 160 })
            {
                WorkerProductionStateStore.Clear(); WorkerProductionStateStore.Admit(WorkerProductionWorldMode.Extended);
                configuration.ResetFault(); ResetYield(productivity, 99, 0); Marshal.WriteInt32(percent, 0);
                harness.ResetInput(); harness.Set(0, (uint)productivity); harness.Set(1, 100); harness.Set(8, 1);
                harness.Set(9, UnitStride); harness.Set(10, 1); harness.Set(11, Address(0)); harness.Run();
                Check(harness.Get(0) == 0 && harness.Marker == 1, "Defined hot-address path reaches actual native return.");
                harness.CheckPreserved(productivity == 100 ? new[] { 0, 1, 2, 8, 10 } : new[] { 0 });
                Check((harness.Flags & 0x8D5) == 0x44, "Successful zero native return and terminal fault both establish correct arithmetic flags.");
            }
        }
        finally { Marshal.FreeHGlobal(percent); WorkerProductionStateStore.Clear(); }
    }

    private static unsafe void TestFearHotCore(HotConfiguration configuration, IntPtr percent)
    {
        // CD850 reads the signed player fear level at this offset. Include a value
        // above the Vanilla maximum so a stale productivity clamp cannot pass by coincidence.
        const int playerFearRva = 0x379CF04;
        const int unit = WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride;
        var positive = CompileProfiles(FearProfile(-5, ValueModifierContribution.FixedValue(75)),
            FearProfile(3, ValueModifierContribution.FixedValue(25))).Rules;
        configuration.Set(positive, 0, includeBaseTable: false);
        WorkerProductionStateStore.Clear(); WorkerProductionStateStore.Admit(WorkerProductionWorldMode.Legacy);
        configuration.ResetFault(); Marshal.WriteInt32(percent, 50);
        Word(unit + 0x6E6, (int)eChimps.CHIMP_TYPE_FARMER_WHEAT);
        foreach (var scenario in new[] { (Level: -7, CachedProductivity: 150, Yield: 175),
            (Level: 0, CachedProductivity: 130, Yield: 100), (Level: 3, CachedProductivity: 70, Yield: 125) })
        {
            ResetYield(scenario.CachedProductivity, 99, 0);
            Put(playerFearRva + PlayerStride, scenario.Level);
            Check(YieldFunction(1, 100, 0) == scenario.Yield && WorkerProductionStateStore.RowPointer(1)->IsEmpty,
                "The fear table affects Wheat with no fixed flag and creates no native-wire sidecar.");
        }

        var signed = CompileProfiles(FearProfile(1, ValueModifierContribution.FixedValue(-25)),
            FearProfile(2, ValueModifierContribution.FixedValue(-100)),
            FearProfile(-3, ValueModifierContribution.FixedValue(61))).Rules;
        configuration.Set(signed, 0);
        WorkerProductionStateStore.Clear(); WorkerProductionStateStore.Admit(WorkerProductionWorldMode.Extended);
        configuration.ResetFault(); Marshal.WriteInt32(percent, 50);
        var stages = new[] { 50, 40, 61, 20, 10, 0, -25, -100, 0, 0, 0 };
        foreach (int level in Enumerable.Range(-5, 11))
        foreach (int mode in new[] { 0, 99 })
        foreach (int eligible in new[] { 0, 1 })
        foreach (int amount in new[] { 0, 1, 8, 100 })
        foreach (int remainder in new[] { -99, 0, 99 })
        {
            ResetYield(100 - 10 * level, mode, remainder);
            Put(playerFearRva + PlayerStride, level);
            int points = stages[level + 5] + (mode != 0 && eligible != 0 ? 50 : 0);
            int fractional = points * amount + remainder, extra = fractional / 100;
            Check(YieldFunction(1, amount, eligible) == amount + extra &&
                Word(ExtraRva + UnitStride) == extra && Word(RemainderRva + UnitStride) == fractional - extra * 100 &&
                Marshal.ReadInt32(configuration.Fault) == 0,
                "The executed native body combines the signed fear stage, optional fixed bonus and original fractional words.");
        }

        Put(playerFearRva + PlayerStride, 2);
        foreach (var scenario in new[] {
            (Kind: eChimps.CHIMP_TYPE_MILLER, State: 0x5006D, Yield: 8),
            (Kind: eChimps.CHIMP_TYPE_BAKER, State: 0x00008, Yield: 8),
            (Kind: eChimps.CHIMP_TYPE_BAKER, State: 0x5006D, Yield: 0),
            (Kind: eChimps.CHIMP_TYPE_PEASANT, State: 0x5006D, Yield: 8) })
        {
            ResetYield(80, 0, 0);
            Word(unit + 0x6E6, (int)scenario.Kind); Put(unit + 0x918, scenario.State);
            Check(YieldFunction(1, 8, 0) == scenario.Yield,
                "Fear changes actual output while preserving shared ingredient-pickup and unaudited producer calls.");
        }

        var combinedPenalty = CompileProfiles(FearProfile(1, ValueModifierContribution.FixedValue(-25),
            ValueModifierContribution.FixedValue(-75))).Rules;
        configuration.Set(combinedPenalty, 0); Marshal.WriteInt32(percent, combinedPenalty.FixedBonusPercent);
        Word(unit + 0x6E6, (int)eChimps.CHIMP_TYPE_WOODCUTTER);
        ResetYield(90, 99, 33); Put(playerFearRva + PlayerStride, 1);
        Check(YieldFunction(1, 8, 1) == 1 && Word(RemainderRva + UnitStride) == -67,
            "Combined minus one hundred preserves the last whole item from a previous positive credit.");
        byte[] savedPenalty = Read(0x67E8A58 + UnitStride, UnitStride);
        var uninterrupted = Enumerable.Range(0, 4).Select(_ => YieldFunction(1, 8, 1)).ToArray();
        Copy(0x67E8A58 + UnitStride, savedPenalty);
        Check(uninterrupted.All(amount => amount == 0) &&
            Enumerable.Range(0, 4).Select(_ => YieldFunction(1, 8, 1)).SequenceEqual(uninterrupted),
            "Native signed carry resumes the same production sequence after restoring the record bytes.");

        configuration.Set(signed, 0); Marshal.WriteInt32(percent, signed.FixedBonusPercent);

        // Pre redirects native calculation to owner 2 but the physical Ox remains unit 1.
        // Its reduced cargo needs a row because the native negative Extra is written to unit 2.
        Marshal.WriteInt32(configuration.OriginalUnit, 1);
        ResetYield(130, 0, 0, 1, 1); Put(playerFearRva + PlayerStride, -3);
        ResetYield(90, 0, 0, 2, 2); Put(playerFearRva + 2 * PlayerStride, 1);
        Word(unit + 0x6E6, (int)eChimps.CHIMP_TYPE_QUARRY_OX); Put(unit + 0x6F0, 123);
        Word(unit + UnitStride + 0x6E6, (int)eChimps.CHIMP_TYPE_MILLER); Put(unit + UnitStride + 0x6F0, 456);
        Word(ExtraRva + UnitStride, 0);
        int stones = YieldFunction(2, 8, 1);
        var row = *WorkerProductionStateStore.RowPointer(1);
        Check(stones == 6 && Word(ExtraRva + UnitStride) == 0 && Word(ExtraRva + 2 * UnitStride) == -2 &&
            row.GlobalId == 123 && row.ProducerKind == eChimps.CHIMP_TYPE_QUARRY_OX &&
            row.Flags == WorkerProductionBatchFlags.StoneBatch && WorkerProductionStateStore.Validate(in row, out _) &&
            WorkerProductionStateStore.RowPointer(2)->IsEmpty,
            "Pre redirects the fear and remainder owner while reduced-stone provenance stays on the original Ox.");

        // A later ingredient lookup can redirect its bookkeeping onto a previously
        // loaded Ox. The physical cargo and durable provenance still belong to the Ox.
        WorkerProductionStateStore.Clear(); WorkerProductionStateStore.Admit(WorkerProductionWorldMode.Extended);
        Marshal.WriteInt32(configuration.OriginalUnit, 0);
        ResetYield(90, 0, 0); Put(playerFearRva + PlayerStride, 1);
        stones = YieldFunction(1, 8, 1);
        Check(stones == 6 && Word(ExtraRva + UnitStride) == -2,
            "An ordinary Ox production call still stores the native signed extra.");
        Word(unit + WorkerProductionBonusNativeDefinition.CarriedOffset, stones);
        Marshal.WriteInt32(configuration.OriginalUnit, 2);
        Check(YieldFunction(1, 4, 0) == 4 && Word(ExtraRva + UnitStride) == 0 &&
            Word(unit + WorkerProductionBonusNativeDefinition.CarriedOffset) == stones,
            "A later redirected ingredient call can overwrite native bonus history without altering the Ox cargo.");
        row = *WorkerProductionStateStore.RowPointer(1);
        Check(row.GlobalId == 123 && row.Flags == WorkerProductionBatchFlags.StoneBatch &&
            WorkerProductionStateStore.Validate(in row, out _),
            "The Ox retains durable history even after another producer overwrites its native bonus word.");
        byte[] oxRecord = Read(0x67E8A58 + UnitStride, UnitStride);
        TestFearOxBoundaries(configuration, percent, in row, oxRecord);
        Marshal.WriteInt32(configuration.OriginalUnit, 0);
        Word(OwnerRva + UnitStride, 1);
        Put(playerFearRva + PlayerStride, 0); Put(playerFearRva + 2 * PlayerStride, 0);
    }

    private static unsafe void TestFearOxBoundaries(HotConfiguration configuration, IntPtr percent,
        in WorkerProductionStateRow savedRow, byte[] savedRecord)
    {
        const int unit = WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride;
        var signed = CompileProfiles(FearProfile(1, ValueModifierContribution.FixedValue(-25))).Rules;
        var vanilla = CompileProfiles().Rules;
        foreach (var site in WorkerProductionBonusNativeDefinition.Sites.Where(site =>
            site.Kind == WorkerProductionBonusNativeKind.OxDeparture || site.Kind == WorkerProductionBonusNativeKind.Reserve))
        {
            var original = WorkerProductionBonusNativeDefinition.Decode(site.OriginalBytes, Address(site.Rva));
            int branchTarget = checked((int)(original[1].NearBranchTarget - Address(0)));
            using var generated = new MachineHarness(a => WorkerProductionBonusNativeAssembly.Generate(a, original,
                Address(site.Rva + site.Length), site, Address(0), unchecked((ulong)percent.ToInt64()), Address(ModeRva),
                Address(WorkerProductionBonusNativeDefinition.UnitManagerRva), Address(WorkerProductionBonusNativeDefinition.CurrentUnitRva),
                hot: configuration.Addresses), new Dictionary<int, int> { [site.Rva + site.Length] = 1, [branchTarget] = 2 });
            foreach (int scenario in Enumerable.Range(0, 4))
            {
                configuration.Set(scenario == 0 || scenario == 3 ? signed : vanilla, 0);
                WorkerProductionStateStore.Clear(); WorkerProductionStateStore.Admit(WorkerProductionWorldMode.Extended);
                Copy(0x67E8A58 + UnitStride, savedRecord);
                if (scenario == 1 || scenario == 2) *WorkerProductionStateStore.RowPointer(1) = savedRow;
                if (scenario == 2) Put(unit + 0x6F0, 124);
                Marshal.WriteInt32(percent, sizeof(int), scenario == 3 ? 0 : 1);
                bool active = scenario == 0 || scenario == 1;
                Put(ModeRva, 0); Put(WorkerProductionBonusNativeDefinition.CurrentUnitRva, UnitId);
                foreach (int stones in Enumerable.Range(0, 9))
                {
                    Word(unit + WorkerProductionBonusNativeDefinition.CarriedOffset, stones);
                    generated.ResetInput(); PrepareOriginalInputs(generated, site, stones); generated.Run();
                    bool waiting = site.Kind == WorkerProductionBonusNativeKind.Reserve ? active && stones == 0 : stones < (active ? 1 : 8);
                    Check(generated.Marker == (waiting ? 2 : 1),
                        "Stone guards handle live signed plans, restored rows, slot reuse and the unpublished cohort gate.");
                    generated.CheckPreserved();
                }
            }
        }
        Marshal.WriteInt32(percent, sizeof(int), 1);
    }
}
