using System;
using System.Linq;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APISharedTests
{
    [TestClass]
    public sealed class AllyGoodsPanelTests
    {
        [TestMethod]
        [TestCategory("Presentation")]
        public void IndependentAdornmentProvidersKeepStableOrderAndIdentity()
        {
            var registry = new AllyGoodsAdornmentRegistry();
            Assert.IsFalse(registry.TryRegister("", "marker", (_, __) => null, 0, out _, out _));
            Assert.IsFalse(registry.TryRegister("a", "marker", null, 0, out _, out _));
            Assert.IsTrue(registry.TryRegister("z", "marker", (_, __) => null, 0, out var first, out _));
            var snapshot = registry.Entries;
            Assert.IsTrue(registry.TryRegister("a", "marker", (_, __) => null, 0, out _, out _));
            Assert.IsTrue(registry.TryRegister("z", "earlier", (_, __) => null, -1, out _, out _));
            Assert.IsFalse(registry.TryRegister("z", "marker", (_, __) => null, 99, out _, out _));
            CollectionAssert.AreEqual(new[] { "z/earlier", "a/marker", "z/marker" },
                registry.Entries.Select(x => x.Owner + "/" + x.Id).ToArray());
            Assert.AreEqual(1, snapshot.Length);
            Assert.AreSame(first, snapshot[0]);
            // Sections and adornments deliberately have separate owner-local namespaces.
            Assert.IsTrue(new AllyGoodsSectionRegistry().TryRegister("z", "marker", _ => null, 0, out _, out _));
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void AdornmentCacheIsolatesFailuresAndPreservesSuccessfulViewsAcrossRefresh()
        {
            var registry = new AllyGoodsAdornmentRegistry();
            registry.TryRegister("a", "bad", (_, __) => null, 0, out var bad, out _);
            registry.TryRegister("b", "good", (_, __) => null, 0, out var good, out _);
            var cache = new AllyGoodsAdornmentCache<object>();
            int failures = 0, attempts = 0, creations = 0;
            Func<object> fail = () => { attempts++; throw new InvalidOperationException("expected"); };
            Action<Exception> failed = _ => failures++;
            Assert.IsNull(cache.GetOrCreate(bad, eGoods.STORED_GOLD, fail, failed));
            var view = cache.GetOrCreate(good, eGoods.STORED_GOLD, () => { creations++; return new object(); }, failed);
            Assert.IsNull(cache.GetOrCreate(bad, eGoods.STORED_GOLD, fail, failed));
            Assert.AreEqual(1, attempts);
            Assert.AreEqual(1, failures);
            bad.Revision++; good.Revision++;
            Assert.IsNull(cache.GetOrCreate(bad, eGoods.STORED_GOLD, fail, failed));
            Assert.AreSame(view, cache.GetOrCreate(good, eGoods.STORED_GOLD, () => { creations++; return new object(); }, failed));
            Assert.AreEqual(2, attempts);
            Assert.AreEqual(1, creations);
            Assert.AreNotSame(view, cache.GetOrCreate(good, eGoods.STORED_WOOD_PLANKS, () => new object(), failed));
            cache.Clear(); // Bound-host replacement is the only normal successful-view invalidation.
            Assert.AreNotSame(view, cache.GetOrCreate(good, eGoods.STORED_GOLD, () => new object(), failed));
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void AdornmentRefreshRequestedInsideFailedFactoryIsNotLost()
        {
            var registry = new AllyGoodsAdornmentRegistry();
            registry.TryRegister("a", "marker", (_, __) => null, 0, out var entry, out _);
            var cache = new AllyGoodsAdornmentCache<object>();
            Assert.IsNull(cache.GetOrCreate(entry, eGoods.STORED_GOLD, () => { entry.Revision++; return null; }, _ => Assert.Fail()));
            var retried = new object();
            Assert.AreSame(retried, cache.GetOrCreate(entry, eGoods.STORED_GOLD, () => retried, _ => Assert.Fail()));
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void AdornmentAnchorsAcceptOnlyExactVanillaGoodsCommands()
        {
            Assert.IsTrue(AllyGoodsPanelExtensions.TryParseGoodsCommand("G15", out var gold));
            Assert.AreEqual(eGoods.STORED_GOLD, gold);
            Assert.IsTrue(AllyGoodsPanelExtensions.TryParseGoodsCommand("G02", out var wood));
            Assert.AreEqual(eGoods.STORED_WOOD_PLANKS, wood);
            foreach (var command in new[] { null, "", "g02", "G2", "G002", "G00", "G99", "G-1", "G 2", "G+2" })
                Assert.IsFalse(AllyGoodsPanelExtensions.TryParseGoodsCommand(command, out _), command);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void IndependentSectionsRetainIdentityAndDeterministicOrdering()
        {
            var registry = new AllyGoodsSectionRegistry();
            Assert.IsTrue(registry.TryRegister("mod.z", "automation", _ => null, 0, out var second, out _));
            Assert.IsTrue(registry.TryRegister("mod.a", "automation", _ => null, 0, out var first, out _));
            Assert.IsTrue(registry.TryRegister("mod.z", "priority", _ => null, -1, out _, out _));
            Assert.IsFalse(registry.TryRegister("mod.a", "automation", _ => null, 5, out _, out _));
            CollectionAssert.AreEqual(new[] { "mod.z/priority", "mod.a/automation", "mod.z/automation" },
                registry.Entries.Select(x => x.Owner + "/" + x.Id).ToArray());
            first.Visible = false;
            second.Enabled = false;
            Assert.AreSame(second, registry.Entries.Single(x => x.Owner == "mod.z" && x.Id == "automation"));
            Assert.IsFalse(first.Visible);
            Assert.IsTrue(second.Visible);
            Assert.IsTrue(first.Enabled);
            Assert.IsFalse(second.Enabled);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void FaultyFactoryDoesNotPreventAnotherOwnerFromRunningAndCanRetry()
        {
            var registry = new AllyGoodsSectionRegistry();
            int badCalls = 0, otherCalls = 0;
            Assert.IsTrue(registry.TryRegister("a", "bad", _ => { badCalls++; throw new InvalidOperationException("expected"); },
                0, out var bad, out _));
            Assert.IsTrue(registry.TryRegister("b", "other", _ => { otherCalls++; return null; }, 0, out _, out _));
            foreach (var entry in registry.Entries)
                Assert.IsFalse(AllyGoodsPanelExtensions.TryCreate(entry, null, out _));
            Assert.AreEqual(1, badCalls);
            Assert.AreEqual(1, otherCalls);
            Assert.IsFalse(AllyGoodsPanelExtensions.TryCreate(bad, null, out _));
            Assert.AreEqual(2, badCalls);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void MissingSelectionAndZeroAmountAreDistinctFromValidGoods()
        {
            Assert.IsNull(AllyGoodsContextPolicy.Amount(-1));
            Assert.AreEqual(0, AllyGoodsContextPolicy.Amount(0));
            Assert.IsNull(AllyGoodsContextPolicy.Good(-1));
            Assert.IsNull(AllyGoodsContextPolicy.Good((int)eGoods.STORED_NULL));
            Assert.IsNull(AllyGoodsContextPolicy.Good((int)eGoods.Count));
            Assert.AreEqual(eGoods.STORED_GOLD, AllyGoodsContextPolicy.Good((int)eGoods.STORED_GOLD));
            var selected = new AllyGoodsPanelContext(true, true, false, 1, 3, 2, eGoods.STORED_WOOD_PLANKS, 0, 4);
            Assert.IsTrue(selected.HasSelection);
            Assert.AreEqual(1, selected.LocalPlayerId);
            Assert.AreEqual(3, selected.ViewedPlayerId);
            Assert.IsFalse(new AllyGoodsPanelContext(false, false, false, -1, -1, 0, null, null, 4).HasSelection);
            Assert.IsFalse(selected.Same(true, true, false, 1, 3, 2, eGoods.STORED_WOOD_PLANKS, 1, 4));
            Assert.IsFalse(selected.Same(true, true, false, 1, 3, 2, eGoods.STORED_WOOD_PLANKS, 0, 5));
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void RecipientSelectionDoesNotGuessArrayIndexOrAcceptAnInvalidSlot()
        {
            Assert.AreEqual(7, AllyGoodsContextPolicy.Recipient(1, 2, new[] { 3, 7 }));
            Assert.AreEqual(0, AllyGoodsContextPolicy.Recipient(2, 2, new[] { 3, 7 }));
            Assert.AreEqual(0, AllyGoodsContextPolicy.Recipient(-1, 2, new[] { 3, 7 }));
            Assert.AreEqual(0, AllyGoodsContextPolicy.Recipient(0, 1, new[] { 0 }));
            Assert.AreEqual(0, AllyGoodsContextPolicy.Recipient(0, 1, new[] { 9 }));
            Assert.AreEqual(0, AllyGoodsContextPolicy.Recipient(0, 1, null));
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void ExtensionHeightRespectsViewportAndDoesNotRequireMeasuringAnUnattachedView()
        {
            Assert.AreEqual(90f, AllyGoodsContextPolicy.ExtensionHeight(200, 90));
            Assert.AreEqual(260f, AllyGoodsContextPolicy.ExtensionHeight(800, 900));
            Assert.AreEqual(0f, AllyGoodsContextPolicy.ExtensionHeight(200, -10));
            Assert.AreEqual(128f, AllyGoodsContextPolicy.ExtensionHeight(float.NaN, 200));
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void ViewboxScaleConvertsRemainingPhysicalHeightBeforeApplyingLocalExtension()
        {
            // The UI's fixed 768-unit canvas can be scaled to a 2160px viewport.
            // A compatible foreign mod has already enlarged the original panel to480.
            const float scale = 2.8125f;
            float available = AllyGoodsContextPolicy.AvailableHeight(2160, 100 * scale, scale, 480);
            Assert.AreEqual(180f, available, 0.001f);
            Assert.AreEqual(180f, AllyGoodsContextPolicy.ExtensionHeight(260, available), 0.001f);
            Assert.AreEqual(available, AllyGoodsContextPolicy.AvailableHeight(768, 100, 1, 480), 0.001f);
            Assert.AreEqual(0f, AllyGoodsContextPolicy.AvailableHeight(2160, 281.25f, 0, 480));
            Assert.AreEqual(0f, AllyGoodsContextPolicy.AvailableHeight(float.NaN, 0, 1, 480));
            Assert.AreEqual(0f, AllyGoodsContextPolicy.AvailableHeight(2160, 0, float.NaN, 480));
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void RealAssemblySelectionMembersHaveTheCheckedPrivateContract()
        {
            // The exact private names/types are the required bridge to Vanilla's selection state,
            // unlike an implementation-structure assertion. Constructor resolves the real assembly.
            Assert.IsNotNull(new AllyGoodsPanelExtensions.SelectionReader());
        }
    }
}
