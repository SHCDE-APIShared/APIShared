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
        public void CastleDefenseWindowClosesExactlyWhereVanillasAllocatorReturnsNoGroup()
        {
            // In-game 17.11.2026: Rat with AIV spearman positions and a zero mobile-group window got
            // "Native defensive allocation failed. class=8 positions=3 returnedGroup=0" after the
            // role had been offered. 2BC40 cuts only classes 8/10/17 and only for a positive count.
            var mobile = new[] { (eChimps.CHIMP_TYPE_SPEARMAN, 8), (eChimps.CHIMP_TYPE_MACEMAN, 10), (eChimps.CHIMP_TYPE_ARAB_HORSEMAN, 17) };
            foreach (var (type, cls) in mobile)
            {
                Assert.IsTrue(APIShared.Units.AiMilitary.Native.AiCastleDefenseTables.TryGetMobileWindowClass((int)type, out int actual), type.ToString());
                Assert.AreEqual(cls, actual, type.ToString());
            }
            foreach (var type in new[] { eChimps.CHIMP_TYPE_ARCHER, eChimps.CHIMP_TYPE_PIKEMAN, eChimps.CHIMP_TYPE_KNIGHT,
                eChimps.CHIMP_TYPE_ARAB_SLINGER, eChimps.CHIMP_TYPE_BEDOUIN_CAMEL_LANCER })
                Assert.IsFalse(APIShared.Units.AiMilitary.Native.AiCastleDefenseTables.TryGetMobileWindowClass((int)type, out _), type.ToString());
            Assert.IsTrue(APIShared.Units.AiMilitary.Native.AiCastleDefenseTables.IsDefenseWindowClosed(3, 0));
            Assert.IsTrue(APIShared.Units.AiMilitary.Native.AiCastleDefenseTables.IsDefenseWindowClosed(1, -1));
            Assert.IsFalse(APIShared.Units.AiMilitary.Native.AiCastleDefenseTables.IsDefenseWindowClosed(3, 1), "window cut to one group");
            Assert.IsFalse(APIShared.Units.AiMilitary.Native.AiCastleDefenseTables.IsDefenseWindowClosed(0, 0), "no positions: the fallback slot always yields a group");
        }

        [TestMethod]
        public void ControllerHandoffCoversHarassmentAndEverySiegeStateWithItsWaitDetail()
        {
            // In-game 18.11.2026: after a save/load, harassment and idle siege groups had lost their
            // recorded order through ordinary member routes and every recruit was rejected, although
            // 3E200 and 3C2E0 command those groups as a whole. User decision 18.11.2026: siege recruits
            // join in every siege state and wait for the next phase where 3C2E0 does not reach them sooner.
            Func<AiMilitaryRole, int, bool> commands = (role, state) => APIShared.Units.AiMilitary.Orders.AiControllerHandoffPolicy.CommandsJoinedMembers(role, state);
            Func<AiMilitaryRole, int, bool> prompt = (role, state) => APIShared.Units.AiMilitary.Orders.AiControllerHandoffPolicy.SiegeControllerReachesIdleMember(role, state);
            foreach (var role in new[] { AiMilitaryRole.CastleDefense, AiMilitaryRole.DefensePatrol, AiMilitaryRole.HomeMoat,
                AiMilitaryRole.EconomyProtection, AiMilitaryRole.Intervention, AiMilitaryRole.Harassment })
                Assert.IsTrue(commands(role, 5) && commands(role, -1), role.ToString());
            var siege = new[] { AiMilitaryRole.SiegeEngineers, AiMilitaryRole.SiegeMoat, AiMilitaryRole.SiegeAssassins,
                AiMilitaryRole.SiegeDiversion, AiMilitaryRole.SiegeLadders, AiMilitaryRole.SiegeTunnelers, AiMilitaryRole.SiegeStorm,
                AiMilitaryRole.SiegeCover, AiMilitaryRole.SiegeShock, AiMilitaryRole.SiegeReserve, AiMilitaryRole.SiegeWall };
            foreach (var role in siege)
            {
                for (int state = 0; state <= 9; state++) Assert.IsTrue(commands(role, state), role + " siege state " + state);
                // An unreadable or out-of-domain siege state stays an explicit rejection.
                Assert.IsFalse(commands(role, -1) || commands(role, 10), role + " unknown siege state");
                Assert.IsTrue(prompt(role, 0), role + " idle siege state");
                foreach (int state in new[] { 1, 2, 6, 7, 8, 9 }) Assert.IsFalse(prompt(role, state), role + " state " + state);
            }
            // 3C2E0 reaches an idle joined member: storm 3CE40 (3-5), shock 3CDC0/122B40 (4-5),
            // state-5 dispatch of moat/assassins/diversion/cover/reserve/wall; ladders, tunnelers and
            // engineers only at a later transition.
            Assert.IsTrue(prompt(AiMilitaryRole.SiegeStorm, 3) && prompt(AiMilitaryRole.SiegeStorm, 4) && prompt(AiMilitaryRole.SiegeStorm, 5));
            Assert.IsTrue(!prompt(AiMilitaryRole.SiegeShock, 3) && prompt(AiMilitaryRole.SiegeShock, 4) && prompt(AiMilitaryRole.SiegeShock, 5));
            foreach (var role in new[] { AiMilitaryRole.SiegeMoat, AiMilitaryRole.SiegeAssassins, AiMilitaryRole.SiegeDiversion,
                AiMilitaryRole.SiegeCover, AiMilitaryRole.SiegeReserve, AiMilitaryRole.SiegeWall })
                Assert.IsTrue(!prompt(role, 3) && !prompt(role, 4) && prompt(role, 5), role.ToString());
            foreach (var role in new[] { AiMilitaryRole.SiegeLadders, AiMilitaryRole.SiegeTunnelers, AiMilitaryRole.SiegeEngineers })
                Assert.IsFalse(prompt(role, 3) || prompt(role, 4) || prompt(role, 5), role.ToString());
            Assert.AreEqual(APIShared.Units.AiMilitary.Orders.AiControllerHandoffPolicy.SiegeWaitDetail,
                APIShared.Units.AiMilitary.Orders.AiControllerHandoffPolicy.Detail(AiMilitaryRole.SiegeLadders, 5));
            Assert.AreEqual(APIShared.Units.AiMilitary.Orders.AiControllerHandoffPolicy.AppliedDetail,
                APIShared.Units.AiMilitary.Orders.AiControllerHandoffPolicy.Detail(AiMilitaryRole.SiegeStorm, 4));
            Assert.AreEqual(APIShared.Units.AiMilitary.Orders.AiControllerHandoffPolicy.AppliedDetail,
                APIShared.Units.AiMilitary.Orders.AiControllerHandoffPolicy.Detail(AiMilitaryRole.Harassment, 3));
            Assert.IsFalse(commands(AiMilitaryRole.HarassmentEngineers, 0) || commands(AiMilitaryRole.None, 0));
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
