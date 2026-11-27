using System;
using System.Reflection;
using APIShared.Economy;
using APIShared.Modifiers;
using APIShared.Events;
using SHCDESE.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class MarketPriceModifiersTests
    {
        public TestContext TestContext { get; set; }
        private sealed class Fixture
        {
            internal readonly int[] Values = { 100, 50 };
            internal readonly MarketPriceTableCoordinator Table;
            internal int Writes, Errors, Reads;
            internal bool FailAfterMutation, FailRead;
            internal Fixture()
            {
                Table = new MarketPriceTableCoordinator(1, (g, b) => { Reads++; if (FailRead) throw new Exception("table reader"); return Values[b ? 0 : 1]; },
                    (g, b, v) => { Values[b ? 0 : 1] = v; Writes++; if (FailAfterMutation && b) throw new Exception("partial setter"); },
                    (g, b) => b ? 100 : 50);
                Table.Begin(null);
            }
            internal void RefreshTracked(MarketPriceModifierHandle[] selected) =>
                Table.Refresh((g, b, ai, baseline, foreign, trace) =>
                    MarketPriceModifiers.Resolve(selected, g, b, ai, baseline, foreign, trace), _ => Errors++);
            internal ValueModifierStatus Status(bool buying = true, bool ai = false)
            {
                Assert.IsTrue(Table.TryGetStatus(0, buying, ai, out var status, out string reason), reason);
                return status;
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
        [DoNotParallelize]
        public void PublicQueryReadsTheSelectedMarketCellAndItsSharedComparison()
        {
            var f = new Fixture();
            f.RefreshTracked(new[] { new MarketPriceModifierHandle("prices", "factor", 0,
                c => ValueModifierContribution.Factor(c.IsAIPlayer ? 3 : 2)) });
            var buy = MarketPriceModifiers.CreateStatusQuery(eGoods.STORED_NULL, MarketPriceDirection.Buy, true);
            var sell = MarketPriceModifiers.CreateStatusQuery(eGoods.STORED_NULL, MarketPriceDirection.Sell, false);
            // Use the production query with a managed table. Restore all process-wide fields
            // before releasing the fixture; this exercises the real mission and monitor guards.
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var owner = typeof(MarketPriceModifiers);
            var fields = new[] { owner.GetField("Table", flags), owner.GetField("engineMonitor", flags),
                owner.GetField("initialized", flags), owner.GetField("sessionActive", flags) };
            var previous = Array.ConvertAll(fields, field => field.GetValue(null));
            try
            {
                fields[0].SetValue(null, f.Table);
                fields[1].SetValue(null, new object());
                fields[2].SetValue(null, true);
                fields[3].SetValue(null, true);
                f.Values[0] = 333;
                Assert.IsTrue(ValueModifiers.TryGetStatus(buy, out var aiBuy, out string reason), reason);
                Assert.AreEqual(300m, aiBuy.ResolvedValue);
                Assert.AreEqual(200m, aiBuy.Observation.Value.ReferenceValue);
                Assert.AreEqual(333m, aiBuy.Observation.Value.Value);
                Assert.IsTrue(ValueModifiers.TryGetStatus(sell, out var sharedSell, out reason), reason);
                Assert.AreEqual(100m, sharedSell.ResolvedValue);
                Assert.IsFalse(sharedSell.Observation.Value.DiffersFromReference);
                fields[3].SetValue(null, false);
                Assert.IsFalse(ValueModifiers.TryGetStatus(buy, out var ended, out reason));
                Assert.IsNull(ended);
                Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
            }
            finally
            {
                for (int i = fields.Length - 1; i >= 0; i--) fields[i].SetValue(null, previous[i]);
            }
        }

        [TestMethod]
        public void StatusQueryDoesNotEvaluateProvidersWriteOrAdoptATransientForeignValue()
        {
            var queried = new Fixture(); var control = new Fixture();
            int providers = 0; decimal factor = 2;
            var selected = new[] { new MarketPriceModifierHandle("prices", "factor", 7,
                c => { providers++; return ValueModifierContribution.Factor(factor); }) };
            queried.RefreshTracked(selected); control.RefreshTracked(selected);
            int beforeProviders = providers, beforeWrites = queried.Writes, beforeReads = queried.Reads;
            queried.Values[0] = control.Values[0] = 333;
            var status = queried.Status(); var ai = queried.Status(ai: true);
            Assert.AreEqual(beforeProviders, providers); Assert.AreEqual(beforeWrites, queried.Writes);
            Assert.AreEqual(beforeReads + 2, queried.Reads);
            Assert.AreEqual(100m, status.BaselineValue); Assert.AreEqual(200m, status.ResolvedValue);
            Assert.AreEqual(333m, status.Observation.Value.Value); Assert.AreEqual(200m, status.Observation.Value.ReferenceValue);
            Assert.IsNull(status.ExternalFixedValue); Assert.IsTrue(status.Observation.Value.DiffersFromReference);
            Assert.AreEqual(333m, ai.Observation.Value.Value);
            // If the query incorrectly calls Observe, it adopts 333 as foreign. Restoring the
            // real table before Refresh then exposes that unwanted persistent ownership change.
            queried.Values[0] = control.Values[0] = 200; factor = 3;
            queried.RefreshTracked(selected); control.RefreshTracked(selected);
            CollectionAssert.AreEqual(control.Values, queried.Values);
            Assert.AreEqual(300, queried.Values[0]); Assert.IsNull(queried.Status().ExternalFixedValue);
            Assert.AreEqual(200m, status.ResolvedValue); Assert.AreEqual(333m, status.Observation.Value.Value);
        }

        [TestMethod]
        public void StatusRetainsOrderedStagesFailuresAIProjectionAndDetachedResults()
        {
            var f = new Fixture(); decimal finalFixed = 80;
            bool outsideAccepted = ValueModifiers.TryGetStatus(MarketPriceModifiers.CreateStatusQuery(eGoods.STORED_NULL, MarketPriceDirection.Buy, false), out _, out string outsideReason);
            bool queriedInsideProvider = false, insideAccepted = true;
            ValueModifierStatus insideStatus = null; string insideReason = null;
            var selected = new[] {
                new MarketPriceModifierHandle("a", "before", -4, c => {
                    if (!queriedInsideProvider)
                    {
                        queriedInsideProvider = true;
                        insideAccepted = ValueModifiers.TryGetStatus(MarketPriceModifiers.CreateStatusQuery(eGoods.STORED_NULL,
                            MarketPriceDirection.Buy, false), out insideStatus, out insideReason);
                    }
                    return c.IsAIPlayer ? ValueModifierContribution.None : ValueModifierContribution.Factor(2);
                }),
                new MarketPriceModifierHandle("b", "earlier", 0, c => ValueModifierContribution.FixedValue(70)),
                new MarketPriceModifierHandle("b", "winner", 3, c => ValueModifierContribution.FixedValue(finalFixed)),
                new MarketPriceModifierHandle("c", "after", 5, c => c.IsAIPlayer ? ValueModifierContribution.None : ValueModifierContribution.Factor(1.5m, ValueModifierStage.AfterFixedValue)),
                new MarketPriceModifierHandle("d", "broken", 8, c => throw new Exception("isolated status provider")) };
            f.RefreshTracked(selected);
            var human = f.Status(); var ai = f.Status(ai: true);
            // Assert outside the isolated provider: an assertion inside it could be swallowed as
            // a provider failure. In an unavailable fixture, false alone would not prove refusal
            // for reentrancy, so require its diagnostic to distinguish the active evaluation.
            Assert.IsTrue(queriedInsideProvider); Assert.IsFalse(insideAccepted); Assert.IsNull(insideStatus);
            Assert.IsFalse(string.IsNullOrEmpty(insideReason));
            if (!outsideAccepted) Assert.AreNotEqual(outsideReason, insideReason);
            Assert.AreEqual(120m, human.ResolvedValue); Assert.AreEqual(80m, ai.ResolvedValue);
            Assert.AreEqual(120m, ai.Observation.Value.Value); Assert.AreEqual(120m, ai.Observation.Value.ReferenceValue);
            Assert.IsFalse(ai.Observation.Value.DiffersFromReference);
            Assert.AreEqual(5, human.Contributions.Count);
            Assert.AreEqual("a", human.Contributions[0].OwnerGuid); Assert.AreEqual("before", human.Contributions[0].RegistrationId);
            Assert.AreEqual(-4, human.Contributions[0].Order); Assert.IsTrue(human.Contributions[0].SupersededByFixedValue);
            Assert.IsTrue(human.Contributions[1].SupersededByFixedValue); Assert.IsFalse(human.Contributions[2].SupersededByFixedValue);
            Assert.AreEqual(80m, human.Contributions[2].Contribution.Value);
            Assert.AreEqual(ValueModifierStage.AfterFixedValue, human.Contributions[3].Contribution.Stage);
            Assert.IsTrue(human.Contributions[4].ProviderFailed);
            Assert.AreEqual(ValueModifierKind.None, ai.Contributions[0].Contribution.Kind);
            Assert.ThrowsExactly<NotSupportedException>(() =>
                ((System.Collections.Generic.IList<ValueModifierContributionStatus>)human.Contributions)[0] = default);
            finalFixed = 90; f.RefreshTracked(selected);
            Assert.AreEqual(135m, f.Status().ResolvedValue);
            Assert.AreEqual(120m, human.ResolvedValue); Assert.AreEqual(80m, human.Contributions[2].Contribution.Value);
            f.Table.End(_ => f.Errors++);
            Assert.AreEqual(120m, human.ResolvedValue); Assert.AreEqual(5, human.Contributions.Count);
        }

        [TestMethod]
        public void StatusPreservesLegacyPassthroughAndReportsOnlyActualFinalClamping()
        {
            var f = new Fixture();
            var before = new MarketPriceModifierHandle("a", "before", 0, c => ValueModifierContribution.Factor(2));
            var afterOne = new MarketPriceModifierHandle("b", "identity-after", 0,
                c => ValueModifierContribution.Factor(1, ValueModifierStage.AfterFixedValue));
            var after = new MarketPriceModifierHandle("c", "after", 0,
                c => ValueModifierContribution.Factor(1.5m, ValueModifierStage.AfterFixedValue));
            f.Values[0] = 9000; f.RefreshTracked(new[] { before, afterOne });
            var passthrough = f.Status();
            Assert.AreEqual(9000m, passthrough.ResolvedValue); Assert.AreEqual((decimal?)9000, passthrough.ExternalFixedValue);
            Assert.IsFalse(passthrough.Bounds.HasValue);
            Assert.IsTrue(passthrough.Contributions[0].SupersededByFixedValue);
            f.RefreshTracked(new[] { before, after });
            var capped = f.Status(); Assert.AreEqual(6553m, capped.ResolvedValue);
            Assert.IsTrue(capped.Bounds.HasValue); Assert.IsTrue(capped.Bounds.Value.WasClamped);
            Assert.AreEqual(0m, capped.Bounds.Value.Minimum); Assert.AreEqual(6553m, capped.Bounds.Value.Maximum);
            Assert.IsFalse(capped.Contributions[1].SupersededByFixedValue);
            var clean = new Fixture(); clean.RefreshTracked(Array.Empty<MarketPriceModifierHandle>());
            Assert.IsFalse(clean.Status().Bounds.HasValue);
            clean.RefreshTracked(new[] { new MarketPriceModifierHandle("bounds", "negative", 0, c => ValueModifierContribution.FixedValue(-1)) });
            Assert.AreEqual(0m, clean.Status().ResolvedValue); Assert.IsTrue(clean.Status().Bounds.Value.WasClamped);
            clean.RefreshTracked(new[] { new MarketPriceModifierHandle("bounds", "huge", 0, c => ValueModifierContribution.Factor(decimal.MaxValue)) });
            Assert.AreEqual(6553m, clean.Status().ResolvedValue); Assert.IsTrue(clean.Status().Bounds.Value.WasClamped);
            Assert.AreEqual(6554m, clean.Status(buying: false).ResolvedValue); Assert.IsTrue(clean.Status(buying: false).Bounds.Value.WasClamped);
            clean.RefreshTracked(new[] { new MarketPriceModifierHandle("bounds", "exact", 0, c => ValueModifierContribution.FixedValue(6553)) });
            Assert.IsTrue(clean.Status().Bounds.HasValue); Assert.IsFalse(clean.Status().Bounds.Value.WasClamped);
        }

        [TestMethod]
        public void StatusRefusesUnavailableFailedRefreshAndUnreadableCellsWithoutChangingOtherCells()
        {
            var f = new Fixture();
            Assert.IsFalse(f.Table.TryGetStatus(0, true, false, out var unavailable, out _)); Assert.IsNull(unavailable);
            Assert.IsFalse(f.Table.TryGetStatus(-1, true, false, out _, out _));
            Assert.IsFalse(ValueModifiers.TryGetStatus(MarketPriceModifiers.CreateStatusQuery((eGoods)(-1), MarketPriceDirection.Buy, false), out _, out _));
            Assert.IsFalse(ValueModifiers.TryGetStatus(MarketPriceModifiers.CreateStatusQuery(eGoods.STORED_NULL, (MarketPriceDirection)99, false), out _, out _));
            var selected = new[] { new MarketPriceModifierHandle("test", "twice", 0, c => ValueModifierContribution.Factor(2)) };
            f.FailAfterMutation = true; f.RefreshTracked(selected);
            Assert.IsFalse(f.Table.TryGetStatus(0, true, false, out unavailable, out _)); Assert.IsNull(unavailable);
            Assert.AreEqual(100m, f.Status(buying: false).ResolvedValue);
            f.FailAfterMutation = false; f.RefreshTracked(selected);
            Assert.AreEqual(200m, f.Status().ResolvedValue);
            int writes = f.Writes; f.FailRead = true;
            Assert.IsFalse(f.Table.TryGetStatus(0, true, false, out unavailable, out _)); Assert.IsNull(unavailable);
            Assert.AreEqual(writes, f.Writes); f.FailRead = false;
            Assert.AreEqual(200m, f.Status().ResolvedValue);
            f.Table.End(_ => f.Errors++);
            Assert.IsFalse(f.Table.TryGetStatus(0, true, false, out unavailable, out _)); Assert.IsNull(unavailable);
        }

        [TestMethod]
        public void ReusedStatusCaptureAddsNoWarmRefreshAllocationBeforeQueries()
        {
            var plain = new Fixture(); var traced = new Fixture();
            var selected = new[] { new MarketPriceModifierHandle("allocation", "factor", 0, c => ValueModifierContribution.Factor(2)) };
            Func<int, bool, bool, int, int?, MarketPriceCompositionTrace, MarketPriceResolution> without =
                (g, b, ai, baseline, foreign, trace) => MarketPriceModifiers.Resolve(selected, g, b, ai, baseline, foreign);
            Func<int, bool, bool, int, int?, MarketPriceCompositionTrace, MarketPriceResolution> with =
                (g, b, ai, baseline, foreign, trace) => MarketPriceModifiers.Resolve(selected, g, b, ai, baseline, foreign, trace);
            Action<string> error = message => throw new Exception(message);
            Action plainRefresh = () => plain.Table.Refresh(without, error);
            Action tracedRefresh = () => traced.Table.Refresh(with, error);
            long plainBytes = MeasureRefresh(plainRefresh), tracedBytes = MeasureRefresh(tracedRefresh);
            long controlStart = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(new object[1]);
            long controlBytes = GC.GetAllocatedBytesForCurrentThread() - controlStart;
            TestContext.WriteLine("MARKET_STATUS_ALLOC iterations=512 plain={0} traced={1} positiveControl={2}", plainBytes, tracedBytes, controlBytes);
            Assert.IsTrue(controlBytes > 0, "The allocation counter must observe the allocating control.");
            Assert.AreEqual(plainBytes, tracedBytes, "Cached provenance must add no warm per-refresh allocations.");
            CollectionAssert.AreEqual(plain.Values, traced.Values);
            Assert.AreEqual(200m, traced.Status().ResolvedValue);
        }

        private static long MeasureRefresh(Action refresh)
        {
            for (int i = 0; i < 256; i++) refresh();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 512; i++) refresh();
            long after = GC.GetAllocatedBytesForCurrentThread();
            return after - before;
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
