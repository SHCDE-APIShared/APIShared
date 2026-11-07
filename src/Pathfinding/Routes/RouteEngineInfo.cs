using System;

namespace APIShared.Pathfinding
{
    /// <summary>Route rule contracts a route engine applies to the routes it publishes.</summary>
    [Flags]
    public enum RouteEngineFeatures
    {
        /// <summary>No rule contract.</summary>
        None = 0,
        /// <summary><see cref="RouteTileRules"/> effects (forbidden tiles and penalties).</summary>
        TileRules = 1,
        /// <summary><see cref="RouteTileRuleSet.ReplanPolicy"/> for units already walking.</summary>
        ReplanPolicies = 2,
        /// <summary><see cref="RouteSearchEvents"/> edge costs.</summary>
        RouteCosts = 4
    }

    /// <summary>
    /// Tells rule mods whether a route engine currently applies their rules. Rules stay valid without an
    /// engine, they just have no effect, so this is for diagnostics and option texts; it never changes
    /// how rules work. One engine reports at a time; another owner's report is refused until it clears.
    /// </summary>
    /// <remarks>Reads are safe from any thread and return one consistent report. An engine reports before a
    /// mission loads and keeps its report for the mission. Not synchronized between players.</remarks>
    public static class RouteEngineInfo
    {
        private sealed class Report
        {
            internal string Owner;
            internal RouteEngineFeatures Features;
        }

        private static readonly object gate = new object();
        private static volatile Report current;

        /// <summary>Whether an engine currently applies rules.</summary>
        public static bool IsActive => current != null;

        /// <summary>The reporting engine's owner GUID, or null without an engine.</summary>
        public static string OwnerGuid => current?.Owner;

        /// <summary>The contracts the current engine applies; <see cref="RouteEngineFeatures.None"/> without an engine.</summary>
        public static RouteEngineFeatures Features => current?.Features ?? RouteEngineFeatures.None;

        /// <summary>Reports or updates this owner's engine.</summary>
        /// <param name="ownerGuid">The engine mod's GUID.</param>
        /// <param name="features">The contracts it applies now.</param>
        /// <param name="reason">Why the report was refused, otherwise null.</param>
        /// <returns>False for an empty GUID or while another owner reports; nothing changes then.</returns>
        public static bool TryReport(string ownerGuid, RouteEngineFeatures features, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(ownerGuid)) { reason = "Owner GUID is required."; return false; }
            lock (gate)
            {
                Report active = current;
                if (active != null && !string.Equals(active.Owner, ownerGuid, StringComparison.Ordinal))
                {
                    reason = "Route engine '" + active.Owner + "' is already active.";
                    return false;
                }
                current = new Report { Owner = ownerGuid, Features = features };
                return true;
            }
        }

        /// <summary>Withdraws this owner's report; another owner's report is left alone.</summary>
        /// <param name="ownerGuid">The engine mod's GUID.</param>
        public static void Clear(string ownerGuid)
        {
            lock (gate)
            {
                if (current != null && string.Equals(current.Owner, ownerGuid, StringComparison.Ordinal))
                    current = null;
            }
        }
    }
}
