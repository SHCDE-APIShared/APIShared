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
        public void VanillaActionSlotsDoNotReserveNavigationUntilNeeded(int count, int slots)
        {
            var decision = TroopActionPagingPolicy.Decide(count, slots, 5);
            Assert.AreEqual(count, decision.DirectCount);
            Assert.AreEqual(1, decision.PageCount);
            Assert.IsFalse(decision.HasNavigation);
            Assert.IsFalse(decision.UsesFallbackAnchor);
            Assert.AreEqual(0, decision.GetSlice(0).Start);
            Assert.AreEqual(count, decision.GetSlice(0).Count);
        }

        [TestMethod]
        public void SixArcherActionsKeepOneDirectAndFiveOnTheSecondPage()
        {
            var decision = TroopActionPagingPolicy.Decide(6, 2, 5);
            Assert.AreEqual(1, decision.DirectCount);
            Assert.AreEqual(2, decision.PageCount);
            Assert.AreEqual(1, decision.GetSlice(1).Start);
            Assert.AreEqual(5, decision.GetSlice(1).Count);
            Assert.IsTrue(decision.HasNavigation);
            Assert.IsFalse(decision.UsesFallbackAnchor);

            var afterHidingAnAction = TroopActionPagingPolicy.Decide(2, 2, 5);
            Assert.AreEqual(2, afterHidingAnAction.DirectCount);
            Assert.IsFalse(afterHidingAnAction.HasNavigation, "Releasing overflow must restore direct access.");
            Assert.AreEqual(0, afterHidingAnAction.ClampPage(1));
        }

        [TestMethod]
        public void OneRemainingSlotBecomesNavigationAndZeroSlotsUseTheExternalAnchor()
        {
            var oneSlot = TroopActionPagingPolicy.Decide(2, 1, 5);
            Assert.AreEqual(0, oneSlot.DirectCount);
            Assert.AreEqual(2, oneSlot.GetSlice(1).Count);
            Assert.IsTrue(oneSlot.HasNavigation);
            Assert.IsFalse(oneSlot.UsesFallbackAnchor);

            var noSlots = TroopActionPagingPolicy.Decide(7, 0, 6);
            Assert.AreEqual(0, noSlots.DirectCount);
            Assert.AreEqual(3, noSlots.PageCount);
            Assert.AreEqual(0, noSlots.GetSlice(1).Start);
            Assert.AreEqual(6, noSlots.GetSlice(1).Count);
            Assert.AreEqual(6, noSlots.GetSlice(2).Start);
            Assert.AreEqual(1, noSlots.GetSlice(2).Count);
            Assert.IsTrue(noSlots.HasNavigation);
            Assert.IsTrue(noSlots.UsesFallbackAnchor, "Occupied Vanilla slots must never make registered actions unreachable.");
        }

        [TestMethod]
        public void RingNavigationTraversesBothEndsAndClampsAfterActionsDisappear()
        {
            var mixed = TroopActionPagingPolicy.Decide(8, 2, 5);
            Assert.AreEqual(3, mixed.PageCount);
            Assert.AreEqual(2, mixed.Move(0, -1));
            Assert.AreEqual(0, mixed.Move(2, 1));
            Assert.AreEqual(1, mixed.Move(2, -1));
            Assert.AreEqual(0, mixed.Move(1, -1));
            Assert.AreEqual(1, mixed.Move(0, 1));
            Assert.AreEqual(2, mixed.Move(1, 1));
            Assert.AreEqual(2, mixed.GetSlice(2).Count);
            Assert.AreEqual(6, mixed.GetSlice(2).Start);

            var reduced = TroopActionPagingPolicy.Decide(3, 2, 5);
            Assert.AreEqual(1, reduced.ClampPage(2));
            Assert.AreEqual(0, reduced.ClampPage(-1));
            Assert.AreEqual(2, reduced.GetSlice(2).Count);
        }

        [TestMethod]
        public void ForeignReservationsReduceLaterPageCapacityWithoutSkippingActions()
        {
            var reserved = TroopActionPagingPolicy.Decide(9, 1, 3);
            Assert.AreEqual(4, reserved.PageCount);
            var indexes = Enumerable.Range(0, reserved.PageCount)
                .SelectMany(page => Enumerable.Range(reserved.GetSlice(page).Start, reserved.GetSlice(page).Count)).ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(0, 9).ToArray(), indexes,
                "Every registered action must appear once even when foreign buttons reserve slots.");
        }

        [TestMethod]
        public void InvalidCapacitiesCannotInventGridSlotsOrUnreachableNavigation()
        {
            var empty = TroopActionPagingPolicy.Decide(-1, -1, -1);
            Assert.AreEqual(0, empty.DirectCount);
            Assert.AreEqual(1, empty.PageCount);
            Assert.IsFalse(empty.HasNavigation);
            Assert.AreEqual(0, empty.Move(20, -1));
            var bounded = TroopActionPagingPolicy.Decide(20, 100, 100);
            Assert.AreEqual(5, bounded.DirectCount, "A grid has six fields, including navigation when needed.");
            Assert.AreEqual(6, bounded.GetSlice(1).Count);
            var foreignOnly = TroopActionPagingPolicy.Decide(2, 0, 0);
            Assert.AreEqual(1, foreignOnly.PageCount);
            Assert.IsFalse(foreignOnly.HasNavigation, "Navigation cannot promise an inaccessible page.");
            Assert.AreEqual(0, foreignOnly.GetSlice(0).Count);
        }

        [TestMethod]
        public void TemplateProvidesAnOpaqueIconAndHitSurfaceAboveVanillaClickShields()
        {
            var patch = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "HUD_Troops.xaml"));
            System.Xml.Linq.XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var style = patch.Descendants().Single(e => e.Name.LocalName == "Style" &&
                (string)e.Attribute(xaml + "Key") == "APISharedTroopActionButtonStyle" &&
                (string)e.Attribute("TargetType") == "{x:Type Button}");
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
