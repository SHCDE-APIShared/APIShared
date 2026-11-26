using System;
using APIShared.Economy;
using APIShared.Modifiers;
using APIShared.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class MarketPriceModifiersTests
    {
        private sealed class Fixture
        {
            internal readonly int[] Values = { 100, 50 };
            internal readonly MarketPriceTableCoordinator Table;
            internal int Writes, Errors;
            internal bool FailAfterMutation;
            internal Fixture()
            {
                Table = new MarketPriceTableCoordinator(1, (g, b) => Values[b ? 0 : 1],
                    (g, b, v) => { Values[b ? 0 : 1] = v; Writes++; if (FailAfterMutation && b) throw new Exception("partial setter"); },
                    (g, b) => b ? 100 : 50);
                Table.Begin(null);
            }
            internal void Refresh(decimal factor, bool applyAI = true)
            {
                Table.Refresh((g, b, ai, baseline, foreign) => foreign.HasValue
                    ? new MarketPriceResolution(foreign.Value, false)
                    : new MarketPriceResolution((int)ValueModifierCalculator.Calculate(baseline,
                        new[] { ValueModifierContribution.Factor(ai && !applyAI ? 1 : factor) }, 0, 32767), factor != 1), _ => Errors++);
            }
        }

        [TestMethod]
        public void BasePricesRemainStableAcrossRefreshDisableForeignWritesAndSessionEnd()
        {
            var f = new Fixture(); f.Refresh(1); Assert.AreEqual(0, f.Writes);
            f.Refresh(1.5m); f.Refresh(1.5m); Assert.AreEqual(150, f.Values[0]); Assert.AreEqual(2, f.Writes);
            f.Refresh(2, false); Assert.AreEqual(200, f.Values[0]); Assert.AreEqual(100, f.Table.AIPrice(0, true));
            Assert.IsTrue(f.Table.HasContextualPrices);
            f.Values[0] = 333; f.Refresh(3); Assert.AreEqual(333, f.Values[0]);
            // Returning to the native default is still an explicit in-session foreign fixed write.
            f.Values[0] = 100; f.Refresh(3); Assert.AreEqual(100, f.Values[0]);
            f.Refresh(1); Assert.AreEqual(100, f.Values[0]); Assert.AreEqual(50, f.Values[1]);
            f.Table.End(_ => f.Errors++); Assert.AreEqual(100, f.Values[0]); Assert.AreEqual(0, f.Errors);
        }

        [TestMethod]
        public void SaveReloadUsesStoredBasesWithCurrentSettingsAndPreservesForeignAbsolutePrices()
        {
            var f = new Fixture(); f.Refresh(2);
            byte[] bytes = f.Table.Save();
            f.Table.Begin(MarketPriceTableCoordinator.Decode(bytes, 1)); f.Refresh(3);
            Assert.AreEqual(300, f.Values[0]); Assert.AreEqual(150, f.Values[1]);
            bytes = f.Table.Save();
            f.Table.Begin(MarketPriceTableCoordinator.Decode(bytes, 1)); f.Refresh(3);
            Assert.AreEqual(300, f.Values[0]);
            f.Values[0] = 123; f.Refresh(2); bytes = f.Table.Save();
            // Mission save/reset paths can restore the native defaults before deserialization.
            f.Values[0] = 100; f.Values[1] = 50;
            f.Table.Begin(MarketPriceTableCoordinator.Decode(bytes, 1)); f.Refresh(5);
            Assert.AreEqual(123, f.Values[0]); Assert.AreEqual(250, f.Values[1]);
            f.Table.End(_ => f.Errors++); Assert.AreEqual(123, f.Values[0]); Assert.AreEqual(50, f.Values[1]);
        }

        [TestMethod]
        public void PartialSetterFailureRetainsOwnershipAndDoesNotPreventOtherCells()
        {
            var f = new Fixture { FailAfterMutation = true }; f.Refresh(2);
            Assert.AreEqual(1, f.Errors); Assert.AreEqual(200, f.Values[0]); Assert.AreEqual(100, f.Values[1]);
            f.FailAfterMutation = false; f.Table.End(_ => f.Errors++);
            Assert.AreEqual(100, f.Values[0]); Assert.AreEqual(50, f.Values[1]);
        }

        [TestMethod]
        public void SavePayloadRejectsTruncationTrailingBytesVersionsAndInvalidFlags()
        {
            byte[] bytes = new Fixture().Table.Save();
            Assert.ThrowsExactly<InvalidOperationException>(() => MarketPriceTableCoordinator.Decode(new byte[bytes.Length - 1], 1));
            Assert.ThrowsExactly<InvalidOperationException>(() => MarketPriceTableCoordinator.Decode(new byte[bytes.Length + 1], 1));
            bytes[0] = 2;
            Assert.ThrowsExactly<InvalidOperationException>(() => MarketPriceTableCoordinator.Decode(bytes, 1));
            bytes[0] = 1; bytes[20] = 2;
            Assert.ThrowsExactly<InvalidOperationException>(() => MarketPriceTableCoordinator.Decode(bytes, 1));
        }

        [TestMethod]
        public void FactorsFixedValuesAfterFactorsCapsAndCallbackIsolationUseTheProductionResolver()
        {
            var before = new MarketPriceModifierHandle("a", "factor", 0, c => c.IsAIPlayer ? ValueModifierContribution.None : ValueModifierContribution.Factor(2));
            var broken = new MarketPriceModifierHandle("b", "broken", 0, c => throw new Exception("isolated"));
            var fixedValue = new MarketPriceModifierHandle("c", "fixed", 0, c => ValueModifierContribution.FixedValue(76));
            var after = new MarketPriceModifierHandle("d", "after", 0, c => ValueModifierContribution.Factor(1.5m, ValueModifierStage.AfterFixedValue));
            Assert.AreEqual(200, MarketPriceModifiers.Resolve(new[] { before, broken }, 0, true, false, 100, null).Price);
            Assert.AreEqual(100, MarketPriceModifiers.Resolve(new[] { before, broken }, 0, true, true, 100, null).Price);
            Assert.AreEqual(114, MarketPriceModifiers.Resolve(new[] { before, fixedValue, after }, 0, true, false, 100, null).Price);
            Assert.AreEqual(123, MarketPriceModifiers.Resolve(new[] { before, fixedValue }, 0, true, true, 100, 123).Price);
            Assert.AreEqual(185, MarketPriceModifiers.Resolve(new[] { before, fixedValue, after }, 0, true, true, 100, 123).Price);
            var huge = new MarketPriceModifierHandle("z", "huge", 0, c => ValueModifierContribution.Factor(decimal.MaxValue));
            Assert.AreEqual(6553, MarketPriceModifiers.Resolve(new[] { huge }, 0, true, false, 100, null).Price);
            Assert.AreEqual(6554, MarketPriceModifiers.Resolve(new[] { huge }, 0, false, false, 50, null).Price);
            // Composition ends at the base lot price: native callers retain their distinct rounding.
            Assert.AreEqual(76, MarketPriceModifiers.Resolve(new[] { fixedValue }, 0, true, false, 100, null).Price);
            Assert.AreEqual(75, MarketPriceEvents.CalculateTradeTotal(76, 5));
        }

        [TestMethod]
        public void LegacyTotalVetoRemainsAuthoritativeWithTheBasePriceRouteEnabled()
        {
            var registry = new InterceptionEvent<MarketPricePreEventArgs, MarketPricePostEventArgs>();
            int originals = 0, observed = 0;
            registry.TryRegister("legacy", "total", pre => { pre.ReplacementTotal = 37; pre.SkipOriginalFunction = true; },
                post => { observed = post.Result; Assert.IsTrue(post.WasSkipped); }, out _);
            int result = MarketPriceEvents.DispatchObserved(registry, MarketPriceDirection.Buy, IntPtr.Zero, 1, 0, 25,
                (manager, player, good, amount) => { originals++; return 999; }, true);
            Assert.AreEqual(37, result); Assert.AreEqual(37, observed); Assert.AreEqual(0, originals);
        }
    }
}
