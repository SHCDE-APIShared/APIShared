using System;
using System.Reflection;
using System.Runtime.Serialization;
using APIShared.Presentation;
using CrusaderDE;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class BuildingTooltipHudFieldTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        public void CompiledHudSelectionReadsMatchRealFieldsWithoutAllocating()
        {
            // Real HUD_Main fields from the installed assembly; no Unity constructor runs.
            var hud = (HUD_Main)FormatterServices.GetUninitializedObject(typeof(HUD_Main));
            var selection = new BuildingTooltipPresentation.HudSelection();
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo Field(string name) => typeof(HUD_Main).GetField(name, Flags);
            foreach (int hover in new[] { 0, 24 }) foreach (string hoverText in new[] { null, "", "Quarry" })
            foreach (int selected in new[] { 0, 18 }) foreach (string selectedText in new[] { null, "Wheat" })
            foreach (bool otherVisible in new[] { false, true }) foreach (bool otherPriority in new[] { false, true })
            foreach (bool detailed in new[] { false, true })
            {
                Field("HoverStruct").SetValue(hud, hover); Field("HoverString").SetValue(hud, hoverText);
                Field("SelectedStruct").SetValue(hud, selected); Field("SelectedString").SetValue(hud, selectedText);
                Field("OtherVisible").SetValue(hud, otherVisible); Field("OtherHighestPri").SetValue(hud, otherPriority);
                int expected = BuildingTooltipSelection.Resolve(hover, hoverText, selected, selectedText,
                    otherVisible, otherPriority, detailed, out bool expectedHover);
                Assert.AreEqual(expected, selection.Resolve(hud, detailed, out bool hovering));
                Assert.AreEqual(expectedHover, hovering);
            }
            Field("HoverStruct").SetValue(hud, 24); Field("HoverString").SetValue(hud, "Quarry");
            for (int i = 0; i < 2000; i++) selection.Resolve(hud, false, out _);
            long best = long.MaxValue;
            for (int sample = 0; sample < 5; sample++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 20000; i++) selection.Resolve(hud, false, out _);
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            TestContext.WriteLine("TOOLTIP_ALLOC HudSelection.Resolve: bytesPerCall={0:F1}", best / 20000.0);
            Assert.AreEqual(0L, best, "HUD selection reads must not box or reflect per update.");
        }
    }
}
