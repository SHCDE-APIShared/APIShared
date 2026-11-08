using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    // Contribution ordering, isolation and allocation behaviour of the live registry are
    // covered by TooltipRegistryTests; these tests cover the building-tooltip contracts.
    [TestClass]
    public class BuildingTooltipTests
    {
        private static BuildingTooltipLine[] Line(string text) => new[] { new BuildingTooltipLine(new BuildingTooltipText(text)) };
        [TestMethod]
        public void ImmutableSegmentsRetainColoursAndInputsAreCopied()
        {
            var segments=new[] {new BuildingTooltipText("Vegetation: 81/81"),new BuildingTooltipText(" · Green: 47/50",0xFFFF5555)};
            var line=new BuildingTooltipLine(segments);segments[0]=new BuildingTooltipText("changed");
            Assert.AreEqual("Vegetation: 81/81",line.Segments[0].Text);
            Assert.AreEqual("#FFFF5555",line.Segments[1].Foreground);
            Assert.ThrowsExactly<ArgumentNullException>(()=>new BuildingTooltipText(null));
            Assert.ThrowsExactly<ArgumentException>(()=>new BuildingTooltipLine(new BuildingTooltipText[] {null}));
            Assert.IsTrue(BuildingTooltipLines.Same(new[]{line},new[]{new BuildingTooltipLine(new BuildingTooltipText("Vegetation: 81/81"),new BuildingTooltipText(" · Green: 47/50",0xFFFF5555))}));
            Assert.IsFalse(BuildingTooltipLines.Same(new[]{line},Line("changed")));
        }
        [TestMethod]
        public void SelectionPreservesVanillaPriorityAndCancellation()
        {
            Assert.AreEqual(24,BuildingTooltipSelection.Resolve(24,"Quarry",18,"Wheat",false,false,true,out bool hover));Assert.IsTrue(hover);
            Assert.AreEqual(18,BuildingTooltipSelection.Resolve(0,"",18,"Wheat",false,false,false,out hover));Assert.IsFalse(hover);
            Assert.AreEqual(0,BuildingTooltipSelection.Resolve(0,"Other symbol",18,"Wheat",false,false,false,out hover));Assert.IsTrue(hover);
            Assert.AreEqual(0,BuildingTooltipSelection.Resolve(0,"",18,"Wheat",true,true,false,out _));
            Assert.AreEqual(24,BuildingTooltipSelection.Resolve(24,"Quarry",18,"Wheat",true,true,true,out _));
            Assert.AreEqual(0,BuildingTooltipSelection.Resolve(0,"",0,"",false,false,false,out _));
            Assert.AreEqual(0,BuildingTooltipSelection.Resolve(0,"",18,"",true,false,false,out _));
        }
    }
}
