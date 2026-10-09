using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    [TestClass]
    public class UnitHudHoverTextTests
    {
        [TestMethod]
        public void UpdatesLocalizedTextAndClosesOnlyItsOwnRollover()
        {
            var presenter = new UnitHudHoverTextPresenter();
            object hud = new object(), formation = new object(), arrow = new object();
            string displayed = ""; bool shown = false; int updates = 0;
            void Show(object source, string text) => presenter.Show(hud, source, text, () => displayed,
                value => { displayed = value; shown = true; updates++; }, () => shown = false);
            Show(formation, "Arrangement"); Show(formation, "Arrangement");
            Assert.AreEqual(1, updates);
            Show(formation, "Aufstellung"); Assert.AreEqual("Aufstellung", displayed);
            Show(arrow, "2 / 3 →"); presenter.Close(formation);
            Assert.IsTrue(shown, "Leaving the previous button cannot hide the arrow's text.");
            presenter.Close(arrow); Assert.IsFalse(shown);
            object category = new object(); Show(category, "Controlled Lord");
            presenter.Close(formation); presenter.Close(arrow);
            Assert.IsTrue(shown, "Hiding the action strip cannot clear a visible unit category rollover.");
            Show(formation, "Arrangement"); displayed = "Vanilla building cost";
            presenter.Close(); Assert.IsTrue(shown, "Another control's replacement rollover stays visible.");
        }
        [TestMethod]
        public void ForeignHudAndHudRecreationCloseAndRecoverWithoutStaleOwners()
        {
            var presenter = new UnitHudHoverTextPresenter();
            object oldHud = new object(), newHud = new object(), button = new object();
            string oldText = "", newText = ""; bool oldShown = false, newShown = false;
            presenter.Show(oldHud, button, "Formation", () => oldText,
                value => { oldText = value; oldShown = true; }, () => oldShown = false);
            presenter.Close(); Assert.IsFalse(oldShown); Assert.IsFalse(presenter.IsOwnedBy(oldHud));
            presenter.Show(newHud, button, "Formation", () => newText,
                value => { newText = value; newShown = true; }, () => newShown = false);
            Assert.IsTrue(newShown); Assert.IsTrue(presenter.IsOwnedBy(newHud));
            presenter.Close(); Assert.IsFalse(newShown);
        }
        [TestMethod]
        public void RecruitmentRolloverRestoresPreviousNameButPreservesVanillaTakeover()
        {
            var presenter = new UnitHudHoverTextPresenter();
            object hud = new object(), previous = new object(), next = new object();
            string displayed = "Archer";
            void Show(object source, string text)
            {
                string original = null;
                presenter.Show(hud, source, text, () => displayed,
                    value => { original = displayed; displayed = value; }, () => displayed = original);
            }
            Show(previous, "Previous variant"); Show(previous, "Vorherige Variante");
            presenter.Close(); Assert.AreEqual("Archer", displayed);
            Show(next, "Next variant"); displayed = "Vanilla changed selection";
            presenter.Close(); Assert.AreEqual("Vanilla changed selection", displayed);
        }
    }
}
