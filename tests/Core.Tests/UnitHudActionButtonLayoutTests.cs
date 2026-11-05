using APIShared;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    [TestClass]
    public class UnitHudActionButtonLayoutTests
    {
        [TestMethod]
        [DataRow(0, 0)]
        [DataRow(0, 2)]
        [DataRow(1, 1)]
        [DataRow(1, 2)]
        [DataRow(2, 2)]
        public void VanillaActionSlotsDoNotReserveAMenuUntilNeeded(int count, int slots)
        {
            var decision = TroopActionOverflowPolicy.Decide(count, slots);
            Assert.AreEqual(count, decision.DirectCount);
            Assert.AreEqual(0, decision.OverflowCount);
            Assert.IsFalse(decision.HasMore);
            Assert.IsFalse(decision.UsesFallbackAnchor);
        }

        [TestMethod]
        public void OverflowKeepsTheFirstActionDirectAndMovesTheRestTogether()
        {
            string[] orderedActions = { "knight", "assassin", "third-party" };
            var decision = TroopActionOverflowPolicy.Decide(orderedActions.Length, 2);
            CollectionAssert.AreEqual(new[] { "knight" }, orderedActions.Take(decision.DirectCount).ToArray());
            CollectionAssert.AreEqual(new[] { "assassin", "third-party" },
                orderedActions.Skip(decision.OverflowStart).Take(decision.OverflowCount).ToArray());
            Assert.IsTrue(decision.HasMore);
            Assert.IsFalse(decision.UsesFallbackAnchor);

            var afterHidingAnAction = TroopActionOverflowPolicy.Decide(2, 2);
            Assert.AreEqual(2, afterHidingAnAction.DirectCount);
            Assert.IsFalse(afterHidingAnAction.HasMore, "Releasing overflow must restore direct access.");
        }

        [TestMethod]
        public void OneRemainingSlotBecomesTheMenuAndZeroSlotsUseTheExternalAnchor()
        {
            var oneSlot = TroopActionOverflowPolicy.Decide(2, 1);
            Assert.AreEqual(0, oneSlot.DirectCount);
            Assert.AreEqual(2, oneSlot.OverflowCount);
            Assert.IsTrue(oneSlot.HasMore);
            Assert.IsFalse(oneSlot.UsesFallbackAnchor);

            var noSlots = TroopActionOverflowPolicy.Decide(2, 0);
            Assert.AreEqual(0, noSlots.DirectCount);
            Assert.AreEqual(2, noSlots.OverflowCount);
            Assert.IsTrue(noSlots.HasMore);
            Assert.IsTrue(noSlots.UsesFallbackAnchor, "Occupied Vanilla slots must never make registered actions unreachable.");
        }

        [TestMethod]
        public void InvalidCapacityCannotInventSlotsOrAnEmptyOverflowMenu()
        {
            var empty = TroopActionOverflowPolicy.Decide(-1, -1);
            Assert.AreEqual(0, empty.DirectCount);
            Assert.IsFalse(empty.HasMore);
            var occupied = TroopActionOverflowPolicy.Decide(1, -1);
            Assert.IsTrue(occupied.UsesFallbackAnchor);
            var bounded = TroopActionOverflowPolicy.Decide(3, 100);
            Assert.AreEqual(1, bounded.DirectCount, "Only the two established Vanilla action positions are assignable.");
            Assert.AreEqual(2, bounded.OverflowCount);
        }

        [TestMethod]
        public void OpenOverflowLeavesBothDirectAndFallbackOpenersReachable()
        {
            // Concrete zero-slot regression: placing the menu immediately above the panel
            // covered the fallback opener at y=459..496 with the menu's own hit surface.
            Assert.IsTrue(TroopActionOverflowPolicy.MenuBottom(500, 459) < 459,
                "A fallback opener above the panel must remain outside the open menu.");
            Assert.IsTrue(TroopActionOverflowPolicy.MenuBottom(500, 560) < 500,
                "A normal opener inside the panel leaves the menu above the panel.");
        }

        [TestMethod]
        public void TemplateProvidesAnOpaqueIconAndHitSurfaceAboveVanillaClickShields()
        {
            var patch = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "HUD_Troops.xaml"));
            var style = patch.Descendants().Single(e => e.Name.LocalName == "Style" && (string)e.Attribute("TargetType") == "{x:Type Button}");
            var template = style.Descendants().Single(e => e.Name.LocalName == "ControlTemplate");
            var hitSurface = template.Elements().First();
            Assert.AreEqual("Border", hitSurface.Name.LocalName);
            Assert.AreEqual("{TemplateBinding Background}", (string)hitSurface.Attribute("Background"));
            Assert.AreEqual("Transparent", (string)style.Elements().Single(e => (string)e.Attribute("Property") == "Background").Attribute("Value"));
            var content = hitSurface.Elements().Single();
            Assert.AreEqual("ContentPresenter", content.Name.LocalName);
            Assert.AreEqual("1", (string)content.Attribute("Opacity"));
            var pressed = template.Descendants().Single(e => e.Name.LocalName == "Trigger" && (string)e.Attribute("Property") == "IsPressed");
            Assert.AreEqual("0.75", (string)pressed.Elements().Single().Attribute("Value"));
            var layer = patch.Descendants().Single(e => (string)e.Attribute("AttributeName") == "Panel.ZIndex" &&
                (string)e.Attribute("XPath") == "//n:Grid[@Name='TroopSelectionControls']");
            Assert.AreEqual("//n:Grid[@Name='TroopSelectionControls']", (string)layer.Attribute("XPath"));
            Assert.AreEqual("3", (string)layer.Attribute("Value"));
            var vanillaButtonLayer = patch.Descendants().Single(e => (string)e.Attribute("XPath") == "//n:Button[@Name='ToggleControlGroups']");
            Assert.AreEqual("4", (string)vanillaButtonLayer.Attribute("Value"), "Vanilla Control Groups must stay above the overlapping troop container.");
            var hostOperation = patch.Elements().Single().Elements().Single(e => e.Descendants().Any(child =>
                child.Name.LocalName == "Canvas" && child.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "APISharedTroopActionButtonsHost")));
            Assert.AreEqual("//n:Grid[@Name='TroopSelectionControls']", (string)hostOperation.Attribute("XPath"));
            var next = hostOperation.Descendants().Single(e => e.Name.LocalName == "Button");
            Assert.AreEqual("False", (string)next.Attribute("ToolTipService.IsEnabled"));
            Assert.IsNull(next.Attribute("ToolTip"), "Action paging must not create a popup.");
        }
        [TestMethod]
        public void FitsOnlyWholeButtonsWithGaps()
        {
            Assert.AreEqual(9, UnitHudActionButtonLayout.Capacity(377, 12));
            Assert.AreEqual(1, UnitHudActionButtonLayout.Capacity(100, 61));
            Assert.AreEqual(0, UnitHudActionButtonLayout.Capacity(100, 62));
            Assert.AreEqual(0, UnitHudActionButtonLayout.Capacity(100, 120));
            Assert.AreEqual(0, UnitHudActionButtonLayout.Capacity(float.NaN, 0));
        }

        [TestMethod]
        public void CyclesPagesAndClampsAfterHidingOrResizing()
        {
            Assert.AreEqual(3, UnitHudActionButtonLayout.PageCount(19, 9));
            Assert.AreEqual(1, UnitHudActionButtonLayout.NextPage(0, 19, 9));
            Assert.AreEqual(2, UnitHudActionButtonLayout.NextPage(1, 19, 9));
            Assert.AreEqual(0, UnitHudActionButtonLayout.NextPage(2, 19, 9));
            Assert.AreEqual(0, UnitHudActionButtonLayout.ClampPage(2, 9, 9));
            Assert.AreEqual(1, UnitHudActionButtonLayout.ClampPage(2, 19, 18));
            Assert.AreEqual(0, UnitHudActionButtonLayout.PageCount(0, 9));
            Assert.AreEqual(0, UnitHudActionButtonLayout.NextPage(2, 19, 0));
        }

        [TestMethod]
        public void ForeignHudCannotBorrowTheVanillaShowFlag()
        {
            Assert.IsTrue(UnitHudActionButtonLayout.OwnContext(true, true, true, 1, 1, 3));
            Assert.IsFalse(UnitHudActionButtonLayout.OwnContext(true, true, false, 0, 1, 3));
            Assert.IsFalse(UnitHudActionButtonLayout.OwnContext(true, true, true, 0, 1, 3));
            Assert.IsFalse(UnitHudActionButtonLayout.OwnContext(true, true, true, 1, 1, 0));
            Assert.IsFalse(UnitHudActionButtonLayout.OwnContext(true, true, true, 1, -1, 3));
            Assert.IsFalse(UnitHudActionButtonLayout.OwnContext(false, true, true, 1, 1, 3));
            Assert.IsFalse(UnitHudActionButtonLayout.OwnContext(true, false, true, 1, 1, 3));
        }
    }
}
