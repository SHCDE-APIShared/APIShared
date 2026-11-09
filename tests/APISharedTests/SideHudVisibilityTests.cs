using APIShared.Presentation;
using CrusaderDE;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public class SideHudVisibilityTests
    {
        [TestMethod]
        [DataRow(true, true, false, SideHudVisibilityState.Visible)]
        [DataRow(false, true, true, SideHudVisibilityState.HiddenByHudMode)]
        [DataRow(false, true, false, SideHudVisibilityState.Hidden)]
        [DataRow(false, false, true, SideHudVisibilityState.Hidden)]
        [DataRow(true, false, false, SideHudVisibilityState.Hidden)]
        public void ClassifiesOnlyTheHudModeAsAnException(bool hudVisible, bool extrasEnabled,
            bool hudMode, SideHudVisibilityState expected)
        {
            Assert.AreEqual(expected, SideHudVisibility.Classify(hudVisible, extrasEnabled, hudMode));
        }

        [TestMethod]
        public void ValidatesTheInstalledGetterAndRejectsAnUnrelatedGetter()
        {
            var type = typeof(MainViewModel);
            var hudGetter = type.GetProperty(nameof(MainViewModel.Show_InGameUI)).GetGetMethod();
            var extrasGetter = type.GetProperty(nameof(MainViewModel.Show_HUD_Extras)).GetGetMethod();
            Assert.IsNotNull(SideHudVisibility.ResolveExtrasField(extrasGetter, hudGetter));
            Assert.IsNull(SideHudVisibility.ResolveExtrasField(extrasGetter,
                type.GetProperty(nameof(MainViewModel.Show_InGame)).GetGetMethod()));
            Assert.IsFalse(SideHudVisibility.TryGetState(null, out var state));
            Assert.AreEqual(SideHudVisibilityState.Hidden, state);
        }
    }
}
