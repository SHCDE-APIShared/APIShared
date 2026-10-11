using System;
using SHCDESE.Interop;

namespace APIShared
{
    internal static unsafe class AssassinObstacleCompletion
    {
        internal static void Complete(GameUnit* unit, int unitId, IntPtr tribeManager,
            bool nativeRetarget, AssassinAttackNativeContract.RetargetDelegate selectTarget)
        {
            // 16D573..16D5A6: clear the finished obstacle before objective-based selection.
            unit->r_AI_ContextTargetBuildingTileId = 0;
            try
            {
                if (!nativeRetarget || selectTarget(tribeManager, unitId, (int)unit->r_AIObjectiveRole2) == 0)
                    unit->r_AIState = 0;
            }
            catch
            {
                // A cleared target must never remain in damage state 107 after a managed failure.
                unit->r_AIState = 0;
                throw;
            }
            finally { unit->r_AnimationTimer = 0; }
        }
    }
}
