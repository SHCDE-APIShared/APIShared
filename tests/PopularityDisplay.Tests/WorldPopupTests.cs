using APIShared.ScriptExtenderFixes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WorldPopupTests
{
    [TestMethod]
    public void PopupUsesNativeFortyAndSixtyThresholdsFromItsOwnSnapshot()
    {
        foreach (int priorMask in new[] { 0, 128, 129, 130, 171 })
        {
            foreach (int value in new[] { 0, 39 }) Assert.AreEqual(130, WorldPopularityPolicy.Normalize(true, priorMask, value));
            foreach (int value in new[] { 40, 49, 50, 59 }) Assert.AreEqual(129, WorldPopularityPolicy.Normalize(true, priorMask, value));
            foreach (int value in new[] { 60, 98, 100 }) Assert.AreEqual(128, WorldPopularityPolicy.Normalize(true, priorMask, value));
            foreach (int value in new[] { 0, 39, 40, 59, 60, 98, 100 })
                Assert.AreEqual(priorMask, WorldPopularityPolicy.Normalize(false, priorMask, value), "Other popup icons remain Vanilla.");
        }
    }
}
