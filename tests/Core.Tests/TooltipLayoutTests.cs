using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    [TestClass]
    public sealed class TooltipLayoutTests
    {
        private sealed class Node
        {
            internal Node Parent;
            internal bool Panel, Boundary;
        }
        private static Node Root(Node node) => TooltipLayout.FindRoot(node, n => n.Parent, n => n.Panel, n => n.Boundary);

        [TestMethod]
        public void OverlayCannotEscapeItsHudAndCoverAnOuterModalLayer()
        {
            var screen = new Node { Panel = true };
            var hud = new Node { Boundary = true, Parent = screen };
            var content = new Node { Panel = true, Parent = hud };
            var panel = new Node { Panel = true, Parent = content };
            Assert.AreSame(content, Root(new Node { Parent = panel }));
            Assert.AreNotSame(screen, Root(new Node { Parent = panel }));
        }

        [TestMethod]
        public void NestedControlsAndDetachedAnchorsKeepTheirOwnBoundaries()
        {
            var outer = new Node { Panel = true };
            var boundary = new Node { Boundary = true, Parent = outer };
            Assert.IsNull(Root(new Node { Parent = boundary }));
            Assert.IsNull(Root(new Node()));
            Assert.AreSame(outer, Root(new Node { Parent = outer }));
        }
        [TestMethod]
        public void LegacyBuildingTextRetainsItsPublishedStyleWithoutChangingGeneralRows()
        {
            var legacy = new TooltipLine(true, new TooltipSegment[] { new TooltipText("limit") });
            var general = new TooltipLine(new TooltipText("limit"));
            Assert.AreEqual(16f, TooltipLayout.RowFontSize(legacy, 20));
            Assert.AreEqual(440f, TooltipLayout.RowWidthLimit(legacy));
            Assert.AreEqual(20f, TooltipLayout.RowFontSize(general, 20));
            Assert.AreEqual(float.PositiveInfinity, TooltipLayout.RowWidthLimit(general));
            Assert.IsFalse(TooltipRegistry.SameLine(legacy, general));
        }
    }
}
