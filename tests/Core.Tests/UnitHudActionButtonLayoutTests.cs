using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    [TestClass]
    public class UnitHudActionButtonLayoutTests
    {
        [TestMethod]
        public void FitsOnlyWholeButtonsWithGaps()
        {
            Assert.AreEqual(9, UnitHudActionButtonLayout.Capacity(377, 12));
            Assert.AreEqual(1, UnitHudActionButtonLayout.Capacity(100, 61));
            Assert.AreEqual(0, UnitHudActionButtonLayout.Capacity(100, 62));
            Assert.AreEqual(0, UnitHudActionButtonLayout.Capacity(100, 120));
            Assert.AreEqual(0, UnitHudActionButtonLayout.Capacity(float.NaN, 0));
        }

        [TestMethod]
        public void CyclesPagesAndClampsAfterHidingOrResizing()
        {
            Assert.AreEqual(3, UnitHudActionButtonLayout.PageCount(19, 9));
            Assert.AreEqual(1, UnitHudActionButtonLayout.NextPage(0, 19, 9));
            Assert.AreEqual(2, UnitHudActionButtonLayout.NextPage(1, 19, 9));
            Assert.AreEqual(0, UnitHudActionButtonLayout.NextPage(2, 19, 9));
            Assert.AreEqual(0, UnitHudActionButtonLayout.ClampPage(2, 9, 9));
            Assert.AreEqual(1, UnitHudActionButtonLayout.ClampPage(2, 19, 18));
            Assert.AreEqual(0, UnitHudActionButtonLayout.PageCount(0, 9));
            Assert.AreEqual(0, UnitHudActionButtonLayout.NextPage(2, 19, 0));
        }

        [TestMethod]
        public void ForeignHudCannotBorrowTheVanillaShowFlag()
        {
            Assert.IsTrue(UnitHudActionButtonLayout.OwnContext(true, true, true, 1, 1, 3));
            Assert.IsFalse(UnitHudActionButtonLayout.OwnContext(true, true, false, 0, 1, 3));
            Assert.IsFalse(UnitHudActionButtonLayout.OwnContext(true, true, true, 0, 1, 3));
            Assert.IsFalse(UnitHudActionButtonLayout.OwnContext(true, true, true, 1, 1, 0));
            Assert.IsFalse(UnitHudActionButtonLayout.OwnContext(true, true, true, 1, -1, 3));
            Assert.IsFalse(UnitHudActionButtonLayout.OwnContext(false, true, true, 1, 1, 3));
            Assert.IsFalse(UnitHudActionButtonLayout.OwnContext(true, false, true, 1, 1, 3));
        }
    }
}
