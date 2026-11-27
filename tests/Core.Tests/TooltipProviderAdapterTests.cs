using System;
using System.Collections.Generic;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    [TestClass]
    public sealed class TooltipProviderAdapterTests
    {
        private static TooltipContext Context(TooltipTarget target, bool active = true, int building = 1) =>
            new TooltipContext(target, active, building, null, 1, 1, true);

        [TestMethod]
        public void TextCachesAreIndependentAcrossActiveAndRetiredTargets()
        {
            var adapter = new TooltipTextProvider(c => c.IsActive ? c.Target.ToString() : null);
            var compact = Context(TooltipTarget.BuildingCompact);
            var detailed = Context(TooltipTarget.BuildingDetailed, false);
            var original = adapter.Get(compact);
            Assert.AreEqual(0, adapter.Get(detailed).Count);
            Assert.AreSame(original, adapter.Get(compact));
            Assert.AreEqual(0, adapter.Get(Context(TooltipTarget.BuildingCompact, false)).Count);
            Assert.AreEqual("BuildingDetailed", ((TooltipText)adapter.Get(Context(TooltipTarget.BuildingDetailed))[0].Segments[0]).Text);
        }

        [TestMethod]
        public void TextProviderDoesNotAllocateForRepeatedMixedTargetsAfterWarmup()
        {
            var adapter = new TooltipTextProvider(c => c.IsActive ? "extra cost" : null);
            var active = Context(TooltipTarget.Recruitment);
            var inactive = Context(TooltipTarget.SiegeConstruction, false);
            for (int i = 0; i < 100; i++) { adapter.Get(active); adapter.Get(inactive); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { adapter.Get(active); adapter.Get(inactive); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated);
        }

        [TestMethod]
        public void LegacyProviderRunsOnceForTheSelectedVariantAndReceivesZeroBuildingRetirement()
        {
            int calls = 0;
            BuildingTooltipContext received = null;
            IReadOnlyList<BuildingTooltipLine> lines = new[] { new BuildingTooltipLine(new BuildingTooltipText("limit", 0xFF123456)) };
            var adapter = new LegacyBuildingTooltipProvider(c => { calls++; received = c; return c.BuildingType == 0 ? null : lines; });
            Assert.AreEqual(0, adapter.Get(Context(TooltipTarget.BuildingCompact), false).Count);
            var result = adapter.Get(Context(TooltipTarget.BuildingDetailed), true);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(BuildingTooltipVariant.Detailed, received.Variant);
            Assert.IsTrue(result[0].LegacyBuildingStyle);
            Assert.AreEqual((uint?)0xFF123456, ((TooltipText)result[0].Segments[0]).ForegroundArgb);
            Assert.AreSame(result, adapter.Get(Context(TooltipTarget.BuildingDetailed), true));
            Assert.AreEqual(0, adapter.Get(Context(TooltipTarget.BuildingDetailed, false, 0), true).Count);
            Assert.AreEqual(0, received.BuildingType);
            Assert.AreEqual(BuildingTooltipVariant.Detailed, received.Variant);
            Assert.AreEqual(3, calls);
            Assert.AreEqual(0, adapter.Get(Context(TooltipTarget.BuildingCompact, false, 0), false).Count);
            Assert.AreEqual(3, calls);
        }

        [TestMethod]
        public void LegacyAdapterDoesNotAllocateForRepeatedSelectedAndInactiveVariantsAfterWarmup()
        {
            var lines = new[] { new BuildingTooltipLine(new BuildingTooltipText("limit")) };
            var adapter = new LegacyBuildingTooltipProvider(c => lines);
            var active = Context(TooltipTarget.BuildingCompact);
            var inactive = Context(TooltipTarget.BuildingDetailed, false);
            for (int i = 0; i < 100; i++) { adapter.Get(active, true); adapter.Get(inactive, false); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { adapter.Get(active, true); adapter.Get(inactive, false); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated);
        }
    }
}
