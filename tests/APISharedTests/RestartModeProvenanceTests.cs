using APIShared.GameModes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class RestartModeProvenanceTests
    {
        private static GameModeSnapshot Mode(GameModeKind kind, GameModeLaunchVariant variant,
            bool multiplayer = false, bool locked = false, int members = 0, bool save = false,
            int trail = -1, int mission = -1, int origin = 0) =>
            new GameModeSnapshot(multiplayer, !multiplayer, false, true, false, save, true, multiplayer, true,
                multiplayer, multiplayer, 0, 0, 0, members, members, -1, -1, -1, 0, kind, variant,
                0, -1, trail, mission, origin, locked);

        [TestMethod]
        [DataRow(GameModeKind.CustomTrail)]
        [DataRow(GameModeKind.CoopTrail)]
        [DataRow(GameModeKind.VanillaTrail)]
        [DataRow(GameModeKind.SandsOfTime)]
        public void RestartRetainsAuthorizationWithoutOriginProviderButUsesFreshNetworkState(GameModeKind kind)
        {
            foreach (var variant in new[] { GameModeLaunchVariant.Customized, GameModeLaunchVariant.RestoredCustomizedSave })
            foreach (bool multiplayer in new[] { false, true })
            foreach (bool locked in new[] { false, true })
            {
                var source = Mode(kind, variant, multiplayer, locked, 4, true, 7, 8, 2);
                var live = Mode(kind, GameModeLaunchVariant.Standard, multiplayer, false, 2);
                var restart = live.WithRestartProvenance(source);
                Assert.AreEqual(variant, restart.LaunchVariant);
                Assert.AreEqual(7, restart.CustomizedTrailId);
                Assert.AreEqual(8, restart.CustomizedMissionId);
                Assert.AreEqual(2, restart.CustomizedOriginKind);
                Assert.AreEqual(locked, restart.HasConflictingCustomizedOrigin);
                Assert.AreEqual(!locked, restart.AllowsCustomGameMods);
                Assert.AreEqual(2, restart.GameMembers);
                Assert.AreEqual(multiplayer, restart.IsRealMultiplayer);
                Assert.IsFalse(restart.MultiplayerSave, "A restart is a new game, not another save load.");
                Assert.AreEqual(GameModeLaunchVariant.Standard, live.LaunchVariant,
                    "Restart provenance changed an unrelated native snapshot.");
            }
        }

        [TestMethod]
        [DataRow(GameModeKind.CustomTrail)]
        [DataRow(GameModeKind.CoopTrail)]
        [DataRow(GameModeKind.VanillaTrail)]
        [DataRow(GameModeKind.SandsOfTime)]
        public void GenericNativeSkirmishCarrierRetainsTheKnownTrailOrigin(GameModeKind kind)
        {
            foreach (var variant in new[] { GameModeLaunchVariant.Standard, GameModeLaunchVariant.RestoredCustomizedSave })
            {
                var source = Mode(kind, variant);
                var native = Mode(GameModeKind.CustomGame, GameModeLaunchVariant.Standard);
                var restart = native.WithRestartProvenance(source);
                Assert.AreEqual(kind, restart.Kind);
                Assert.AreEqual(variant, restart.LaunchVariant);
                Assert.AreEqual(variant != GameModeLaunchVariant.Standard, restart.AllowsCustomGameMods);
            }
        }

        [TestMethod]
        public void MissingNativeEvidenceCannotInheritAnOpenCustomGameGate()
        {
            var source = Mode(GameModeKind.CustomGame, GameModeLaunchVariant.Standard);
            var unknown = Mode(GameModeKind.Unknown, GameModeLaunchVariant.Standard).WithRestartProvenance(source);
            Assert.AreEqual(GameModeKind.Unknown, unknown.Kind);
            Assert.IsTrue(unknown.HasConflictingCustomizedOrigin);
            Assert.IsFalse(unknown.AllowsCustomGameMods);
        }

        [TestMethod]
        public void NativeConflictAndDifferentModeCannotAcquireRestartPermission()
        {
            var source = Mode(GameModeKind.CustomTrail, GameModeLaunchVariant.Customized);
            var conflicting = Mode(GameModeKind.CustomTrail, GameModeLaunchVariant.Standard, locked: true).WithRestartProvenance(source);
            Assert.IsTrue(conflicting.HasConflictingCustomizedOrigin);
            Assert.IsFalse(conflicting.AllowsCustomGameMods);
            var different = Mode(GameModeKind.Campaign, GameModeLaunchVariant.Standard).WithRestartProvenance(source);
            Assert.AreEqual(GameModeKind.Campaign, different.Kind);
            Assert.AreEqual(GameModeLaunchVariant.Standard, different.LaunchVariant);
            Assert.IsTrue(different.HasConflictingCustomizedOrigin);
            Assert.IsFalse(different.AllowsRegularGameplayMods);
        }
    }
}
