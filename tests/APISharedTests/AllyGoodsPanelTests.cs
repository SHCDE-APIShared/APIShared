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
