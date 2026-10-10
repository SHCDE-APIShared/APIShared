using System;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;

namespace APISharedTests
{
    [TestClass]
    public sealed class MarketPriceEventsTests
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint kind);

        [TestMethod]
        public void ReplacementsRollbackAndVetoIsStickyAcrossOwners()
        {
            var events = new InterceptionEvent<MarketPricePreEventArgs, MarketPricePostEventArgs>();
            MarketPricePostEventArgs observed = null;
            events.TryRegister("a", "replace", a => { a.ReplacementTotal = 37; a.SkipOriginalFunction = true; a.State = "a"; },
                a => { observed = a; Assert.AreEqual("a", a.State); }, out _);
            events.TryRegister("b", "broken", a => { a.ReplacementTotal = -100; a.State = "b"; throw new Exception(); },
                a => Assert.IsNull(a.State), out _);
            events.TryRegister("c", "compose", a => { a.SkipOriginalFunction = false; a.ReplacementTotal += 5; }, null, out _);
            var invocation = events.Begin(new MarketPricePreEventArgs(MarketPriceDirection.Sell, new IntPtr(3), 2, 4, 7));
            Assert.IsTrue(invocation.Pre.SkipOriginalFunction);
            Assert.AreEqual(42, invocation.Pre.ReplacementTotal);
            invocation.Complete(state => new MarketPricePostEventArgs(invocation.Pre, invocation.Pre.ReplacementTotal, state));
            Assert.IsTrue(observed.WasSkipped); Assert.AreEqual(42, observed.Result);
            Assert.AreEqual(7, observed.Amount); Assert.AreEqual(4, observed.Good);
        }

        [TestMethod]
        public void ArithmeticPreservesSignedDivisionAndOverflow()
        {
            foreach (int price in new[] { -26, -1, 0, 1, 4, 5, 26, int.MinValue, int.MaxValue })
                foreach (int amount in new[] { -7, 0, 1, 5, int.MaxValue })
                    Assert.AreEqual(unchecked((price / 5) * amount), MarketPriceEvents.CalculateTradeTotal(price, amount));
        }

        [TestMethod]
        public void ProductiveBackendPreservesAbiArithmeticAndExactTrampolineContinuation()
        {
            foreach (byte[] entry in new[] { MarketPriceNativeRuntime.BuyBytes, MarketPriceNativeRuntime.SellBytes })
            {
                // A private executable fixture, never a published runtime hook. Its deliberate teardown
                // is permitted; no game code is modified. Exercise the SAME backend and validator.
                IntPtr copy = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x40);
                IntPtr manager = Marshal.AllocHGlobal(0x181900);
                Assert.AreNotEqual(IntPtr.Zero, copy);
                NativeDetour<MarketPriceNativeRuntime.PriceCall> probe = null;
                MarketPriceNativeRuntime.PriceCall callback = null;
                try
                {
                    Marshal.Copy(entry, 0, copy, entry.Length);
                    MarketPriceNativeRuntime.VerifyBytes(copy, entry);
                    callback = (m, player, good, amount) => probe.Original(m, player, good, amount);
                    var request = new DetourRequest<MarketPriceNativeRuntime.PriceCall> {
                        Name = "APIShared native market private fixture", TargetAddress = unchecked((ulong)copy.ToInt64()), Callback = callback };
                    probe = MarketPriceNativeRuntime.Backend.CreateDetour(in request) as NativeDetour<MarketPriceNativeRuntime.PriceCall>;
                    Assert.IsNotNull(probe); Assert.IsFalse(probe.IsInstalled);
                    Assert.AreEqual(DetourScheme.Indirect, probe.Scheme); Assert.AreEqual(10, probe.DisplacedByteCount);
                    probe.Enable();
                    MarketPriceNativeRuntime.Validate(probe, copy, entry);
                    var call = (MarketPriceNativeRuntime.PriceCall)Marshal.GetDelegateForFunctionPointer(copy, typeof(MarketPriceNativeRuntime.PriceCall));
                    int table = entry[6] == 0xB8 ? 0x1817B8 : 0x1817BC;
                    foreach (int good in new[] { 0, 1, 17 })
                        foreach (int price in new[] { -26, 0, 26, int.MinValue, int.MaxValue })
                            foreach (int amount in new[] { -3, 0, 1, 7, int.MaxValue })
                            {
                                Marshal.WriteInt32(manager, table + good * 8, price);
                                Assert.AreEqual(MarketPriceEvents.CalculateTradeTotal(price, amount), call(manager, 8, good, amount));
                            }
                    Marshal.WriteByte(copy, 10, 0x90);
                    Assert.ThrowsExactly<InvalidOperationException>(() => MarketPriceNativeRuntime.Validate(probe, copy, entry));
                    Marshal.WriteByte(copy, 10, entry[10]);
                }
                finally
                {
                    probe?.Dispose();
                    MarketPriceNativeRuntime.VerifyBytes(copy, entry);
                    VirtualFree(copy, UIntPtr.Zero, 0x8000); Marshal.FreeHGlobal(manager); GC.KeepAlive(callback);
                }
            }
        }
    }
}
