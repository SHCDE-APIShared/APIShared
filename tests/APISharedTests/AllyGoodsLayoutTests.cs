using System;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class AllyGoodsLayoutTests
    {
        [TestMethod]
        public void LegacyOverloadAndConstructorKeepPanelRootPlacement()
        {
            var registry = new AllyGoodsSectionRegistry();
            Assert.IsTrue(registry.TryRegister("old", "section", _ => null, 0, out var old, out _));
            Assert.AreEqual(AllyGoodsSectionPlacement.PanelRootBottom, old.Placement);
            Assert.AreEqual(AllyGoodsSectionPlacement.PanelRootBottom,
                new AllyGoodsSectionEntry("old", "direct", 0, _ => null).Placement);
            Assert.IsTrue(registry.TryRegister("new", "section", _ => null,
                AllyGoodsSectionPlacement.GoodsDialogBottom, 0, out var modern, out _));
            Assert.AreEqual(AllyGoodsSectionPlacement.GoodsDialogBottom, modern.Placement);
            Assert.IsFalse(registry.TryRegister("old", "section", _ => null,
                AllyGoodsSectionPlacement.GoodsDialogBottom, 1, out _, out _));
            Assert.IsFalse(registry.TryRegister("new", "invalid", _ => null,
                (AllyGoodsSectionPlacement)42, 0, out _, out _));
            Assert.AreEqual(2, registry.Entries.Length);
        }

        [TestMethod]
        [DataRow(320f, 320f, 420f)]
        [DataRow(484f, 320f, 484f)]
        [DataRow(484f, 484f, 584f)]
        public void ExplicitGoodsAnchorUsesVisibleFrameWithoutShrinkingForeignRoot(float root, float goods, float expectedRoot)
        {
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(root, goods, 1000, 100, true, 0, false, out var result));
            Assert.AreEqual(goods, result.GoodsTop);
            Assert.AreEqual(100f, result.GoodsHeight);
            Assert.AreEqual(expectedRoot, result.RootHeight);
        }

        [TestMethod]
        public void LegacyOnlyIsUnchangedAndMixedGroupsNeverOverlap()
        {
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 1000, 0, false, 100, true, out var legacy));
            Assert.AreEqual(484f, legacy.LegacyTop); Assert.AreEqual(584f, legacy.RootHeight);
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 1000, 220, true, 100, true, out var mixed));
            Assert.AreEqual(540f, mixed.LegacyTop); Assert.AreEqual(640f, mixed.RootHeight);
            Assert.IsTrue(mixed.LegacyTop >= mixed.GoodsTop + mixed.GoodsHeight);
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 1000, 0, false, 0, false, out var closed));
            Assert.AreEqual(484f, closed.RootHeight);
        }

        [TestMethod]
        public void LimitedViewportIsSharedInLocalUnits()
        {
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 600, 400, true, 100, true, out var result));
            Assert.AreEqual(260f, result.GoodsHeight); Assert.AreEqual(12f, result.LegacyHeight);
            Assert.AreEqual(592f, result.RootHeight);
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 300, 100, true, 100, true, out var noSpace));
            Assert.AreEqual(0f, noSpace.GoodsHeight); Assert.AreEqual(0f, noSpace.LegacyHeight);
            Assert.AreEqual(484f, noSpace.RootHeight);
        }

        [TestMethod]
        public void InvalidMarkedGeometryAndForeignHeightAreRejected()
        {
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.ValidMarkedBounds(484, 0, 0, 700, 320, 20, 220, 660, 80));
            Assert.IsFalse(AllyGoodsPanelLayoutPolicy.ValidMarkedBounds(484, 0, 0, 700, 320, 20, 280, 660, 80));
            Assert.IsFalse(AllyGoodsPanelLayoutPolicy.ValidMarkedBounds(484, 0, 0, 700, 320, 20, float.NaN, 660, 80));
            Assert.IsFalse(AllyGoodsPanelLayoutPolicy.ValidMarkedBounds(484, 0, 0, 700, 320, 20, 220, float.PositiveInfinity, 80));
            Assert.IsFalse(AllyGoodsPanelLayoutPolicy.TryCompose(float.NaN, 320, 600, 100, true, 100, true, out _));
            Assert.IsFalse(AllyGoodsPanelLayoutPolicy.TryCompose(320, 484, 600, 100, true, 100, true, out _));
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.OwnsHeight(584, 584));
            Assert.IsFalse(AllyGoodsPanelLayoutPolicy.OwnsHeight(484, 584));
            Assert.IsFalse(AllyGoodsPanelLayoutPolicy.OwnsHeight(float.NaN, 584));
        }

        [TestMethod]
        public void AvailabilityNotifiesOnlyChangesAndIsolatesSubscribers()
        {
            var entry = new AllyGoodsSectionEntry("owner", "section", 0, _ => null);
            int invoked = 0, failed = 0;
            entry.LayoutChanged += () => throw new InvalidOperationException("expected");
            entry.LayoutChanged += () => invoked++;
            entry.SetLayoutAvailability(true, null, _ => failed++);
            Assert.IsTrue(entry.LayoutAvailable); Assert.AreEqual(string.Empty, entry.LayoutReason);
            entry.SetLayoutAvailability(true, "ignored while available", _ => failed++);
            Assert.AreEqual(1, invoked); Assert.AreEqual(1, failed);
            entry.SetLayoutAvailability(false, "no space", _ => failed++);
            Assert.AreEqual(2, invoked); Assert.AreEqual(2, failed);
            Assert.AreEqual("no space", entry.LayoutReason);
        }
    }
}
