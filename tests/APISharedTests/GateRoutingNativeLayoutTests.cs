using System;
using APIShared.GateRouting.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class GateRoutingNativeLayoutTests
    {
        [TestMethod]
        public void PublicLayoutForwardsTheAuditedDefinition()
        {
            Assert.AreEqual(EnemyGatePathfindingNativeDefinition.ReferenceSha256, GateRoutingNativeLayout.ReferenceSha256);
            Assert.AreEqual(EnemyGatePathfindingNativeDefinition.MapGridWidth, GateRoutingNativeLayout.MapGridWidth);
            Assert.AreEqual(EnemyGatePathfindingNativeDefinition.MaximumTileIdExclusive, GateRoutingNativeLayout.MaximumTileIdExclusive);
            Assert.AreEqual(EnemyGatePathfindingNativeDefinition.PclGraphCapturedByFilterRva, GateRoutingNativeLayout.PclGraphCapturedByFilterRva);
            Assert.AreEqual(EnemyGatePathfindingNativeDefinition.BuilderPrecheckCapturedByFilterHookLength, GateRoutingNativeLayout.BuilderPrecheckCapturedByFilterHookLength);
            Assert.AreEqual(EnemyGatePathfindingNativeDefinition.RecordThirdPclOffset, GateRoutingNativeLayout.RecordThirdPclOffset);
            Assert.AreEqual(EnemyGatePathfindingNativeDefinition.CursorMouseTileIdRva, GateRoutingNativeLayout.CursorMouseTileIdRva);
        }

        [TestMethod]
        public void ValidationFailsClosedOutsideTheImage()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => GateRoutingNativeLayout.ValidateComparisonSites(new byte[16]));
            Assert.ThrowsExactly<InvalidOperationException>(() => GateRoutingNativeLayout.ValidateMovementSites(new byte[16], true));
        }
    }
}
