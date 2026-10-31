#nullable enable
using APIShared.Presentation;
using CrusaderDE;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noesis;

namespace Core.Tests
{
    [TestClass]
    public sealed class IngameMenuTooltipTests
    {
        [TestMethod]
        public void PopupMatchesActualButtonFontAcrossSeparateViewsWithoutDoubleScalingOrCompetingScaler()
        {
            var viewport = new Grid { ActualWidth = 1000, ScreenScaleX = 2, ScreenScaleY = 2 };
            var button = new Button { ScreenScaleX = 2, ScreenScaleY = 2, Template = new ControlTemplate() };
            button.Template.Parts["CenterText"] = new TextBlock { FontSize = 18, Height = 24 };
            button.Load();
            var tip = new ToolTip { ScalerEnabled = true }; tip.Load();
            var sizing = new IngameMenuTooltips(viewport, () => true);
            sizing.Attach(tip, button); sizing.Attach(tip, button);
            Assert.IsFalse(tip.ScalerEnabled);
            tip.IsOpen = true;
            Assert.AreEqual(36f, tip.FontSize); Assert.AreEqual(840d, tip.MaxWidth);
            Assert.AreEqual(20d, tip.Padding.Left); Assert.AreEqual(16d, tip.Padding.Top);
            Assert.AreEqual(3d, tip.BorderThickness.Top);
            Assert.AreEqual(18f, ((TextBlock)button.Template.Parts["CenterText"]).FontSize);
            int writes = tip.FontWrites;
            for (int i = 0; i < 100; i++) { tip.Resize(); tip.Load(); button.Resize(); viewport.Resize(); }
            Assert.AreEqual(writes, tip.FontWrites);
            tip.IsOpen = false; tip.ScreenScaleX = tip.ScreenScaleY = 2; tip.IsOpen = true;
            Assert.AreEqual(18f, tip.FontSize); Assert.AreEqual(420d, tip.MaxWidth);
            Assert.AreEqual(10d, tip.Padding.Left);
            tip.IsOpen = false; tip.ScreenScaleX = 1; tip.IsOpen = true;
            Assert.AreEqual(18f, tip.FontSize); Assert.AreEqual(840d, tip.MaxWidth);
            Assert.AreEqual(20d, tip.Padding.Left); Assert.AreEqual(8d, tip.Padding.Top);
            sizing.Detach(); Assert.IsFalse(tip.IsOpen); Assert.IsTrue(tip.ScalerEnabled);
        }
        [TestMethod]
        public void DelayedPopupWaitsForItsViewAndInvalidTransformsNeverWriteInvalidSizes()
        {
            var viewport = new Grid(); var button = new Button { ScreenScaleX = 3, ScreenScaleY = 3 }; button.Load();
            var tip = new ToolTip(); var sizing = new IngameMenuTooltips(viewport, () => true);
            sizing.Attach(tip, button); tip.IsOpen = true;
            Assert.AreEqual(16f, tip.FontSize); // Opened can run before the popup joins a view.
            tip.Load(); Assert.AreEqual(48f, tip.FontSize);
            foreach (float scale in new[] { 0f, float.NaN, float.PositiveInfinity })
            {
                tip.ScreenScaleY = scale; tip.Resize();
                Assert.AreEqual(48f, tip.FontSize); Assert.IsTrue(double.IsFinite(tip.MaxWidth));
            }
            tip.ScreenScaleY = 1.5f; tip.Resize(); Assert.AreEqual(32f, tip.FontSize);
            sizing.Detach();
        }
        [TestMethod]
        public void WidthUsesViewportPixelsAndClosesWithItsOwnerWithoutAffectingOtherPopups()
        {
            bool visible = true;
            var viewport = new Grid { ActualWidth = 300, ScreenScaleX = 2 };
            var button = new Button { ScreenScaleX = 2, ScreenScaleY = 2 }; button.Load();
            var tip = new ToolTip(); tip.Load();
            var unrelated = new ToolTip { FontSize = 7, ScalerEnabled = true }; unrelated.Load(); unrelated.IsOpen = true;
            var sizing = new IngameMenuTooltips(viewport, () => visible);
            sizing.Attach(tip, button); tip.IsOpen = true;
            Assert.AreEqual(560d, tip.MaxWidth); // (300 local units - 20 padding) * screen scale 2.
            viewport.ActualWidth = 200; viewport.Resize(); Assert.AreEqual(360d, tip.MaxWidth);
            viewport.ActualWidth = 10; viewport.Resize(); Assert.AreEqual(2d, tip.MaxWidth);
            sizing.CloseAll(); Assert.IsFalse(tip.IsOpen); Assert.IsTrue(unrelated.IsOpen);
            visible = false; tip.IsOpen = true; Assert.IsFalse(tip.IsOpen);
            visible = true; button.Visibility = Visibility.Collapsed; tip.IsOpen = true; Assert.IsFalse(tip.IsOpen);
            Assert.AreEqual(7f, unrelated.FontSize); Assert.IsTrue(unrelated.ScalerEnabled);
            sizing.Detach(); tip.FontSize = 9; tip.IsOpen = true; tip.Load(); tip.Resize(); button.Resize(); viewport.Resize();
            Assert.AreEqual(9f, tip.FontSize); // Old HUD events no longer own the popup.
        }
    }
}
