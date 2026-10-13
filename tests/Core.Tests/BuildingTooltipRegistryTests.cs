using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    [TestClass]
    public class BuildingTooltipRegistryTests
    {
        private static BuildingTooltipLine[] Line(string text) => new[] { new BuildingTooltipLine(new BuildingTooltipText(text)) };
        private static string Text(BuildingTooltipLine line) => line.Segments[0].Text;
        [TestMethod]
        public void RegistrationValidatesIdentityAndSortsOrdinallyAcrossUnrelatedOwners()
        {
            var registry = new BuildingTooltipRegistry();
            Assert.IsFalse(registry.HasDemand);
            Assert.IsFalse(registry.TryRegister("", "line", 0, _ => Line("bad"), out _));
            Assert.IsFalse(registry.TryRegister("foreign", "", 0, _ => Line("bad"), out _));
            Assert.IsFalse(registry.TryRegister("foreign", "line", 0, null, out _));
            Assert.IsFalse(registry.HasDemand);
            Assert.IsTrue(registry.TryRegister("z.external", "line", 0, _ => Line("Z"), out _));
            Assert.IsTrue(registry.TryRegister("a.external", "b", 0, _ => Line("AB"), out _));
            Assert.IsTrue(registry.TryRegister("a.external", "a", 0, _ => Line("AA"), out _));
            Assert.IsTrue(registry.TryRegister("other", "ground", 1000, _ => Line("GROUND"), out _));
            Assert.IsFalse(registry.TryRegister("a.external", "a", -1, _ => Line("duplicate"), out string reason));
            StringAssert.Contains(reason, "Duplicate");
            Assert.IsTrue(registry.HasDemand);
            var rows = registry.Collect(new BuildingTooltipContext(18,BuildingTooltipVariant.Compact,false), null);
            CollectionAssert.AreEqual(new[] { "AA", "AB", "Z", "GROUND" }, rows.Select(Text).ToArray());
        }
        [TestMethod]
        public void ContextMatchesBothVariantsAndLogicalDeactivationConsumesNoRows()
        {
            var registry = new BuildingTooltipRegistry(); bool enabled = true;
            registry.TryRegister("arbitrary.mod", "line", 0, c => enabled ? Line(c.BuildingType+":"+c.Variant+":"+c.IsSymbolHover) : Array.Empty<BuildingTooltipLine>(), out _);
            var compact = registry.Collect(new BuildingTooltipContext(24,BuildingTooltipVariant.Compact,false),null);
            Assert.AreEqual("24:Compact:False",Text(compact.Single()));
            var detailed = registry.Collect(new BuildingTooltipContext(26,BuildingTooltipVariant.Detailed,true),null);
            Assert.AreEqual("26:Detailed:True",Text(detailed.Single()));
            enabled = false; Assert.AreEqual(0,registry.Collect(new BuildingTooltipContext(24,BuildingTooltipVariant.Compact,false),null).Length);
            enabled = true; Assert.AreEqual(1,registry.Collect(new BuildingTooltipContext(28,BuildingTooltipVariant.Compact,false),null).Length);
        }
        [TestMethod]
        public void OneFailedProviderDoesNotHideOtherRowsAndReportsOnce()
        {
            var registry=new BuildingTooltipRegistry(); int reports=0; bool fail=true;
            registry.TryRegister("foreign", "broken", 0, _ => fail ? throw new InvalidOperationException("fixture") : Line("recovered"),out _);
            registry.TryRegister("independent", "healthy", 1, _ => Line("alive"),out _);
            for(int i=0;i<3;i++) Assert.AreEqual("alive",Text(registry.Collect(new BuildingTooltipContext(18,BuildingTooltipVariant.Compact,false),(_,_)=>reports++).Single()));
            Assert.AreEqual(1,reports);
            fail=false;Assert.AreEqual(2,registry.Collect(new BuildingTooltipContext(18,BuildingTooltipVariant.Compact,false),null).Length);
        }
        [TestMethod]
        public void ImmutableSegmentsRetainColoursAndInputsAreCopied()
        {
            var segments=new[] {new BuildingTooltipText("Vegetation: 81/81"),new BuildingTooltipText(" · Green: 47/50",0xFFFF5555)};
            var line=new BuildingTooltipLine(segments);segments[0]=new BuildingTooltipText("changed");
            Assert.AreEqual("Vegetation: 81/81",line.Segments[0].Text);
            Assert.AreEqual("#FFFF5555",line.Segments[1].Foreground);
            Assert.ThrowsExactly<ArgumentNullException>(()=>new BuildingTooltipText(null));
            Assert.ThrowsExactly<ArgumentException>(()=>new BuildingTooltipLine(new BuildingTooltipText[] {null}));
            Assert.IsTrue(BuildingTooltipRegistry.Same(new[]{line},new[]{new BuildingTooltipLine(new BuildingTooltipText("Vegetation: 81/81"),new BuildingTooltipText(" · Green: 47/50",0xFFFF5555))}));
            Assert.IsFalse(BuildingTooltipRegistry.Same(new[]{line},Line("changed")));
        }
        [TestMethod]
        public void NullEmptyAndInvalidContributionsNeverLeavePartialRows()
        {
            var registry=new BuildingTooltipRegistry();
            registry.TryRegister("null","line",0,_=>null,out _);
            registry.TryRegister("empty","line",0,_=>Line(""),out _);
            registry.TryRegister("invalid","line",0,_=>new BuildingTooltipLine[]{Line("partial")[0],null},out _);
            Assert.AreEqual(0,registry.Collect(new BuildingTooltipContext(18,BuildingTooltipVariant.Detailed,true),null).Length);
        }
        [TestMethod]
        public void CallbackCanRegisterWithoutReenteringALockOrChangingTheCurrentSnapshot()
        {
            var registry=new BuildingTooltipRegistry();bool first=true;
            registry.TryRegister("foreign","first",0,context=>{
                if(first) { first=false;registry.TryRegister("foreign","later",1,__=>Line("later"),out _); }
                return Line("first");
            },out _);
            var context=new BuildingTooltipContext(18,BuildingTooltipVariant.Compact,false);
            Assert.AreEqual(1,registry.Collect(context,null).Length);
            Assert.AreEqual(2,registry.Collect(context,null).Length);
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
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static long MeasureDormantDemand(BuildingTooltipRegistry registry)
        {
            long before=GC.GetAllocatedBytesForCurrentThread();bool demand=false;
            for(int i=0;i<50000;i++) demand |= registry.HasDemand;
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            if(demand) throw new InvalidOperationException("Dormant fixture acquired demand.");
            return allocated;
        }
        [TestMethod]
        public void DormantDemandCheckAllocatesNothingAfterWarmup()
        {
            var registry=new BuildingTooltipRegistry();
            // Warm the complete measurement path and tiered loop, not only the property.
            for(int i=0;i<5;i++) MeasureDormantDemand(registry);
            long best=long.MaxValue;
            for(int i=0;i<5;i++) best=Math.Min(best,MeasureDormantDemand(registry));
            Assert.AreEqual(0L,best);
        }
        private static readonly IReadOnlyList<BuildingTooltipLine> steadyRows=Array.AsReadOnly(Line("stable"));
        private static IReadOnlyList<BuildingTooltipLine> Empty(BuildingTooltipContext _) => Array.Empty<BuildingTooltipLine>();
        private static IReadOnlyList<BuildingTooltipLine> Steady(BuildingTooltipContext _) => steadyRows;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static long MeasureCollection(BuildingTooltipRegistry registry, BuildingTooltipContext context)
        {
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<10000;i++) registry.Collect(context,null);
            return GC.GetAllocatedBytesForCurrentThread()-before;
        }
        [TestMethod]
        public void EmptyAndUnchangedContributionsReuseSnapshotsWithoutCollectionAllocations()
        {
            foreach(var provider in new Func<BuildingTooltipContext,IReadOnlyList<BuildingTooltipLine>>[]{Empty,Steady})
            {
                var registry=new BuildingTooltipRegistry();registry.TryRegister("external","line",0,provider,out _);
                var context=new BuildingTooltipContext(24,BuildingTooltipVariant.Compact,false);
                var initial=registry.Collect(context,null);
                for(int i=0;i<5;i++) MeasureCollection(registry,context);
                long best=long.MaxValue;for(int i=0;i<5;i++) best=Math.Min(best,MeasureCollection(registry,context));
                Assert.AreEqual(0L,best);Assert.AreSame(initial,registry.Collect(context,null));
            }
        }
        [TestMethod]
        public void ReusableBufferCannotChangeEarlierPublishedRows()
        {
            var registry=new BuildingTooltipRegistry();string text="before";
            registry.TryRegister("foreign","line",0,_=>Line(text),out _);
            var context=new BuildingTooltipContext(24,BuildingTooltipVariant.Compact,false);
            var before=registry.Collect(context,null);text="after";var after=registry.Collect(context,null);
            Assert.AreEqual("before",Text(before.Single()));Assert.AreEqual("after",Text(after.Single()));Assert.AreNotSame(before,after);
        }
    }
}
