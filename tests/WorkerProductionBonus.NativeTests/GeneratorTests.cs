using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using Iced.Intel;
using RedBird.X64.Hooks;

internal static partial class Program
{
    private static partial void TestProductionGenerators()
    {
        IntPtr percent = Marshal.AllocHGlobal(2 * sizeof(int));
        try
        {
            Marshal.WriteInt32(percent, sizeof(int), 1);
            foreach (var site in WorkerProductionBonusNativeDefinition.Sites)
            {
                Check(At(site.Rva, site.Length).SequenceEqual(site.OriginalBytes), "Actual native fingerprint: " + site.Name);
                using var probe = new X64InlineHook(Address(site.Rva), site.Length);
                Check(probe.DisplacedByteCount == site.Length, "Installed RedBird complete-instruction span: " + site.Name);
                probe.Generate((assembler, original, back) => Generate(assembler, original, back, site, percent));
                byte[] expected = WorkerProductionBonusNativeAssembly.ExpectedStub(site, unchecked((ulong)image.ToInt64()),
                    unchecked((ulong)probe.StubAddress.ToInt64()), unchecked((ulong)percent.ToInt64()), Address(ModeRva),
                    Address(WorkerProductionBonusNativeDefinition.UnitManagerRva), Address(WorkerProductionBonusNativeDefinition.CurrentUnitRva));
                var actual = new byte[expected.Length];
                Marshal.Copy(probe.StubAddress, actual, 0, actual.Length);
                Check(actual.SequenceEqual(expected), "Installed backend emitted the validated production generator: " + site.Name);
            }
            TestCoreGenerator(percent);
            TestBoundaryGenerators(percent);
            TestActualNativeFooters(percent);
        }
        finally { Marshal.FreeHGlobal(percent); }
    }

    private static void Generate(Assembler assembler, ReadOnlySpan<Instruction> original, ulong back,
        WorkerProductionBonusNativeSite site, IntPtr percent) =>
        WorkerProductionBonusNativeAssembly.Generate(assembler, original, back, site, unchecked((ulong)image.ToInt64()),
            unchecked((ulong)percent.ToInt64()), Address(ModeRva), Address(WorkerProductionBonusNativeDefinition.UnitManagerRva),
            Address(WorkerProductionBonusNativeDefinition.CurrentUnitRva));

    private static void TestCoreGenerator(IntPtr percent)
    {
        var site = WorkerProductionBonusNativeDefinition.Sites.Single(s => s.Kind == WorkerProductionBonusNativeKind.Core);
        using var hook = new X64InlineHook(Address(site.Rva), site.Length);
        hook.Generate((assembler, original, back) => Generate(assembler, original, back, site, percent));
        hook.Enable();
        Check(hook.IsInstalled, "Actual native yield core generator installed in private image.");
        foreach (int value in new[] { -100, -99, -50, -1, 0, 25, 50, 100, 1000 })
        {
            Marshal.WriteInt32(percent, value);
            foreach (int productivity in new[] { 50, 100, 110, 150 })
            foreach (int mode in new[] { 0, 1, 2, 99 })
            foreach (int eligible in new[] { 0, 1 })
            foreach (int amount in new[] { 1, 2, 3, 6, 8, 12, 100 })
            foreach (int remainder in new[] { -99, -50, 0, 50, 99 })
            {
                ResetYield(productivity, mode, remainder);
                var expected = ReferenceYield(productivity, mode, eligible, amount, remainder, value);
                int result = YieldFunction(UnitId, amount, eligible);
                Check(result == expected.Amount && result >= 0, "Production core result/bounds.");
                Check(Word(RemainderRva + UnitId * UnitStride) == expected.Remainder, "Production core signed carry.");
                Check(Word(ExtraRva + UnitId * UnitStride) == expected.Extra, "Production core signed extra.");
            }
        }
        Marshal.WriteInt32(percent, -50);
        ResetYield(100, 99, 0);
        Check(Enumerable.Range(0, 10).Select(_ => YieldFunction(UnitId, 1, 1)).SequenceEqual(new[] { 1, 0, 1, 0, 1, 0, 1, 0, 1, 0 }),
            "Negative half output alternates complete items and zero without per-cycle rounding.");
        Marshal.WriteInt32(percent, -1);
        ResetYield(100, 99, 0);
        int total = Enumerable.Range(0, 100).Sum(_ => YieldFunction(UnitId, 1, 1));
        Check(total == 99 && Word(RemainderRva + UnitId * UnitStride) == 0, "One-percent reduction persists until a complete lost item.");
        Marshal.WriteInt32(percent, -100);
        ResetYield(100, 99, 99);
        Check(YieldFunction(UnitId, 1, 1) == 1 && YieldFunction(UnitId, 1, 1) == 0,
            "A preexisting positive fractional credit survives changing to minus one hundred.");
        // Raw record persistence must also resume a negative configured sequence, not
        // only a preexisting negative word under the unmodified Vanilla coefficient.
        Marshal.WriteInt32(percent, -1);
        ResetYield(100, 99, 0);
        for (int index = 0; index < 10; index++) YieldFunction(UnitId, 1, 1);
        const int origin = 0x67E8A58 + UnitId * UnitStride;
        byte[] save = Read(origin, UnitStride);
        int[] uninterrupted = Enumerable.Range(0, 100).Select(_ => YieldFunction(UnitId, 1, 1)).ToArray();
        Copy(origin, save);
        Check(Enumerable.Range(0, 100).Select(_ => YieldFunction(UnitId, 1, 1)).SequenceEqual(uninterrupted),
            "Negative configured production resumes the identical sequence after raw save roundtrip.");
        // The hook is private and unpublished. Dispose restores this private body before
        // later fixtures; production hooks intentionally have no runtime uninstall path.
    }

    private static partial void TestBoundaryGenerators(IntPtr percent);
}
