using System;
using SHCDESE.Interop;

namespace APIShared.Buildings
{
    /// <summary>Read-only checks for the current automatic gatehouse closing rule.</summary>
    public static class GatehouseClosingRules
    {
        // The audited native square/perimeter tables cover sizes 1 through 13 (C0270).
        internal const int MaximumNativeGridSize = 13;

        /// <summary>Checks whether distance alone rules out this gatehouse candidate.</summary>
        /// <remarks>
        /// Call synchronously with live records during the simulation's gatehouse query.
        /// The configured rule must stay stable during that query. The larger of the two
        /// role-specific limits is used, so a false result still requires candidate evaluation.
        /// Missing or unvalidated native state also returns false. This check does not
        /// acquire ownership, authorize a close, or test reachability.
        /// </remarks>
        public static bool IsDefinitelyOutsideRange(in GameBuilding gate, in GameUnit candidate) =>
            ApiSharedRuntime.ProcessInstance.TryReadGatehouseClosingRange(
                out GatehouseDistanceOrigin origin, out int maximumNativeDistance) &&
            IsDefinitelyOutsideRange(in gate, in candidate, origin, maximumNativeDistance);

        internal static bool IsDefinitelyOutsideRange(
            in GameBuilding gate, in GameUnit candidate, GatehouseDistanceOrigin origin, int maximumNativeDistance)
        {
            if (maximumNativeDistance <= 0)
                return false;

            int beginX = (short)gate.r_TilePositionXBegin;
            int beginY = (short)gate.r_TilePositionYBegin;
            int centerX;
            int centerY;
            if (origin == GatehouseDistanceOrigin.BuildingBoundsCenter)
            {
                uint gridSize = (uint)gate.r_OccupyTileGridSize;
                if (gridSize > MaximumNativeGridSize ||
                    !BuildingGeometry.TryGetSquareFootprintCenter(beginX, beginY, (int)gridSize, out centerX, out centerY))
                    return false;
            }
            else if (origin == GatehouseDistanceOrigin.VanillaBuildingBegin)
            {
                centerX = beginX * BuildingGeometry.NativeUnitsPerTile;
                centerY = beginY * BuildingGeometry.NativeUnitsPerTile;
            }
            else
            {
                return false;
            }

            return Math.Abs(centerX - (short)candidate.r_CurrentWorldPositionX) >= maximumNativeDistance ||
                   Math.Abs(centerY - (short)candidate.r_CurrentWorldPositionY) >= maximumNativeDistance;
        }
    }
}
