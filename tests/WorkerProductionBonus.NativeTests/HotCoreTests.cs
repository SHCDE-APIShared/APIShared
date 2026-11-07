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
        internal readonly IntPtr Slot = Marshal.AllocHGlobal(8), Header = Marshal.AllocHGlobal(24),
            Fault = Marshal.AllocHGlobal(32), Ai = Marshal.AllocHGlobal(36), OriginalUnit = Marshal.AllocHGlobal(4);
        internal IntPtr Table;
        internal WorkerProductionHotAddresses Addresses => new WorkerProductionHotAddresses(
            (ulong)Slot.ToInt64(), (ulong)WorkerProductionStateStore.RowsAddress.ToInt64(),
            (ulong)WorkerProductionStateStore.ControlAddress.ToInt64(), (ulong)Fault.ToInt64(), (ulong)Ai.ToInt64(), (ulong)OriginalUnit.ToInt64());
        internal HotConfiguration()
        {
            Marshal.WriteInt64(Slot, Header.ToInt64());
            Marshal.WriteInt32(OriginalUnit, 0);
            for (int n = 0; n < 36; n += 4) Marshal.WriteInt32(Ai, n, 0);
            ResetFault();
        }
        internal void ResetFault()
        { for (int n = 0; n < 32; n += 4) Marshal.WriteInt32(Fault, n, 0); Marshal.WriteInt32(Fault, 24, 1); }
        internal void Set(WorkerProductionCompiledRules rules, ulong mask)
        {
            if (Table != IntPtr.Zero) Marshal.FreeHGlobal(Table);
            Table = Marshal.AllocHGlobal(rules.Entries.Count * 24);
            for (int n = 0; n < rules.Entries.Count; n++)
            {
                var entry = rules.Entries[n]; Marshal.WriteInt64(Table, n * 24, entry.Whole);
                Marshal.WriteInt64(Table, n * 24 + 8, (long)entry.Numerator);
                Marshal.WriteInt64(Table, n * 24 + 16, (long)entry.Denominator);
            }
            Marshal.WriteInt64(Header, 0, (long)rules.CommonDenominator); Marshal.WriteInt64(Header, 8, Table.ToInt64());
            Marshal.WriteInt64(Header, 16, unchecked((long)mask));
        }
        public void Dispose()
        { if (Table != IntPtr.Zero) Marshal.FreeHGlobal(Table); Marshal.FreeHGlobal(Slot); Marshal.FreeHGlobal(Header); Marshal.FreeHGlobal(Fault); Marshal.FreeHGlobal(Ai); Marshal.FreeHGlobal(OriginalUnit); }
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
}
