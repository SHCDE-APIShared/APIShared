using APIShared;
using APIShared.Commands;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System.Runtime.InteropServices;

namespace APISharedTests
{
    // The helper writes native unit memory. These checks keep it fail-closed on another image or
    // interop layout and restrict the write to the living members Vanilla's own clearing writes.
    [TestClass]
    public unsafe class GroupMoveExclusionTests
    {
        [StructLayout(LayoutKind.Explicit, Size = 0x490)]
        private struct ShiftedMark
        {
            [FieldOffset(0x88)] public AliveState r_AliveState;
            [FieldOffset(0x29C)] public ushort r_IsKilledByProjectile;
            [FieldOffset(0x42A)] public ushort N000000D8;
        }

        [StructLayout(LayoutKind.Explicit, Size = 0x490)]
        private struct WideMark
        {
            [FieldOffset(0x88)] public AliveState r_AliveState;
            [FieldOffset(0x29C)] public ushort r_IsKilledByProjectile;
            [FieldOffset(0x428)] public uint N000000D8;
        }

        private static GroupMoveExclusionState Available()
        {
            var state = GroupMoveExclusionState.Validate(ApiSharedRuntime.SupportedHash, typeof(GameUnit), typeof(AliveState));
            Assert.IsTrue(state.IsAvailable, state.Reason);
            return state;
        }

        [TestMethod]
        public void InstalledLayoutIsAcceptedAndOtherImagesOrLayoutsAreRejected()
        {
            Available();
            Assert.IsFalse(GroupMoveExclusionState.Validate(new string('0', 64), typeof(GameUnit), typeof(AliveState)).IsAvailable);
            Assert.IsFalse(GroupMoveExclusionState.Validate(null, typeof(GameUnit), typeof(AliveState)).IsAvailable);
            Assert.IsFalse(GroupMoveExclusionState.Validate(ApiSharedRuntime.SupportedHash, typeof(ShiftedMark), typeof(AliveState)).IsAvailable);
            Assert.IsFalse(GroupMoveExclusionState.Validate(ApiSharedRuntime.SupportedHash, typeof(WideMark), typeof(AliveState)).IsAvailable);
        }

        [TestMethod]
        public void ClearsOnlyTheMarkOfLivingUnits()
        {
            var state = Available();
            GameUnit unit = new GameUnit { r_AliveState = AliveState.IsAlive, N000000D8 = 1, N000000D9 = 7 };
            Assert.IsTrue(GroupMoveExclusion.TryClear(state, &unit, out bool wasMarked) && wasMarked);
            Assert.AreEqual((ushort)0, unit.N000000D8);
            Assert.AreEqual((ushort)7, unit.N000000D9, "other unit fields stay untouched");
            Assert.IsTrue(GroupMoveExclusion.TryClear(state, &unit, out wasMarked) && !wasMarked, "an unmarked unit is reported, not rewritten");

            unit = new GameUnit { r_AliveState = AliveState.IsAlive, r_IsKilledByProjectile = 1, N000000D8 = 1 };
            Assert.IsFalse(GroupMoveExclusion.TryClear(state, &unit, out wasMarked) || wasMarked, "dying unit");
            Assert.AreEqual((ushort)1, unit.N000000D8);
            unit = new GameUnit { r_AliveState = AliveState.NeedsInit, N000000D8 = 1 };
            Assert.IsFalse(GroupMoveExclusion.TryClear(state, &unit, out wasMarked) || wasMarked, "unit not yet alive");
            Assert.AreEqual((ushort)1, unit.N000000D8);
            unit = new GameUnit { r_AliveState = AliveState.IsAlive, N000000D8 = 1 };
            Assert.IsFalse(GroupMoveExclusion.TryClear(GroupMoveExclusionState.Unavailable("test"), &unit, out wasMarked) || wasMarked);
            Assert.AreEqual((ushort)1, unit.N000000D8, "an unavailable helper writes nothing");
            Assert.IsFalse(GroupMoveExclusion.TryClear(state, null, out wasMarked) || wasMarked);
        }
    }
}
