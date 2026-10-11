using System;
using System.Runtime.InteropServices;
using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    [TestClass]
    public unsafe class AssassinObstacleCompletionTests
    {
        [TestMethod]
        public void LegacyCompletionDoesNotCallTargetSelection()
        {
            GameUnit unit=default;
            unit.r_AIState=107;unit.r_AI_ContextTargetBuildingTileId=45;unit.r_AnimationTimer=9;
            AssassinObstacleCompletion.Complete(&unit,37,IntPtr.Zero,false,(m,id,r)=>{Assert.Fail("Legacy retarget");return 0;});
            Assert.AreEqual((ushort)0,unit.r_AIState);
            Assert.AreEqual(0u,unit.r_AI_ContextTargetBuildingTileId);
            Assert.AreEqual(0u,unit.r_AnimationTimer);
        }
        [TestMethod]
        public void NativeCompletionPreservesPublishedStateAndSecondObjective()
        {
            IntPtr memory=Marshal.AllocHGlobal(sizeof(GameUnit));
            GameUnit* unit=(GameUnit*)memory;
            try
            {
                *unit=default;unit->r_AIState=107;unit->r_AI_ContextTargetBuildingTileId=45;unit->r_AnimationTimer=9;
                unit->r_AIObjectiveRole=AITribeObjectiveRole.MoveToEnemyGatehouse;
                unit->r_AIObjectiveRole2=AITribeObjectiveRole.SiegeAssassins;
                int calls=0;
                AssassinObstacleCompletion.Complete(unit,37,new IntPtr(1234),true,(m,id,r)=>
                {
                    calls++;Assert.AreEqual(new IntPtr(1234),m);Assert.AreEqual(37,id);
                    Assert.AreEqual((int)AITribeObjectiveRole.SiegeAssassins,r);
                    Assert.AreEqual(0u,unit->r_AI_ContextTargetBuildingTileId);
                    unit->r_AIState=101;unit->r_AI_ContextTargetBuildingTileId=78;return 1;
                });
                Assert.AreEqual(1,calls);Assert.AreEqual((ushort)101,unit->r_AIState);
                Assert.AreEqual(78u,unit->r_AI_ContextTargetBuildingTileId);Assert.AreEqual(0u,unit->r_AnimationTimer);
                Assert.AreEqual(new IntPtr(0x2FE),Marshal.OffsetOf(typeof(GameUnit),nameof(GameUnit.r_AIObjectiveRole2)));
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        [TestMethod]
        public void FailureAndExceptionLeaveSafeIdleState()
        {
            GameUnit unit=default;unit.r_AIState=107;unit.r_AI_ContextTargetBuildingTileId=45;unit.r_AnimationTimer=9;
            AssassinObstacleCompletion.Complete(&unit,37,IntPtr.Zero,true,(m,id,r)=>0);
            Assert.AreEqual((ushort)0,unit.r_AIState);Assert.AreEqual(0u,unit.r_AnimationTimer);
            unit.r_AIState=107;unit.r_AI_ContextTargetBuildingTileId=45;unit.r_AnimationTimer=9;
            bool caught=false;
            try { AssassinObstacleCompletion.Complete(&unit,37,IntPtr.Zero,true,(m,id,r)=>{throw new InvalidOperationException();}); }
            catch(InvalidOperationException) { caught=true; }
            Assert.IsTrue(caught);Assert.AreEqual((ushort)0,unit.r_AIState);
            Assert.AreEqual(0u,unit.r_AI_ContextTargetBuildingTileId);Assert.AreEqual(0u,unit.r_AnimationTimer);
        }
        [TestMethod]
        public void BothPublicOverloadsRemainAndInvalidModeDoesNotInstall()
        {
            Assert.IsNotNull(typeof(AssassinAttackControlAPI).GetMethod("RegisterGuard",new[] { typeof(string),typeof(Func<int,bool>) }));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>AssassinAttackControlAPI.RegisterGuard("test",_=>false,(AssassinObstacleCompletionMode)99));
        }
    }
}
