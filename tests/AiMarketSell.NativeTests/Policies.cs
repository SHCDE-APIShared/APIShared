using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using APIShared.Economy;
using Iced.Intel;

namespace APIShared.Economy
{
    // Only image acquisition is replaced. Tests compile the production broker and machine generator.
    internal static class AiMarketSellNativeRuntime
    {
        internal static void EnsureInstalled() { }
        internal static void PublishDemand(int demand) { }
    }
}
internal static partial class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Adjust(int player, int good, int stock, int original);
    private static AiMarketSellRegistry callbackRegistry;
    private static readonly Adjust reserveCallback = (slot, good, stock, original) => callbackRegistry == null ? original :
        callbackRegistry.Reserve(slot / 0x160F, good, stock, original);
    private static readonly Adjust quantityCallback = (player, good, stock, original) => callbackRegistry == null ? original :
        callbackRegistry.Quantity(player, good, stock, original);
    private static void Generate(Assembler a, ReadOnlySpan<Instruction> original, ulong back, int index, ulong image, ulong state) =>
        AiMarketSellAssembly.Generate(a, original, back, index, image, state,
            (ulong)Marshal.GetFunctionPointerForDelegate(reserveCallback).ToInt64(),
            (ulong)Marshal.GetFunctionPointerForDelegate(quantityCallback).ToInt64());

    private static void TestPolicies()
    {
        int flags = -1, installs = 0;
        var registry = new AiMarketSellRegistry(() => installs++, value => flags = value);
        Check(registry.TryRegister("b", "one", (AiMarketSellSafety)3, out var first, out _), "independent owner registration");
        Check(!first.IsEnabled && flags == -1, "registration initially dormant");
        Check(!registry.TryRegister("b", "one", 0, out _, out _), "duplicate refused");
        Check(!registry.TryRegister("", "id", 0, out _, out _) && !registry.TryRegister("a", "id", (AiMarketSellSafety)4, out _, out _), "invalid identity/flags refused");
        Check(registry.TryRegister("a", "two", AiMarketSellSafety.LimitQuantityToStock, out var second, out _), "second independent owner");
        first.SetEnabled(true); second.SetEnabled(true); Check(flags == 3, "OR of active constraints");
        first.SetEnabled(false); Check(flags == 2 && second.IsEnabled, "one owner cannot withdraw another's constraint");
        second.SetEnabled(false); Check(flags == 0, "all inactive complete Vanilla demand");
        var calls = new List<string>(); AiMarketSellReserveContext retained = null;
        registry.TryRegister("z", "late", 0, out var late, out _, c => { calls.Add("z"); c.Reserve += 10; }, c => c.Amount += 1, 1);
        registry.TryRegister("b", "early", 0, out var bad, out _, c => { calls.Add("b"); c.Reserve = 999; throw new Exception(); },
            c => { c.Amount = 999; throw new Exception(); });
        registry.TryRegister("a", "early", 0, out var early, out _, c => {
            calls.Add("a"); retained = c; Check(c.PlayerId == 1 && c.Good == 14 && c.Stock == 3 && c.OriginalReserve == -999, "validated immutable reserve context");
            Check(registry.Reserve(1, 14, 3, 42) == 42, "recursive dispatch bypasses notifications"); c.Reserve = 4;
        }, c => c.Amount = 2);
        late.SetEnabled(true); bad.SetEnabled(true); early.SetEnabled(true);
        Check(flags == 12 && registry.Reserve(1,14,3,-999) == 14 && string.Join("", calls) == "abz", "ordered callbacks with isolated rollback");
        Check(registry.Quantity(1,14,3,9) == 3 && bad.CallbackErrorCount == 2, "quantity rollback and later callback continues");
        bool closed = false; try { retained.Reserve = 1; } catch (InvalidOperationException) { closed = true; }
        Check(closed, "retained public context frozen after dispatch");
        Check(registry.Quantity(0,14,3,9) == 9 && registry.Quantity(1,15,3,9) == 9, "invalid context never notified");
        early.SetEnabled(false); bad.SetEnabled(false); late.SetEnabled(false);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) registry.Quantity(1,14,3,9);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "dormant broker allocates nothing");
        var failed = new AiMarketSellRegistry(() => throw new InvalidOperationException("occupied"), _ => throw new Exception());
        Check(!failed.TryRegister("a", "id", 0, out var unavailable, out string reason) && unavailable == null && reason == "occupied", "unavailable native owner isolated");
        Console.WriteLine("PASS: policy ownership, OR, ordering, disabled path, rollback, recursion, retained contexts, no-demand allocations.");
    }
    private static void TestCallbacks(IntPtr image, IntPtr state, Sale sale, Decision decide)
    {
        callbackRegistry = new AiMarketSellRegistry(() => {}, value => Marshal.WriteInt32(state, value));
        callbackRegistry.TryRegister("safety", "native", (AiMarketSellSafety)3, out var safety, out _);
        int reserveCalls = 0, quantityCalls = 0;
        callbackRegistry.TryRegister("other-mod", "adjust", 0, out var adjustment, out _, c => {
            reserveCalls++; Check(c.PlayerId == 1 && c.Good > 0 && c.Good != 15, "native reserve bridge identity");
            c.Reserve = -999;
        }, c => {
            quantityCalls++; Check(c.PlayerId == 1 && c.Good == 14 && c.Stock == 3 && c.OriginalAmount == 99, "native quantity bridge inputs");
            c.Amount = int.MaxValue;
        });
        safety.SetEnabled(true); adjustment.SetEnabled(true);
        Reset(image); SetStock(image,1,14,3); sale(image+Profile,1,14,99);
        Check(quantityCalls == 1 && Get(image,GoldOffset(1)) == 1006 && Get(image,StockOffset(1,14)) == 0,
            "actual reverse P/Invoke callback cannot override final stock bound");
        adjustment.SetEnabled(false);
        callbackRegistry.TryRegister("other-mod", "reserve-only", 0, out var reserve, out _, c => { reserveCalls++; c.Reserve = -999; });
        reserve.SetEnabled(true); Reset(image); SetStock(image,1,14,3); decide(image+Profile,1);
        Check(reserveCalls > 0 && Get(image,GoldOffset(1)) == 1006, "actual reserve bridge preserves later stocked good and floors callback reserve");
        reserve.SetEnabled(false);
        callbackRegistry.TryRegister("other-mod", "cancel", 0, out var cancel, out _, quantity: c => c.Amount = 0);
        cancel.SetEnabled(true); Reset(image); SetStock(image,1,14,3); sale(image+Profile,1,14,2);
        Check(Get(image,GoldOffset(1)) == 1000 && Get(image,StockOffset(1,14)) == 3, "callback cancels price, credit and removal");
        safety.SetEnabled(false); Reset(image); SetStock(image,1,14,3); sale(image+Profile,1,14,2);
        Check(Get(image,GoldOffset(1)) == 1000, "callback-only cancellation works without safety");
        cancel.SetEnabled(false); Reset(image); SetStock(image,1,14,3); sale(image+Profile,1,14,99);
        Check(Get(image,GoldOffset(1)) == 1198, "all owners disabled restores exact Vanilla booking");
        callbackRegistry.TryRegister("other-mod", "recursive", 0, out var recursive, out _, quantity: c => {
            quantityCalls++; sale(image+Profile,1,14,3); c.Amount = 99;
        });
        safety.SetEnabled(true); recursive.SetEnabled(true);
        Reset(image); SetStock(image,1,14,3); int callsBefore = quantityCalls;
        sale(image+Profile,1,14,99);
        Check(quantityCalls == callsBefore + 1 && Get(image,GoldOffset(1)) == 1006 && Get(image,StockOffset(1,14)) == 0,
            "recursive sale bypasses notifications, retains safety and outer sale rechecks consumed stock");
        recursive.SetEnabled(false);
        callbackRegistry.TryRegister("another-owner", "keep-stock-safety", AiMarketSellSafety.LimitQuantityToStock, out var keep, out _);
        keep.SetEnabled(true); safety.SetEnabled(false);
        Reset(image); SetStock(image,1,14,3); sale(image+Profile,1,14,99);
        Check(Get(image,GoldOffset(1)) == 1006, "second native owner retains stock safety after first disables");
        keep.SetEnabled(false);
        // Native fast-path execution cost, not an FPS benchmark. Callback counters stay untouched.
        safety.SetEnabled(true); int before = reserveCalls + quantityCalls;
        Reset(image); var watch = Stopwatch.StartNew(); long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) sale(image+Profile,1,14,1);
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes; watch.Stop();
        Check(before == reserveCalls + quantityCalls && bytes == 0, "safety-only native path has no callback/allocation");
        Console.WriteLine($"PASS: callback bridge executed, final native safety, cancellation, dormant parity; 10000 empty native sales {watch.Elapsed.TotalMilliseconds:F2}ms, {bytes} managed bytes.");
        safety.SetEnabled(false); callbackRegistry = null;
    }
}
