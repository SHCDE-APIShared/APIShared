using System;
using APIShared.Units.AiMilitary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    [TestClass]
    public sealed unsafe class AiMilitaryCatalogTests
    {
        [TestMethod]
        public void SemanticRolesBindTheAuditedNativeDomainWithoutUsingStorageIndices()
        {
            // These numbers are the hash-bound native unit-role domain, not implementation
            // ordinals. A refactor must preserve their meaning for the native classifiers.
            var pairs = new[] {
                (AiMilitaryRole.None, 65535), (AiMilitaryRole.CastleDefense, 1),
                (AiMilitaryRole.DefensePatrol, 4), (AiMilitaryRole.HomeMoat, 5),
                (AiMilitaryRole.EconomyProtection, 6), (AiMilitaryRole.Intervention, 7),
                (AiMilitaryRole.Harassment, 2), (AiMilitaryRole.HarassmentEngineers, 22),
                (AiMilitaryRole.SiegeEngineers, 10), (AiMilitaryRole.SiegeMoat, 11),
                (AiMilitaryRole.SiegeAssassins, 12), (AiMilitaryRole.SiegeDiversion, 13),
                (AiMilitaryRole.SiegeLadders, 14), (AiMilitaryRole.SiegeTunnelers, 15),
                (AiMilitaryRole.SiegeStorm, 16), (AiMilitaryRole.SiegeCover, 17),
                (AiMilitaryRole.SiegeShock, 18), (AiMilitaryRole.SiegeReserve, 19), (AiMilitaryRole.SiegeWall, 20) };
            foreach (var pair in pairs) Assert.AreEqual(pair.Item2, (int)AiMilitaryCatalog.NativeRole(pair.Item1), pair.Item1.ToString());
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AiMilitaryCatalog.NativeRole((AiMilitaryRole)255));
        }

        [TestMethod]
        public void GroupWindowsAndFirstZeroWallListFollowTheNativeConfigurationBounds()
        {
            var aic = new InternalAIC();
            var archer = eChimps.CHIMP_TYPE_ARCHER;
            aic.defense_patrols = 0;
            Assert.AreEqual(AiMilitaryReason.InvalidGroupCount, AiMilitaryCatalog.ValidateAic(AiMilitaryRole.DefensePatrol, archer, in aic));
            aic.defense_patrols = 10;
            Assert.AreEqual(AiMilitaryReason.None, AiMilitaryCatalog.ValidateAic(AiMilitaryRole.DefensePatrol, archer, in aic));
            aic.defense_patrols = 11;
            Assert.AreEqual(AiMilitaryReason.InvalidGroupCount, AiMilitaryCatalog.ValidateAic(AiMilitaryRole.DefensePatrol, archer, in aic));
            aic.siege_storm_troop = (int)archer; aic.siege_storm_tribes = 2;
            Assert.AreEqual(AiMilitaryReason.None, AiMilitaryCatalog.ValidateAic(AiMilitaryRole.SiegeStorm, archer, in aic));
            aic.siege_storm_tribes = 0;
            Assert.AreEqual(AiMilitaryReason.InvalidGroupCount, AiMilitaryCatalog.ValidateAic(AiMilitaryRole.SiegeStorm, archer, in aic));
            aic.siege_cover_troop = aic.siege_reserve_troop = (int)archer;
            aic.siege_cover_tribes = aic.siege_reserve_tribes = aic.siege_wall_tribes = -1;
            aic.siege_wall_troops1 = (int)archer;
            foreach (var role in new[] { AiMilitaryRole.SiegeCover, AiMilitaryRole.SiegeReserve, AiMilitaryRole.SiegeWall })
                Assert.AreEqual(AiMilitaryReason.None, AiMilitaryCatalog.ValidateAic(role, archer, in aic), role.ToString());
            aic.siege_cover_tribes = 4; aic.siege_reserve_tribes = 3; aic.siege_wall_tribes = 9;
            foreach (var role in new[] { AiMilitaryRole.SiegeCover, AiMilitaryRole.SiegeReserve, AiMilitaryRole.SiegeWall })
                Assert.AreEqual(AiMilitaryReason.InvalidGroupCount, AiMilitaryCatalog.ValidateAic(role, archer, in aic), role.ToString());
            aic.siege_wall_tribes = 1; aic.siege_wall_troops1 = 0; aic.siege_wall_troops2 = (int)archer;
            Assert.AreEqual(AiMilitaryReason.AicMismatch, AiMilitaryCatalog.ValidateAic(AiMilitaryRole.SiegeWall, archer, in aic));
        }

        [TestMethod]
        public void EditorDefersFinalAicButStillRefusesUncrewedMachinesAndWrongSpecialists()
        {
            var option = AiMilitaryCatalog.Option(1, eChimps.CHIMP_TYPE_ARCHER, AiMilitaryRole.SiegeStorm, true);
            Assert.IsTrue(option.IsAvailable && option.IsDeferred, "Editor declarations do not depend on its temporary AI profile.");
            option = AiMilitaryCatalog.Option(1, eChimps.CHIMP_TYPE_CATAPULT, AiMilitaryRole.Harassment, true);
            Assert.IsFalse(option.IsAvailable); Assert.AreEqual(AiMilitaryReason.IncompleteCrew, option.Reason);
            Assert.IsTrue(AiMilitaryCatalog.Option(1, eChimps.CHIMP_TYPE_CATAPULT, AiMilitaryRole.None, true).IsAvailable);
            Assert.IsFalse(AiMilitaryCatalog.SupportsType(AiMilitaryRole.SiegeAssassins, eChimps.CHIMP_TYPE_ARCHER));
            Assert.IsTrue(AiMilitaryCatalog.SupportsType(AiMilitaryRole.SiegeAssassins, eChimps.CHIMP_TYPE_ARAB_ASSASIN));
            Assert.IsFalse(AiMilitaryCatalog.SupportsType(AiMilitaryRole.SiegeLadders, eChimps.CHIMP_TYPE_ENGINEER));
            Assert.IsTrue(AiMilitaryCatalog.SupportsType(AiMilitaryRole.SiegeLadders, eChimps.CHIMP_TYPE_LADDERMAN));
            Assert.IsTrue(AiMilitaryCatalog.SupportsType(AiMilitaryRole.SiegeTunnelers, eChimps.CHIMP_TYPE_TUNNELER));
            Assert.IsFalse(AiMilitaryCatalog.SupportsType(AiMilitaryRole.SiegeShock, eChimps.CHIMP_TYPE_BEDOUIN_HEALER));
            Assert.IsTrue(AiMilitaryCatalog.SupportsType(AiMilitaryRole.SiegeCover, eChimps.CHIMP_TYPE_BEDOUIN_HEALER));
            Assert.IsFalse(AiMilitaryCatalog.CanDig(eChimps.CHIMP_TYPE_SWORDSMAN));
            Assert.IsTrue(AiMilitaryCatalog.CanDig(eChimps.CHIMP_TYPE_BEDOUIN_SAPPER));
        }
    }
}
