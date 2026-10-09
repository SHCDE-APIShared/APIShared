using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace APISharedTests
{
    public partial class RuntimeTests
    {
        [TestMethod] public void SavegameSettingsRoundTrip() => SavegameModSettingsTests.Run(Assert);
        [TestMethod] public void MarkedSelectionHarmonyContract() => MarkedSelectionHarmonyTests.Run(Assert);
    }
}
