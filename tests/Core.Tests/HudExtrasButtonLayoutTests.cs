#nullable enable
using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Xml.Linq;

namespace Core.Tests
{
    [TestClass]
    public class HudExtrasButtonLayoutTests
    {
        [TestMethod]
        public void FiveEntryPagesCycleAndShrink()
        {
            foreach (var pair in new[] { (0, 0), (1, 1), (5, 1), (6, 2), (11, 3) })
                Assert.AreEqual(pair.Item2, HudExtrasButtonLayout.PageCount(pair.Item1));
            Assert.AreEqual(1, HudExtrasButtonLayout.NextPage(0, 11));
            Assert.AreEqual(2, HudExtrasButtonLayout.NextPage(1, 11));
            Assert.AreEqual(0, HudExtrasButtonLayout.NextPage(2, 11));
            Assert.AreEqual(0, HudExtrasButtonLayout.ClampPage(2, 5));
            Assert.AreEqual(1, HudExtrasButtonLayout.ClampPage(2, 6));
            Assert.AreEqual(0, HudExtrasButtonLayout.NextPage(0, 0));
        }
        [TestMethod]
        public void LayoutReservesVanillaAndUsesOrdinalTieBreaks()
        {
            Assert.AreEqual(34, HudExtrasButtonLayout.BaseBottom(false));
            Assert.AreEqual(68, HudExtrasButtonLayout.BaseBottom(true));
            Assert.IsTrue(HudExtrasButtonLayout.Compare(-1, "z", "z", 0, "a", "a") < 0);
            Assert.IsTrue(HudExtrasButtonLayout.Compare(0, "A", "z", 0, "a", "a") < 0);
            Assert.IsTrue(HudExtrasButtonLayout.Compare(0, "a", "a", 0, "a", "b") < 0);
        }
        [TestMethod]
        public void HostInheritsVanillaVisibilityAndOwnTooltipTemplate()
        {
            var patch = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "IngameUIScreens.xaml"));
            var operation = patch.Root!.Elements().Single();
            Assert.AreEqual("/n:UserControl/n:Grid/n:Grid[@Name='HUD_ObjectivesPanel']", (string?)operation.Attribute("XPath"));
            Assert.AreEqual(1, operation.Element("Content")!.Elements().Count());
            var host = operation.Element("Content")!.Elements().Single();
            Assert.AreEqual("Collapsed", (string?)host.Attribute("Visibility"));
            var style = host.Descendants().Single(e => e.Name.LocalName == "Style" &&
                e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "APISharedHudExtrasTooltipStyle"));
            string Value(string property) => (string)style.Elements().Single(e => (string?)e.Attribute("Property") == property).Attribute("Value")!;
            Assert.AreEqual("#FF1D1710", Value("Background"));
            Assert.AreEqual("White", Value("Foreground"));
            Assert.AreEqual("#FFF2D48A", Value("BorderBrush"));
            Assert.AreEqual("True", Value("seui:ToolTipResolutionScale.Enabled"));
            Assert.IsTrue(style.Descendants().Any(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("TextWrapping") == "Wrap"));
        }
    }
}
