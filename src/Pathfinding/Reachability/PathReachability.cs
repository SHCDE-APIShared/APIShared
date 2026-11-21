using System;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Pathfinding
{
    /// <summary>Answer of a <see cref="PathReachability"/> query.</summary>
    public enum PathReachabilityResult
    {
        /// <summary>No answer: invalid input, a tile outside the map, a dead or unknown unit, or the native query is unavailable.</summary>
        Unknown = 0,
        /// <summary>Vanilla's player-aware component graph has no connection. A reliable negative.</summary>
        Unreachable = 1,
        /// <summary>The components are connected for this player and mode. This is no proof that a detailed
        /// route exists: Vanilla's movement order stays authoritative for the actual path.</summary>
        Connected = 2
    }

    /// <summary>
    /// Coarse reachability between tiles or units through Vanilla's path connection layers (PCL), the
    /// player-aware component graph the game itself consults before it plans a route. It respects the
    /// player's own gates and the connection classes of the chosen mode (for example ladders).
    /// </summary>
    /// <remarks>
    /// <para>Thread and determinism: call synchronously on the simulation thread (a simulation tick, a
    /// simulation event or a lifecycle callback while the simulation is stopped). The native query updates
    /// Vanilla's own query bookkeeping, so in multiplayer use it only for decisions that every peer computes
    /// identically, and never inside a native hook between a Vanilla query and Vanilla's reading of its result.</para>
    /// <para>Cost: a tile lookup and, for different components, one native breadth-first search over the
    /// active connections. Callers that ask often should cache results for a short, deterministic time; this
    /// service keeps no cache because gates and buildings change the graph at any moment.</para>
    /// <para>Semantics: the same component is <see cref="PathReachabilityResult.Connected"/> without a native
    /// call, as in Vanilla. Component 0 (a tile without a walkable component) is unreachable, as Vanilla answers.
    /// Unit queries use the unit's controlling player and its live connection mode, the inputs Vanilla's own
    /// unit route searches use.</para>
    /// </remarks>
    public static unsafe class PathReachability
    {
        /// <summary>Reachability between two tiles for one player and connection mode.</summary>
        /// <param name="playerId">Player whose gates and permissions apply: 0 (neutral) to 8; others give Unknown.</param>
        /// <param name="mode">Connection classes to use.</param>
        /// <param name="fromX">Source tile X.</param>
        /// <param name="fromY">Source tile Y.</param>
        /// <param name="toX">Destination tile X.</param>
        /// <param name="toY">Destination tile Y.</param>
        public static PathReachabilityResult BetweenTiles(int playerId, PathConnectionQueryMode mode,
            int fromX, int fromY, int toX, int toY)
        {
            try
            {
                if (!IsValidPlayer(playerId) || !IsValidMode((int)mode) ||
                    !TryGetComponent(fromX, fromY, out int from) || !TryGetComponent(toX, toY, out int to))
                {
                    return PathReachabilityResult.Unknown;
                }

                return Query(playerId, mode, from, to);
            }
            catch (Exception error)
            {
                Report(error);
                return PathReachabilityResult.Unknown;
            }
        }

        /// <summary>Reachability from a live unit's tile to a tile, with the unit's player and connection mode.</summary>
        /// <param name="unitId">One-based unit ID of a living unit.</param>
        /// <param name="toX">Destination tile X.</param>
        /// <param name="toY">Destination tile Y.</param>
        public static PathReachabilityResult FromUnitToTile(int unitId, int toX, int toY)
        {
            try
            {
                if (!TryGetUnitInputs(unitId, out int playerId, out PathConnectionQueryMode mode, out int from) ||
                    !TryGetComponent(toX, toY, out int to))
                {
                    return PathReachabilityResult.Unknown;
                }

                return Query(playerId, mode, from, to);
            }
            catch (Exception error)
            {
                Report(error);
                return PathReachabilityResult.Unknown;
            }
        }

        /// <summary>Reachability from a live unit's tile to another unit's current tile.</summary>
        /// <param name="unitId">One-based unit ID of the living unit that would walk.</param>
        /// <param name="targetUnitId">One-based unit ID of the target; it may be dead (a corpse), but must exist.</param>
        public static PathReachabilityResult FromUnitToUnit(int unitId, int targetUnitId)
        {
            try
            {
                if (!TryGetUnitInputs(unitId, out int playerId, out PathConnectionQueryMode mode, out int from) ||
                    !UnitAccess.TryGetById(targetUnitId, out GameUnit* target, out _) ||
                    target->r_AliveState == AliveState.None ||
                    !TryGetComponent(target->r_CurrentTilePositionX, target->r_CurrentTilePositionY, out int to))
                {
                    return PathReachabilityResult.Unknown;
                }

                return Query(playerId, mode, from, to);
            }
            catch (Exception error)
            {
                Report(error);
                return PathReachabilityResult.Unknown;
            }
        }

        /// <summary>Reachability between two components read with <see cref="TryGetComponent"/>, for callers that cache them.</summary>
        /// <param name="playerId">Player whose gates and permissions apply: 0 (neutral) to 8; others give Unknown.</param>
        /// <param name="mode">Connection classes to use.</param>
        /// <param name="fromComponent">Source component; 0 is unreachable.</param>
        /// <param name="toComponent">Destination component; 0 is unreachable.</param>
        public static PathReachabilityResult BetweenComponents(int playerId, PathConnectionQueryMode mode,
            int fromComponent, int toComponent)
        {
            try
            {
                if (!IsValidPlayer(playerId) || !IsValidMode((int)mode) ||
                    fromComponent < 0 || fromComponent > ushort.MaxValue ||
                    toComponent < 0 || toComponent > ushort.MaxValue)
                {
                    return PathReachabilityResult.Unknown;
                }

                return Query(playerId, mode, fromComponent, toComponent);
            }
            catch (Exception error)
            {
                Report(error);
                return PathReachabilityResult.Unknown;
            }
        }

        /// <summary>Reads the path connection component of a tile; 0 means no walkable component.</summary>
        /// <returns>False for a tile outside the map or outside the validated component grid.</returns>
        public static bool TryGetComponent(int tileX, int tileY, out int componentId)
        {
            componentId = 0;
            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            if (tiles == null || !tiles.IsTileInsideMapBounds(tileX, tileY))
                return false;

            int tileId = tiles.GetTileId(tileX, tileY);
            if (!tiles.IsValidTileId(tileId))
                return false;

            Span<ushort> components = GamePathingManagerAPI.Instance.GetPathComponentGrid();
            if (!IndexBounds.IsValidIndex(tileId, components.Length))
                return false;

            componentId = components[tileId];
            return true;
        }

        private static bool TryGetUnitInputs(int unitId, out int playerId, out PathConnectionQueryMode mode, out int component)
        {
            playerId = 0;
            mode = PathConnectionQueryMode.ExcludeLadderClimb;
            component = 0;
            if (!UnitAccess.TryGetById(unitId, out GameUnit* unit, out _) ||
                !UnitAccess.IsReallyAlive(unit) ||
                !IsValidMode(unit->r_PathConnectionMode))
            {
                return false;
            }

            playerId = unit->r_ControllableForPlayerId;
            if (!IsValidPlayer(playerId))
                return false;
            mode = (PathConnectionQueryMode)unit->r_PathConnectionMode;
            return TryGetComponent(unit->r_CurrentTilePositionX, unit->r_CurrentTilePositionY, out component);
        }

        private static PathReachabilityResult Query(int playerId, PathConnectionQueryMode mode, int from, int to)
        {
            if (from == 0 || to == 0)
                return PathReachabilityResult.Unreachable;
            if (from == to)
                return PathReachabilityResult.Connected;
            // The Script Extender answers 0 when it did not resolve the native query: no negative then.
            if (SHCDESE.Detours.BulkPathingDetours.c_game_pathfinding_find_next_component_toward_destinaton == null)
            {
                Report(new InvalidOperationException("The Script Extender did not resolve the native component query."));
                return PathReachabilityResult.Unknown;
            }

            return GamePathingManagerAPI.Instance.FindNextComponentTowardDestination(playerId, from, to, mode) != 0
                ? PathReachabilityResult.Connected
                : PathReachabilityResult.Unreachable;
        }

        // Neutral player 0 or a player slot; the native query indexes per-player tables.
        private static bool IsValidPlayer(int playerId) => playerId >= 0 && playerId <= GamePlayerManagerAPI.MAX_PLAYERS;

        // The Script Extender answers 0 for an unknown mode, which would read as a false negative.
        private static bool IsValidMode(int mode) =>
            mode >= (int)PathConnectionQueryMode.ExcludeLadderClimb && mode <= (int)PathConnectionQueryMode.LadderClimbOnly;

        private static int failureReported;

        private static void Report(Exception error)
        {
            if (System.Threading.Interlocked.Exchange(ref failureReported, 1) == 0)
                NativeApiLog.Error(ApiSharedRuntime.ProcessInstance.Logger, "PathReachability query failed; callers receive Unknown: " + error);
        }
    }
}
