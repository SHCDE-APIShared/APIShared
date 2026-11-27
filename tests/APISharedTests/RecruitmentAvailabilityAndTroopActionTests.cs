using System;
using System.Linq;
using APIShared.Presentation;
using APIShared.Recruitment;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public class RecruitmentAvailabilityAndTroopActionTests
    {
        // The pass mirrors Vanilla's GUI check, which rewrites recruit buttons only in these modes.
        private static readonly Enums.InBuildingModes[] RecruitmentModes =
        {
            Enums.InBuildingModes.INSIDE_BARRACKS,
            Enums.InBuildingModes.INSIDE_ENGINEERS_GUILD,
            Enums.InBuildingModes.INSIDE_TUNNELLERS_GUILD,
            Enums.InBuildingModes.INSIDE_MERCPOST,
            Enums.InBuildingModes.INSIDE_BEDOUIN_STOCKADE,
            Enums.InBuildingModes.INSIDE_CATHEDRAL,
        };

        [TestMethod]
        [TestCategory("Presentation")]
        public void RecruitmentPassRunsOnlyInVanillaRecruitmentModes()
        {
            foreach (Enums.InBuildingModes mode in Enum.GetValues(typeof(Enums.InBuildingModes)))
                Assert.AreEqual(RecruitmentModes.Contains(mode), RecruitmentAvailability.IsRecruitmentSubMode((int)mode),
                    "unexpected recruitment classification for " + mode);
            Assert.IsFalse(RecruitmentAvailability.IsRecruitmentSubMode(0), "no in-building mode is not a recruitment panel");
            Assert.IsFalse(RecruitmentAvailability.IsRecruitmentSubMode(-1), "invalid modes are not recruitment panels");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void IncompleteRecruitmentRegistrationsAreRejected()
        {
            Func<RecruitmentAvailabilityContext, SHCDESE.Interop.eChimps, bool> never = (context, type) => false;
            Assert.IsFalse(RecruitmentAvailability.TryRegister(null, "id", never, () => true, out string reason) || reason.Length == 0);
            Assert.IsFalse(RecruitmentAvailability.TryRegister("owner", " ", never, () => true, out reason) || reason.Length == 0);
            Assert.IsFalse(RecruitmentAvailability.TryRegister("owner", "id", null, () => true, out reason) || reason.Length == 0);
            Assert.IsFalse(RecruitmentAvailability.TryRegister("owner", "id", never, null, out reason) || reason.Length == 0);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void IncompleteTroopActionRegistrationsAreRejected()
        {
            Assert.IsFalse(TroopActionHud.TryRegister(null, "id", panel => { }, out string reason) || reason.Length == 0);
            Assert.IsFalse(TroopActionHud.TryRegister("owner", "", panel => { }, out reason) || reason.Length == 0);
            Assert.IsFalse(TroopActionHud.TryRegister("owner", "id", null, out reason) || reason.Length == 0);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void TroopActionLayoutKeepsTheCrossModSlotContract()
        {
            var knight = new TroopActionRequest("ExtraFeatures_Serp.KnightTransform", 100, true);
            var assassin = new TroopActionRequest("BugfixesAndQoL_Serp.AssassinClimb", 200, true);
            TroopActionLayoutDecision free = TroopActionHudLayoutPolicy.CreateDecision(new[] { assassin, knight }, false, false);
            CollectionAssert.AreEqual(new[] { "ExtraFeatures_Serp.KnightTransform:BottomRight", "BugfixesAndQoL_Serp.AssassinClimb:BottomMiddle" },
                free.Assignments.Select(value => value.ActionId + ":" + value.Slot).ToArray(), "priority order assigns the two free slots");
            TroopActionLayoutDecision rightTaken = TroopActionHudLayoutPolicy.CreateDecision(new[] { assassin, knight }, true, false);
            Assert.IsTrue(rightTaken.Assignments.Count == 1 && rightTaken.Assignments[0].ActionId == knight.ActionId &&
                rightTaken.Assignments[0].Slot == TroopActionSlot.BottomMiddle, "the remaining slot goes to the highest priority");
            TroopActionLayoutDecision duplicate = TroopActionHudLayoutPolicy.CreateDecision(new[]
            {
                new TroopActionRequest("Example.Duplicate", 100, true),
                new TroopActionRequest("Example.Duplicate", 200, true),
            }, false, false);
            Assert.IsTrue(duplicate.Assignments.Count == 0 && duplicate.DuplicateActionIds.SequenceEqual(new[] { "Example.Duplicate" }),
                "duplicate action IDs stay hidden");
        }
    }
}
