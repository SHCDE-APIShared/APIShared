using System;

namespace APIShared.Pathfinding
{
    /// <summary>The traversal permissions already selected by a route publisher. Events cannot broaden them.</summary>
    public enum RouteSearchTerrain
    {
        /// <summary>Only ordinary ground transitions are eligible.</summary>
        GroundOnly,
        /// <summary>Eligible ground and friendly or allied completed moat transitions.</summary>
        FriendlyMoat,
        /// <summary>The publisher's explicit diagnostic search may include enemy moat.</summary>
        DiagnosticMoat
    }

    /// <summary>Read-only identity and bounds of one additional weighted route search, without native pointers.</summary>
    public sealed class RouteSearchContext
    {
        internal RouteSearchContext(string sourceGuid, int playerId, int startX, int startY,
            int targetX, int targetY, RouteSearchTerrain terrain, int maximumEdges, long maximumEdgeCost)
        {
            SourceGuid = sourceGuid; PlayerId = playerId;
            StartX = startX; StartY = startY; TargetX = targetX; TargetY = targetY;
            Terrain = terrain; MaximumEdges = maximumEdges; MaximumEdgeCost = maximumEdgeCost;
        }
        /// <summary>The exact GUID of the mod publishing this operation.</summary>
        public string SourceGuid { get; }
        /// <summary>The one-based player game ID supplied by the publisher.</summary>
        public int PlayerId { get; }
        /// <summary>The starting tile X coordinate.</summary>
        public int StartX { get; }
        /// <summary>The starting tile Y coordinate.</summary>
        public int StartY { get; }
        /// <summary>The requested destination tile X coordinate.</summary>
        public int TargetX { get; }
        /// <summary>The requested destination tile Y coordinate.</summary>
        public int TargetY { get; }
        /// <summary>Existing traversal permissions; changing costs never permits a forbidden transition.</summary>
        public RouteSearchTerrain Terrain { get; }
        /// <summary>The publisher's existing route-length bound, which an observer cannot increase.</summary>
        public int MaximumEdges { get; }
        /// <summary>Largest permitted preference cost, bounded by the publisher to prevent path-sum overflow.</summary>
        public long MaximumEdgeCost { get; }
    }

    /// <summary>Scoped Pre arguments. Costs affect route preference, never native speed or transition validity.</summary>
    public sealed class RouteSearchPreEventArgs
    {
        private bool frozen, skip;
        private long ground, moat;
        internal RouteSearchPreEventArgs(RouteSearchContext context, long groundCost, long moatCost)
        { Context = context; ground = groundCost; moat = moatCost; }
        /// <summary>Read-only publisher, destination and traversal constraints for this operation.</summary>
        public RouteSearchContext Context { get; }
        /// <summary>Positive preference cost for an eligible ground edge; valid only during Pre callbacks.</summary>
        public long GroundEdgeCost { get => ground; set { RequirePre(); ground = value; } }
        /// <summary>Positive preference cost for an eligible moat edge; valid only during Pre callbacks.</summary>
        public long MoatEdgeCost { get => moat; set { RequirePre(); moat = value; } }
        /// <summary>Skips this additional weighted calculation. It does not cancel the underlying game command; no Post follows a skipped calculation.</summary>
        public bool SkipOriginalFunction { get => skip; set { RequirePre(); skip = value; } }
        private void RequirePre()
        { if (frozen) throw new InvalidOperationException("Route search Pre arguments have expired."); }
        internal void Freeze() { frozen = true; }
    }

    /// <summary>Immutable Post result of an executed additional route calculation. This notification cannot cancel publication.</summary>
    public sealed class RouteSearchPostEventArgs
    {
        internal RouteSearchPostEventArgs(RouteSearchContext context, long groundCost, long moatCost,
            bool success, string reason, int routeLength, int expandedNodes)
        {
            Context = context; GroundEdgeCost = groundCost; MoatEdgeCost = moatCost;
            Success = success; Reason = reason; RouteLength = routeLength; ExpandedNodes = expandedNodes;
        }
        /// <summary>The same immutable context used by Pre.</summary>
        public RouteSearchContext Context { get; }
        /// <summary>Effective ground preference cost used by the calculation.</summary>
        public long GroundEdgeCost { get; }
        /// <summary>Effective moat preference cost used by the calculation.</summary>
        public long MoatEdgeCost { get; }
        /// <summary>Whether the publisher produced a route that passed its existing live-edge checks.</summary>
        public bool Success { get; }
        /// <summary>The publisher's result description; treat it as diagnostic text rather than a stable enum.</summary>
        public string Reason { get; }
        /// <summary>Number of edges in the accepted route, or zero on failure.</summary>
        public int RouteLength { get; }
        /// <summary>Number of search nodes expanded for this operation.</summary>
        public int ExpandedNodes { get; }
    }
}
