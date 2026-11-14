using System;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Sight
{
    /// <summary>
    /// Vanilla's line-of-sight test between two points: the same native routines the game uses to decide whether
    /// a unit sees another one (for example the Hunter's target search and every ranged attack order).
    /// </summary>
    /// <remarks>
    /// <para>What it answers: Vanilla traces the straight line between the two points in world steps and stops at the
    /// first terrain or building height above the line (see docs/LINE_OF_SIGHT.md for the model). The result is
    /// Vanilla's raw value: <c>0</c> when blocked (also when the target is reached after a single step),
    /// otherwise the number of steps traced, at most <see cref="StepLimitResult"/>, and
    /// <see cref="SamePointResult"/> for identical X and Y. How Vanilla's callers read it differs; use
    /// <see cref="IsVisibleForTargetQuery"/> or <see cref="IsVisibleForDirectAttack"/> or a rule of your own.
    /// Building types registered and enabled through <see cref="SightBlockingBuildings"/> block like ordinary
    /// buildings, because the test reads Vanilla's own building height helper.</para>
    /// <para>Thread and determinism: call synchronously on the simulation thread (a simulation tick, a simulation
    /// event or native hook, or a lifecycle callback while the simulation is stopped). The query reads map tiles
    /// and buildings that the simulation thread writes; another thread may observe a half-updated map. The result
    /// depends only on map state and the inputs, so peers that ask the same question in the same tick get the same
    /// answer.</para>
    /// <para>Side effects: none on game state. Vanilla's routines write a scratch value into a context record; the
    /// service passes a private zeroed record on the stack instead of Vanilla's global one and checks that only the
    /// documented slot changed. If Vanilla ever writes elsewhere, the service disables itself for the process
    /// (<see cref="LineOfSightFailure.NativeContractViolation"/>, then <see cref="LineOfSightFailure.Unavailable"/>).</para>
    /// <para>Reentrancy: a query started from inside another query on the same thread (for example from a native hook
    /// on the traced routines) is refused with <see cref="LineOfSightFailure.Reentrant"/>.</para>
    /// <para>Availability: bound to the audited native image (SHA-256 FBCB9319...); on any other image the service
    /// stays unavailable with a logged reason and every query fails closed. Ready after APIShared's native
    /// initialization (LibraryLoaded). Queries allocate nothing; each costs one native call (two for a blocked
    /// <see cref="LineOfSightRoutine.Bidirectional"/> query) over at most a thousand steps.</para>
    /// </remarks>
    public static unsafe class LineOfSight
    {
        /// <summary>World units per tile on each axis.</summary>
        public const int TileWorldSize = 8;

        /// <summary>Offset of a tile centre from the tile's first world coordinate on each axis.</summary>
        public const int TileCenterOffset = 4;

        /// <summary>
        /// Eye height Vanilla adds to a viewing unit: the Hunter's target search (RVA 0x18AF50, add 0x1E at
        /// 0x18B070) and the default of the ranged attack order (RVA 0x18E9A0, 0x1E stored at 0x18E9DE; two unit types
        /// raise it to 45 or 60 there).
        /// </summary>
        public const int ViewerEyeHeight = 30;

        /// <summary>
        /// Height Vanilla adds to a target unit in the same two callers (add 0x1A at 0x18B052 and 0x18ED3C).
        /// </summary>
        public const int TargetHeight = 26;

        /// <summary>Result of identical viewer and target X and Y.</summary>
        public const int SamePointResult = 100000;

        /// <summary>Largest step count Vanilla returns: tracing stops there and the target counts as seen.</summary>
        public const int StepLimitResult = 1000;

        /// <summary>
        /// Largest result the Hunter's target search accepts as visible (RVA 0x18B0A7: <c>result - 1</c> compared
        /// unsigned with 0x1AF), which also limits that search to 432 steps.
        /// </summary>
        public const int TargetQueryMaximumResult = 0x1B0;

        /// <summary>Largest accepted height magnitude: inside it Vanilla's height interpolation cannot overflow.</summary>
        public const int MaximumHeightMagnitude = 100000;

        /// <summary>Whether queries can run.</summary>
        public static bool IsAvailable => LineOfSightNative.IsAvailable;

        /// <summary>Empty while available; otherwise why the service is unavailable.</summary>
        public static string UnavailableReason => LineOfSightNative.UnavailableReason;

        /// <summary>Runs Vanilla's sight test between two world points.</summary>
        /// <param name="viewer">Start of the trace (the eye).</param>
        /// <param name="target">End of the trace.</param>
        /// <param name="routine">Bidirectional wrapper or directional core.</param>
        /// <param name="result">Vanilla's raw result; 0 when the query failed.</param>
        /// <param name="failure"><see cref="LineOfSightFailure.None"/> on success; otherwise why no query ran.</param>
        /// <returns>True when Vanilla answered; <paramref name="result"/> is then valid.</returns>
        public static bool TryEvaluate(SightPoint viewer, SightPoint target, LineOfSightRoutine routine,
            out int result, out LineOfSightFailure failure) =>
            LineOfSightNative.TryEvaluate(viewer, target, routine, out result, out failure);

        /// <summary>The Hunter target search's rule: visible for results 1 through <see cref="TargetQueryMaximumResult"/>.</summary>
        /// <param name="result">A raw result of a successful query.</param>
        public static bool IsVisibleForTargetQuery(int result) => unchecked((uint)(result - 1)) < TargetQueryMaximumResult;

        /// <summary>The ranged attack order's rule (RVA 0x18ED71: <c>test eax,eax; jle</c>): every positive result is visible.</summary>
        /// <param name="result">A raw result of a successful query.</param>
        public static bool IsVisibleForDirectAttack(int result) => result > 0;

        /// <summary>
        /// Reads a unit's sight point as Vanilla does: world X and Y, and the signed sum of the unit's height
        /// (<c>r_HeightElevation</c>) and its height offset (<c>GameUnit +0xB8</c>), plus <paramref name="heightOffset"/>.
        /// </summary>
        /// <param name="unit">An immediate valid unit record, for example from <see cref="UnitAccess"/>.</param>
        /// <param name="heightOffset">Usually <see cref="ViewerEyeHeight"/> for a viewer and <see cref="TargetHeight"/> for a target.</param>
        /// <param name="point">The point.</param>
        /// <returns>Always true; the shape matches the ID overload.</returns>
        public static bool TryGetUnitPoint(in GameUnit unit, int heightOffset, out SightPoint point)
        {
            // Vanilla reads all four words with movsx (RVA 0x18B043 onwards).
            point = new SightPoint(
                unchecked((short)unit.r_CurrentWorldPositionX),
                unchecked((short)unit.r_CurrentWorldPositionY),
                unchecked((short)unit.r_HeightElevation) + unit.N0000006A + heightOffset);
            return true;
        }

        /// <summary>Like <see cref="TryGetUnitPoint(in GameUnit, int, out SightPoint)"/> for a one-based unit ID.</summary>
        /// <param name="unitId">One-based unit ID of an existing unit record (alive state not None).</param>
        /// <param name="heightOffset">Height added to the unit's own height.</param>
        /// <param name="point">The point on success.</param>
        public static bool TryGetUnitPoint(int unitId, int heightOffset, out SightPoint point)
        {
            point = default;
            return UnitAccess.TryGetById(unitId, out GameUnit* unit, out _) &&
                unit->r_AliveState != AliveState.None &&
                TryGetUnitPoint(in *unit, heightOffset, out point);
        }

        /// <summary>
        /// A viewpoint standing on a tile: its centre (<c>tile * 8 + 4</c> on both axes) at the tile's height from the
        /// tile height grid plus <paramref name="heightOffset"/>, for example to test a place a unit could move to.
        /// </summary>
        /// <param name="tileX">Tile X.</param>
        /// <param name="tileY">Tile Y.</param>
        /// <param name="heightOffset">Usually <see cref="ViewerEyeHeight"/>.</param>
        /// <param name="point">The point on success.</param>
        /// <returns>False for a tile outside the current map.</returns>
        public static bool TryGetTileViewpoint(int tileX, int tileY, int heightOffset, out SightPoint point)
        {
            point = default;
            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            if (tiles == null || !tiles.IsTileInsideMapBounds(tileX, tileY))
                return false;

            int tileId = tiles.GetTileId(tileX, tileY);
            if (!tiles.IsValidTileId(tileId))
                return false;

            point = new SightPoint(
                tileX * TileWorldSize + TileCenterOffset,
                tileY * TileWorldSize + TileCenterOffset,
                tiles.GetTileHeight(tileId) + heightOffset);
            return true;
        }

        /// <summary>Sight from one unit to another with Vanilla's offsets (<see cref="ViewerEyeHeight"/>, <see cref="TargetHeight"/>).</summary>
        /// <param name="viewer">Immediate valid record of the viewing unit.</param>
        /// <param name="target">Immediate valid record of the target unit.</param>
        /// <param name="routine">Bidirectional wrapper or directional core.</param>
        /// <param name="result">Vanilla's raw result; 0 when the query failed.</param>
        /// <param name="failure">Why no query ran, or None.</param>
        public static bool TryEvaluateUnits(in GameUnit viewer, in GameUnit target, LineOfSightRoutine routine,
            out int result, out LineOfSightFailure failure) =>
            TryEvaluateUnits(in viewer, ViewerEyeHeight, in target, TargetHeight, routine, out result, out failure);

        /// <summary>Sight from one unit to another with explicit height offsets.</summary>
        /// <param name="viewer">Immediate valid record of the viewing unit.</param>
        /// <param name="viewerHeightOffset">Added to the viewer's own height.</param>
        /// <param name="target">Immediate valid record of the target unit.</param>
        /// <param name="targetHeightOffset">Added to the target's own height.</param>
        /// <param name="routine">Bidirectional wrapper or directional core.</param>
        /// <param name="result">Vanilla's raw result; 0 when the query failed.</param>
        /// <param name="failure">Why no query ran, or None.</param>
        public static bool TryEvaluateUnits(in GameUnit viewer, int viewerHeightOffset, in GameUnit target, int targetHeightOffset,
            LineOfSightRoutine routine, out int result, out LineOfSightFailure failure)
        {
            TryGetUnitPoint(in viewer, viewerHeightOffset, out SightPoint from);
            TryGetUnitPoint(in target, targetHeightOffset, out SightPoint to);

            return LineOfSightNative.TryEvaluate(from, to, routine, out result, out failure);
        }

        /// <summary>Sight between two units by one-based ID, with Vanilla's offsets.</summary>
        /// <param name="viewerUnitId">One-based ID of an existing viewing unit (alive state not None).</param>
        /// <param name="targetUnitId">One-based ID of an existing target unit (alive state not None).</param>
        /// <param name="routine">Bidirectional wrapper or directional core.</param>
        /// <param name="result">Vanilla's raw result; 0 when the query failed.</param>
        /// <param name="failure">Why no query ran, or None.</param>
        public static bool TryEvaluateUnits(int viewerUnitId, int targetUnitId, LineOfSightRoutine routine,
            out int result, out LineOfSightFailure failure)
        {
            if (!TryGetUnitPoint(viewerUnitId, ViewerEyeHeight, out SightPoint from) ||
                !TryGetUnitPoint(targetUnitId, TargetHeight, out SightPoint to))
            {
                result = 0;
                failure = LineOfSightFailure.InvalidUnit;
                return false;
            }

            return LineOfSightNative.TryEvaluate(from, to, routine, out result, out failure);
        }
    }
}
