using System;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using CorrectedPathing = APIShared.ScriptExtenderReplacements.GamePathingManagerAPI;

namespace ThirdPartyMod
{
    internal static class PathfindingReplacementExample
    {
        // Call after APIShared readiness, on the appropriate game thread. This example only reads.
        internal static void Inspect(Action<string> log)
        {
            var pathing = CorrectedPathing.Instance;
            if (!pathing.IsAvailable)
            {
                log("Pathfinding tables unavailable: " + pathing.UnavailabilityReason);
                return;
            }
            var permissions = pathing.GetUnitTypePathfindingConnectionClassPermissions(PathConnectionClass.Connection2);
            bool archerAllowed = pathing.CanUnitTypeUsePathConnectionClass(
                eChimps.CHIMP_TYPE_ARCHER, PathConnectionClass.Connection2);
            log("Permission row length=" + permissions.Length + "; archer allowed=" + archerAllowed);
        }
    }
}
