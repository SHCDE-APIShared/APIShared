using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    [TestClass]
    public class UnitHudActionButtonLayoutTests
    {
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
