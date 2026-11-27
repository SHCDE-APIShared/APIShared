using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Modifiers;
using Iced.Intel;
using RedBird.X64.Extensions;
using RedBird.X64.Hooks;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

internal static partial class Program
{
    private static unsafe void TestMillerOutput()
    {
        const int epilog = 0x138823, dropEntry = 0x138248;
        int units = WorkerProductionBonusNativeDefinition.UnitManagerRva;
        int unit = units + UnitStride;
        using var configuration = new HotConfiguration();
        var restored = new List<(int Rva, byte[] Bytes)>();
        var installed = new List<X64InlineHook>();
        IntPtr counters = Marshal.AllocHGlobal(64), percent = Marshal.AllocHGlobal(8);
        try
        {
            for (int i = 0; i < 64; i += 8) Marshal.WriteInt64(counters, i, 0);
            Marshal.WriteInt32(percent, 1000); Marshal.WriteInt32(percent, 4, 1);
            void Boundary(int rva, Action<Assembler> emit)
            {
                using var code = new Code(emit);
                var jump = new Assembler(64); jump.AddUnrestrictedJmp((ulong)code.Address.ToInt64());
                using var stream = new System.IO.MemoryStream(); jump.Assemble(new StreamCodeWriter(stream), Address(rva));
                restored.Add((rva, Read(rva, stream.ToArray().Length))); Copy(rva, stream.ToArray());
            }
            // Real native instructions compute the inventory mutation's quantity. Only
            // the engine's external inventory operation is replaced by this recorder.
            Boundary(0xB6100, a => {
                a.push(r11); a.mov(r11, (ulong)counters.ToInt64()); a.inc(__dword_ptr[r11]);
                a.mov(eax, __dword_ptr[rsp + 0x30]); a.add(__dword_ptr[r11 + 4], eax); // fifth arg = one
                a.mov(eax, __dword_ptr[rsp + 0x40]); a.add(__dword_ptr[r11 + 8], eax); // seventh arg = one
                a.mov(__dword_ptr[r11 + 12], r9d); a.pop(r11); a.ret();
            });
            Boundary(0x2960, a => a.ret()); // visual delivery marker, no production state
            Boundary(0xB9280, a => {
                a.push(r11); a.mov(r11, (ulong)counters.ToInt64()); a.inc(__dword_ptr[r11 + 16]);
                a.mov(__dword_ptr[r11 + 20], edx); a.pop(r11); a.xor(eax, eax); a.ret();
            });
            Boundary(0xC90E0, a => { a.xor(eax, eax); a.ret(); }); // no next wheat building
            Boundary(0x19B930, a => { a.mov(eax, 1); a.ret(); }); // valid workplace remains
            Boundary(0x193A70, a => {
                a.push(r11); a.mov(r11, (ulong)counters.ToInt64()); a.inc(__dword_ptr[r11 + 24]);
                a.mov(__dword_ptr[r11 + 28], ecx); a.mov(__dword_ptr[r11 + 32], edx); a.pop(r11); a.ret();
            });

            foreach (var definition in WorkerProductionOutputNativeDefinition.Sites.Where(s => s.Kind == WorkerProductionOutputSiteKind.MillerPickup ||
                s.Kind == WorkerProductionOutputSiteKind.MillerDeposit || s.Kind == WorkerProductionOutputSiteKind.MillerFinish))
            {
                var site = definition.Site; var hook = new X64InlineHook(Address(site.Rva), site.Length);
                hook.Generate((a, original, back) => WorkerProductionOutputNativeAssembly.Generate(a, original, back,
                    definition, (ulong)image.ToInt64(), (ulong)percent.ToInt64(), configuration.Addresses));
                hook.Enable(); installed.Add(hook);
            }
            var prepare = WorkerProductionOutputNativeDefinition.Sites.Single(s => s.Kind == WorkerProductionOutputSiteKind.MillerPrepare);
            var prepareOriginal = WorkerProductionBonusNativeDefinition.Decode(prepare.Site.OriginalBytes, Address(prepare.Site.Rva));
            var pickup = WorkerProductionOutputNativeDefinition.Sites.Single(s => s.Kind == WorkerProductionOutputSiteKind.MillerPickup);

            void Setup(MachineHarness harness)
            {
                harness.ResetInput(); harness.Set(3, UnitStride); harness.Set(1, UnitStride); harness.Set(7, 1);
                harness.Set(12, Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva));
                harness.Set(13, 1); harness.Set(14, 32); harness.Set(15, Address(units)); harness.Set(6, 0); harness.Set(5, 20);
            }
            int RunPrepare()
            {
                using var harness = new MachineHarness(a => WorkerProductionOutputNativeAssembly.Generate(a, prepareOriginal,
                    Address(prepare.Site.Rva + prepare.Site.Length), prepare, (ulong)image.ToInt64(), (ulong)percent.ToInt64(), configuration.Addresses),
                    new Dictionary<int, int> { [prepare.Site.Rva + prepare.Site.Length] = 1, [epilog] = 2 });
                Setup(harness); harness.Run(); return harness.Marker;
            }
            int RunTrip(bool pickupTrip)
            {
                using var harness = new MachineHarness(a => a.AddUnrestrictedJmp(Address(pickupTrip ? pickup.Site.Rva : dropEntry)),
                    new Dictionary<int, int> { [epilog] = pickupTrip ? 3 : 4 });
                Setup(harness); harness.Run(); return harness.Marker;
            }
            void NewBatchRules(decimal factor, bool fixedValue)
            {
                WorkerProductionStateStore.Clear(); WorkerProductionStateStore.Admit(WorkerProductionWorldMode.Extended);
                var rule = fixedValue ? ValueModifierContribution.FixedValue(factor) : ValueModifierContribution.Factor(factor);
                var compiled = CompileProfiles(BaseProfile(rule, worker: eChimps.CHIMP_TYPE_MILLER));
                Check(compiled.Rules != null, "Miller actual-one rule compiles.");
                configuration.Set(compiled.Rules, 1UL << 18); configuration.ResetFault();
                for (int n = 0x650; n < 0xAE0; n += 8) Marshal.WriteInt64(image + unit + n, 0);
                Word(unit + 0x6E4, 2); Word(unit + 0x6E6, (int)eChimps.CHIMP_TYPE_MILLER);
                Put(unit + 0x6F0, 123); Word(unit + 0x6EE, 1); Word(unit + 0x990, 3);
                Put(WorkerProductionBonusNativeDefinition.CurrentUnitRva, 1); Put(ModeRva, 99);
            }

            NewBatchRules(3, true);
            int prepareMarker = RunPrepare();
            Check(prepareMarker == 1 && Word(unit + 0x9E0) == 3, "Miller prepares actual flour3 independently of fixed bonus1000.");
            var first = *WorkerProductionStateStore.RowPointer(1);
            RunPrepare();
            Check(Word(unit + 0x9E0) == 3 && WorkerProductionStateStore.RowPointer(1)->Numerator == first.Numerator,
                "Repeated storage admission computes this flour batch only once.");
            int pickupMarker = RunTrip(true);
            Check(pickupMarker == 3 && Word(unit + 0x9E0) == 3, "Actual native pickup no longer overwrites coordinated cargo with one.");
            for (int remaining = 2; remaining >= 0; remaining--)
            {
                int dropMarker = RunTrip(false);
                Check(dropMarker == 4 && Word(unit + 0x9E0) == remaining, "Actual native deposit decrements one whole unit per successful trip.");
            }
            Check(Marshal.ReadInt32(counters) == 3 && Marshal.ReadInt32(counters, 4) == 3 && Marshal.ReadInt32(counters, 8) == 3 &&
                Marshal.ReadInt32(counters, 12) == 16, "Three native AddGood invocations retain original flour type and quantity-one arguments.");
            Check(WorkerProductionStateStore.RowPointer(1)->IsEmpty, "Actual finish clears one-shot batch provenance after the last flour item.");
            Check(Marshal.ReadInt32(counters, 24) == 1 && Marshal.ReadInt32(counters, 28) == 1 && Marshal.ReadInt32(counters, 32) == 1,
                "Repeated storage and three deposit trips finish and account exactly one manufacturing cycle.");

            NewBatchRules(0.25m, false);
            int before = Marshal.ReadInt32(counters);
            for (int cycle = 1; cycle <= 4; cycle++)
            {
                prepareMarker = RunPrepare();
                if (cycle < 4)
                {
                    Check(prepareMarker == 2 && Word(unit + 0x9E0) == 0, "Fractional zero flour executes the complete real completion footer.");
                    Check(Marshal.ReadInt32(counters, 20) == 9, "Zero flour skips output storage and only searches next wheat through the real footer.");
                    var row = *WorkerProductionStateStore.RowPointer(1);
                    Check(row.Flags == 0 && row.Numerator * 4 == (ulong)cycle * row.Denominator,
                        "Zero-batch completion retains exact quarter credits without retaining one-shot flags.");
                }
                else
                {
                    Check(prepareMarker == 1 && Word(unit + 0x9E0) == 1, "Fourth quarter-flour cycle produces exactly one.");
                    RunTrip(true); RunTrip(false);
                }
            }
            Check(Marshal.ReadInt32(counters) == before + 1 && WorkerProductionStateStore.RowPointer(1)->IsEmpty,
                "Four actual manufacturing/finish cycles deliver exactly one flour and leave no fractional debt.");
            Check(Marshal.ReadInt32(counters, 24) == 5, "Every zero-flour cycle executes work accounting exactly once through the real footer.");
        }
        finally
        {
            foreach (var hook in installed.AsEnumerable().Reverse()) hook.Dispose(); // private fixture only
            foreach (var entry in restored) Copy(entry.Rva, entry.Bytes);
            Marshal.FreeHGlobal(counters); Marshal.FreeHGlobal(percent); WorkerProductionStateStore.Clear();
        }
    }
}
