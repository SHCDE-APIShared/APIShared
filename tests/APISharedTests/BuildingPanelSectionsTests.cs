using System.Linq;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APISharedTests
{
    // Registration and showing rules of building panel sections; Noesis rendering is an in-game check.
    [TestClass]
    public sealed class BuildingPanelSectionsTests
    {
        private static readonly eStructs[] Tannery = { eStructs.STRUCT_TANNERS_WORKSHOP };

        private static BuildingPanelSectionOptions Options(string owner, string id, int order = 0, eStructs[] types = null) =>
            new BuildingPanelSectionOptions(owner, id, types ?? Tannery, _ => null) { Order = order };

        [TestMethod]
        [TestCategory("Presentation")]
        public void SectionsKeepAStableOrderAndRejectInvalidOrDuplicateIdentities()
        {
            var registry = new BuildingPanelSectionRegistry();
            Assert.IsFalse(registry.TryRegister(null, out _, out _));
            Assert.IsFalse(registry.TryRegister(Options("", "a"), out _, out _));
            Assert.IsFalse(registry.TryRegister(new BuildingPanelSectionOptions("o", "a", Tannery, null), out _, out _));
            Assert.IsFalse(registry.TryRegister(Options("o", "a", types: new eStructs[0]), out _, out _));
            Assert.IsTrue(registry.TryRegister(Options("z", "slider"), out var first, out _));
            var snapshot = registry.Entries;
            Assert.IsTrue(registry.TryRegister(Options("a", "slider"), out _, out _));
            Assert.IsTrue(registry.TryRegister(Options("z", "early", -1), out _, out _));
            Assert.IsFalse(registry.TryRegister(Options("z", "slider", 5), out _, out string reason));
            StringAssert.Contains(reason, "Duplicate");
            CollectionAssert.AreEqual(new[] { "z/early", "a/slider", "z/slider" },
                registry.Entries.Select(entry => entry.Owner + "/" + entry.Id).ToArray());
            Assert.AreEqual(1, snapshot.Length, "published snapshots are never modified");
            Assert.AreSame(first, snapshot[0]);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void OnlyBuildingSubModesIdentifyABuilding()
        {
            Assert.IsFalse(BuildingPanelSectionPolicy.IsBuildingSubMode(0), "INSIDE_NULL");
            Assert.IsTrue(BuildingPanelSectionPolicy.IsBuildingSubMode(1), "INSIDE_BARRACKS");
            Assert.IsTrue(BuildingPanelSectionPolicy.IsBuildingSubMode(17), "INSIDE_TANNERS_WORKSHOP");
            Assert.IsTrue(BuildingPanelSectionPolicy.IsBuildingSubMode(63), "INSIDE_BEDOUIN_STOCKADE");
            Assert.IsFalse(BuildingPanelSectionPolicy.IsBuildingSubMode(64), "INSIDE_PEACETIME");
            Assert.IsFalse(BuildingPanelSectionPolicy.IsBuildingSubMode(70), "INSIDE_CHIMP");
            Assert.IsFalse(BuildingPanelSectionPolicy.IsBuildingSubMode(71), "SUB_MODE_REPORTS");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void ShowingsFollowTheContextInstance()
        {
            var tannery = new BuildingPanelContext(7, 100, eStructs.STRUCT_TANNERS_WORKSHOP, 1, 1, 1, 1);
            var other = new BuildingPanelContext(8, 101, eStructs.STRUCT_TANNERS_WORKSHOP, 1, 1, 1, 1);
            Assert.AreEqual(BuildingPanelSectionTransition.None, BuildingPanelSectionPolicy.Plan(null, null, false));
            Assert.AreEqual(BuildingPanelSectionTransition.Open, BuildingPanelSectionPolicy.Plan(null, tannery, true));
            Assert.AreEqual(BuildingPanelSectionTransition.Update, BuildingPanelSectionPolicy.Plan(tannery, tannery, true));
            Assert.AreEqual(BuildingPanelSectionTransition.Reopen, BuildingPanelSectionPolicy.Plan(tannery, other, true));
            Assert.AreEqual(BuildingPanelSectionTransition.Close, BuildingPanelSectionPolicy.Plan(tannery, tannery, false));
            Assert.AreEqual(BuildingPanelSectionTransition.Close, BuildingPanelSectionPolicy.Plan(tannery, null, false));
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void ContextReportsLocalOwnershipOnlyForARealLocalPlayer()
        {
            Assert.IsTrue(new BuildingPanelContext(7, 100, eStructs.STRUCT_TANNERS_WORKSHOP, 2, 2, 2, 1).IsOwnedByLocalPlayer);
            Assert.IsFalse(new BuildingPanelContext(7, 100, eStructs.STRUCT_TANNERS_WORKSHOP, 2, 3, 2, 1).IsOwnedByLocalPlayer);
            Assert.IsFalse(new BuildingPanelContext(7, 100, eStructs.STRUCT_TANNERS_WORKSHOP, 0, 0, 2, 1).IsOwnedByLocalPlayer,
                "a spectator without a controlled player owns nothing");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void PresentedContextsActForThePresentersPlayerWithoutLocalOwnership()
        {
            var own = new BuildingPanelContext(7, 100, eStructs.STRUCT_GATE_MAIN, 2, 2, 2, 1);
            Assert.IsFalse(own.IsPresented);
            Assert.AreEqual(2, own.ActingPlayerId, "Vanilla's own selection acts for the local player");
            Assert.IsTrue(own.CanActAsOwner);
            var presented = new BuildingPanelContext(7, 100, eStructs.STRUCT_GATE_MAIN, 3, 0, 3, 1, 3, "spectator", "gates");
            Assert.IsTrue(presented.IsPresented);
            Assert.IsTrue(presented.CanActAsOwner);
            Assert.IsFalse(presented.IsOwnedByLocalPlayer, "presenting grants no local ownership");
            Assert.IsFalse(new BuildingPanelContext(7, 100, eStructs.STRUCT_GATE_MAIN, 3, 2, 3, 1, 4, "spectator", "gates").CanActAsOwner);
            Assert.IsTrue(presented.Same(7, 100, eStructs.STRUCT_GATE_MAIN, 3, 0, 3, 1, 3, "spectator", "gates"));
            Assert.IsFalse(presented.Same(7, 100, eStructs.STRUCT_GATE_MAIN, 3, 0, 3, 1, 3, "relay", "gates"),
                "another presenter starts a new showing");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void PresentersKeepAStableOrderAndRejectInvalidOrDuplicateIdentities()
        {
            BuildingPanelPresentationQuery none = (out int building, out int acting) => { building = acting = 0; return false; };
            BuildingPanelActionSubmit refuse = (APIShared.Buildings.BuildingActionRequest request, out string reason) => { reason = "no"; return false; };
            var registry = new BuildingPanelSectionRegistry();
            Assert.IsFalse(registry.TryRegisterPresenter(null, out _));
            Assert.IsFalse(registry.TryRegisterPresenter(new BuildingPanelPresenterOptions("o", "p", none, null), out _));
            Assert.IsFalse(registry.TryRegisterPresenter(new BuildingPanelPresenterOptions("o", " ", none, refuse), out _));
            Assert.IsTrue(registry.TryRegisterPresenter(new BuildingPanelPresenterOptions("z", "panel", none, refuse), out _));
            Assert.IsTrue(registry.TryRegisterPresenter(new BuildingPanelPresenterOptions("a", "panel", none, refuse), out _));
            Assert.IsTrue(registry.TryRegisterPresenter(new BuildingPanelPresenterOptions("z", "early", none, refuse) { Order = -1 }, out _));
            Assert.IsFalse(registry.TryRegisterPresenter(new BuildingPanelPresenterOptions("a", "panel", none, refuse), out string reason));
            StringAssert.Contains(reason, "Duplicate");
            CollectionAssert.AreEqual(new[] { "z/early", "a/panel", "z/panel" },
                registry.Presenters.Select(entry => entry.Owner + "/" + entry.Id).ToArray());
        }
    }
}
