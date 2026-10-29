using System.Collections.Generic;
using APIShared;
using APIShared.Pathfinding;

namespace ThirdPartyMod
{
    // Route engines avoid hazard tiles; Vanilla's own searches are unaffected. Call Update on the
    // simulation thread with tiles derived from deterministic game state (identical on all peers).
    internal static class RouteTileRulesExample
    {
        private static RouteTileRuleSet hazards;
        private static readonly HashSet<int> marked = new HashSet<int>();

        internal static bool Register(ModApiClient api, out string reason)
        {
            if (!RouteTileRules.TryRegister(api.OwnerGuid, "hazards", out hazards, out reason)) return false;
            // Moving units re-plan 3 steps before a new hazard; a detour may add 50 % (at least 10
            // steps), otherwise they wait up to about 10 s at normal speed.
            hazards.ReplanPolicy = new RouteReplanPolicy(3, 50, 10, 400);
            return true;
        }

        // burning: current hazard tiles packed as y * RouteTileRules.MapSize + x.
        internal static void Update(ICollection<int> burning)
        {
            if (hazards == null) return;
            foreach (int tile in burning)
                if (marked.Add(tile)) hazards.Set(tile % RouteTileRules.MapSize, tile / RouteTileRules.MapSize, RouteTileEffect.Penalty, 40);
            marked.RemoveWhere(tile =>
            {
                if (burning.Contains(tile)) return false;
                hazards.Clear(tile % RouteTileRules.MapSize, tile / RouteTileRules.MapSize);
                return true;
            });
        }

        // A new map: the old map's tiles no longer apply.
        internal static void Reset() { hazards?.ClearAll(); marked.Clear(); }
    }
}
