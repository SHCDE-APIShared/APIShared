using APIShared;
using APIShared.Economy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class AutoTradeTests
    {
        [TestMethod]
        [DataRow(MissionStartKind.NewGame, 99, false, -1, true, false)]
        [DataRow(MissionStartKind.NewGame, 1, true, 0, true, true)]
        [DataRow(MissionStartKind.LoadedSave, 99, false, 1, true, true)]
        [DataRow(MissionStartKind.LoadedSave, 1, true, 0, true, false)]
        [DataRow(MissionStartKind.LoadedSave, 1, true, -1, false, false)]
        [DataRow(MissionStartKind.LoadedSave, 99, false, -1, false, false)]
        [DataRow(MissionStartKind.NewGame, 0, true, -1, false, false)]
        [DataRow(MissionStartKind.EditorLoaded, 99, true, 1, false, false)]
        public void ResolvesTheConfirmedOptionWithoutTreatingLoadDefaultsAsDecisions(
            MissionStartKind kind, int mode, bool nativeValue, int archive, bool known, bool allowed)
        {
            bool? archived = archive < 0 ? (bool?)null : archive != 0;
            Assert.AreEqual(known, AutoTrade.TrySelectPermission(kind, mode, nativeValue, archived, out bool permission));
            Assert.AreEqual(allowed, permission);
        }

        [TestMethod]
        public void GateValidationRejectsTheWrongModeBranchOrPermissionAddress()
        {
            byte[] gate = { 0x83, 0x3D, 0xCC, 0x47, 0x4A, 0x08, 0, 0x74, 0x0D,
                0x83, 0x3D, 0xF3, 0xC6, 0x71, 0x08, 0, 0x0F, 0x84, 0x18, 1, 0, 0 };
            Assert.IsTrue(AutoTrade.MatchesGate(gate));
            gate[7] = 0x75;
            Assert.IsFalse(AutoTrade.MatchesGate(gate));
            gate[7] = 0x74;
            gate[11]++;
            Assert.IsFalse(AutoTrade.MatchesGate(gate));
        }
    }
}
