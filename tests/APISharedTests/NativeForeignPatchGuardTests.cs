using APIShared;
using BepInEx.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

// Runs NativeHookPublication with the installed RedBird on a private executable page whose copy stands
// in for the Script Extender's load snapshot. Inputs: clean spans, a foreign jmp rel32 inside or directly
// behind a span, and a later overwrite of a published span. Observable results: whether bytes were written,
// whether publication began (so the owner's RollbackUnpublished still disposes the candidate), the Error log
// and the recheck count. The guard's process binding is restored after every test; the page stays allocated
// because RedBird stubs may point into it until the process exits.
[TestClass]
[DoNotParallelize]
public sealed class NativeForeignPatchGuardTests
{
    private const int PageSize = 4096;
    // mov [rsp+8],rbx: a 14-byte inline hook over these 5-byte instructions displaces 15 bytes.
    private static readonly byte[] Move = { 0x48, 0x89, 0x5C, 0x24, 0x08 };
    // mov [rsp+8],rbx; mov [rsp+16],rsi; push rdi; sub rsp,32; xor eax,eax; add rsp,32; pop rdi; ret
    private static readonly byte[] Function =
    {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x20,
        0x33, 0xC0, 0x48, 0x83, 0xC4, 0x20, 0x5F, 0xC3
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int NoArguments();
    private static readonly NoArguments Callback = () => 0;

    private IntPtr page;
    private ulong image;
    private ScanRegion region;
    private readonly List<string> errors = new List<string>();
    private readonly List<string> infos = new List<string>();
    private Dictionary<FieldInfo, object> savedGuard;

    [TestInitialize]
    public void Initialize()
    {
        page = VirtualAlloc(IntPtr.Zero, (UIntPtr)PageSize, 0x3000, 0x40);
        Assert.AreNotEqual(IntPtr.Zero, page, "VirtualAlloc failed.");
        image = unchecked((ulong)page.ToInt64());
        for (int site = 0; site < 4; site++)
            for (int index = 0; index < 6; index++)
                Marshal.Copy(Move, 0, page + site * 0x100 + index * Move.Length, Move.Length);
        Marshal.Copy(Function, 0, page + 0x600, Function.Length);
        var snapshot = new byte[PageSize];
        Marshal.Copy(page, snapshot, 0, PageSize);
        region = ScanRegion.FromMemory(snapshot, image, "foreign patch guard fixture");
        savedGuard = typeof(NativeForeignPatchGuard).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => !field.IsInitOnly && !field.IsLiteral).ToDictionary(field => field, field => field.GetValue(null));
        // A fresh source per test: RuntimeHealth folds identical errors per source.
        var log = new ManualLogSource("NativeForeignPatchGuardTests." + Guid.NewGuid().ToString("N"));
        log.LogEvent += (sender, args) =>
        {
            if ((args.Level & LogLevel.Error) != 0) errors.Add(Convert.ToString(args.Data));
            else if ((args.Level & LogLevel.Info) != 0) infos.Add(Convert.ToString(args.Data));
        };
        NativeForeignPatchGuard.Bind(region, log);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var field in savedGuard) field.Key.SetValue(null, field.Value);
    }

    [TestMethod]
    public void CleanSpanPublishesAndIsWatched()
    {
        var handle = new HookHandle<X64InlineHook>();
        HookTransaction transaction = Inline(image, handle);
        NativeHookPublication.Enable(transaction, transaction.Commit(), new NativeHookPublicationState(), this, null);
        Assert.IsTrue(handle.IsInstalled, "A clean inline span was not published.");
        Assert.IsTrue(Marshal.ReadByte(page) == 0xFF && Marshal.ReadByte(page + 1) == 0x25, "The inline jump was not written.");
        Assert.AreEqual(1, NativeForeignPatchGuard.WatchedCount, "The published span is not watched.");
        Assert.AreEqual(0, NativeForeignPatchGuard.Recheck(), "An untouched published span was reported.");
        Assert.AreEqual(0, errors.Count, string.Join(Environment.NewLine, errors));

        // Generic NativeDetour<T> spans use their displaced prologue.
        var detour = new DetourHandle<NoArguments>();
        var detourTransaction = new HookTransaction(region, null, Options());
        detourTransaction.AddDetour(detour, HookTarget.FromAddress(image + 0x600), Callback);
        NativeHookPublication.Enable(detourTransaction, detourTransaction.Commit());
        Assert.IsTrue(detour.IsInstalled && NativeForeignPatchGuard.WatchedCount == 2, "A clean detour was not published and watched.");
    }

    [TestMethod]
    public void ForeignJumpInsideSpanRefusesBeforePublicationBegins()
    {
        IntPtr occupied = page + 0x100;
        WriteJump(occupied + 5);
        byte[] before = Read(occupied, 15);
        var handle = new HookHandle<X64InlineHook>();
        HookTransaction transaction = Inline(image + 0x100, handle);
        CommitResult result = transaction.Commit();
        var publication = new NativeHookPublicationState();
        bool validated = false;
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
            NativeHookPublication.Enable(transaction, result, publication, this, () => validated = true));
        StringAssert.Contains(failure.Message, "NATIVE_FOREIGN_PATCH:");
        Assert.IsFalse(publication.HasStarted, "A refused candidate began publication and can no longer be rolled back.");
        Assert.IsTrue(!handle.IsInstalled && !validated && before.SequenceEqual(Read(occupied, 15)), "The occupied span was written.");
        Assert.IsTrue(errors.Count == 1 && errors[0].Contains("Jump at +5") && errors[0].Contains("RVA 0x100"),
            "The refusal did not log one Error with RVA and jump hint: " + string.Join(Environment.NewLine, errors));
        bool released = false;
        publication.RollbackUnpublished(transaction, () => released = true);
        Assert.IsTrue(released, "The owner could not roll back the refused candidate.");
        Assert.AreEqual(0, NativeForeignPatchGuard.WatchedCount);
    }

    [TestMethod]
    public void ForeignJumpDirectlyBehindSpanIsAllowed()
    {
        WriteJump(page + 0x200 + 15);
        var handle = new HookHandle<X64InlineHook>();
        HookTransaction transaction = Inline(image + 0x200, handle);
        NativeHookPublication.Enable(transaction, transaction.Commit(), new NativeHookPublicationState(), this, null);
        Assert.IsTrue(handle.IsInstalled && errors.Count == 0, "An adjacent foreign jump blocked the span: " + string.Join(Environment.NewLine, errors));
    }

    [TestMethod]
    public void LaterOverwriteIsReportedOncePerChange()
    {
        var handle = new HookHandle<X64InlineHook>();
        HookTransaction transaction = Inline(image + 0x300, handle);
        NativeHookPublication.Enable(transaction, transaction.Commit(), new NativeHookPublicationState(), this, null);
        IntPtr site = page + 0x300;
        byte[] published = Read(site, 15);
        WriteJump(site);
        Assert.AreEqual(1, NativeForeignPatchGuard.Recheck(), "A later overwrite was not reported.");
        Assert.IsTrue(errors.Count == 1 && errors[0].Contains("NATIVE_FOREIGN_PATCH_LATE"), string.Join(Environment.NewLine, errors));
        Assert.AreEqual(0, NativeForeignPatchGuard.Recheck(), "The same overwrite was reported twice.");
        Assert.AreEqual(1, errors.Count);
        Assert.IsTrue(Read(site, 5).SequenceEqual(new byte[] { 0xE9, 0xFB, 0xFF, 0xFF, 0xFF }), "Recheck rewrote the foreign patch.");
        Marshal.Copy(published, 0, site, published.Length);
        Assert.AreEqual(0, NativeForeignPatchGuard.Recheck(), "A restored span was reported.");
        Marshal.WriteByte(site, 0xCC);
        Assert.AreEqual(1, NativeForeignPatchGuard.Recheck(), "A new deviation after a restore was not reported.");
        Marshal.Copy(published, 0, site, published.Length);
    }

    private static HookTransactionOptions Options() => new HookTransactionOptions
    {
        InstallOnCommit = false,
        Backend = NativeDetourBackend.Instance,
        FailureMode = TransactionFailureMode.RollbackAndThrow,
        OwnsHooks = true
    };

    private HookTransaction Inline(ulong address, HookHandle<X64InlineHook> handle)
    {
        var transaction = new HookTransaction(region, null, Options());
        transaction.AddInline(handle, HookTarget.FromAddress(address), (assembler, original, back) => { }, 14);
        return transaction;
    }

    // jmp rel32 to itself: a foreign hook entry at an instruction boundary.
    private static void WriteJump(IntPtr address) => Marshal.Copy(new byte[] { 0xE9, 0xFB, 0xFF, 0xFF, 0xFF }, 0, address, 5);

    private static byte[] Read(IntPtr address, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(address, bytes, 0, length);
        return bytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);
}
