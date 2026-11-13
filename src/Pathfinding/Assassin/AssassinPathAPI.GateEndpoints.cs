using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace APIShared
{
    public static unsafe partial class AssassinPathAPI
    {
        /// <summary>Publishes the weighted builder's existing reconstruction relaxation as logical data only.</summary>
        /// <remarks>While this or <see cref="SetDirectGatehouseClimbing"/> is enabled, Assassin reconstruction also
        /// accepts an ordinary step by the start-side tile's connection bit, as the distance field and the physical
        /// step already do; a one-way connection no longer fails the route and triggers the full-map repair.
        /// A null or empty owner is rejected with <see cref="ArgumentException"/>.</remarks>
        public static void SetWeightedReconstructionEnabled(string ownerGuid, bool enabled)
            => SetWeightedReconstructionEnabled(ownerGuid, enabled, ignoreGoodsyardWallFlag: false);

        /// <summary>Publishes weighted reconstruction with an optional correction for stockpile wall flags.</summary>
        /// <remarks>When <paramref name="ignoreGoodsyardWallFlag"/> is true, a nonordinary fallback requires
        /// at least one endpoint with IsWall and without GoodsyardRelated. Stockpile flags alone cannot authorize
        /// climbing; a genuine wall at the other endpoint remains sufficient. Ordinary connections are unchanged.
        /// The weighted builder must enforce the same rule in its search. The two-argument overload retains its
        /// existing raw-IsWall behavior. Only the registered weighted-builder owner may change this policy;
        /// activation is process-wide, synchronous and changes aligned data, never published hook code.</remarks>
        public static void SetWeightedReconstructionEnabled(string ownerGuid, bool enabled, bool ignoreGoodsyardWallFlag)
        {
            if (string.IsNullOrEmpty(ownerGuid)) throw new ArgumentException("Owner GUID required.", nameof(ownerGuid));
            lock (Sync)
            {
                if (!string.Equals(weightedOwner, ownerGuid, StringComparison.Ordinal))
                    throw new InvalidOperationException("Only the registered weighted builder can change its reconstruction policy.");
                int weightedFlags = enabled ? 1 | (ignoreGoodsyardWallFlag ? 4 : 0) : 0;
                PublishFlags((flags & ~5) | weightedFlags);
            }
        }

        /// <summary>Enables or disables the registered experiment's gatehouse endpoint rule without repatching code.</summary>
        /// <remarks>Also enables the reconstruction connection rule described at <see cref="SetWeightedReconstructionEnabled(string, bool)"/>.</remarks>
        public static void SetDirectGatehouseClimbing(string ownerGuid, bool enabled)
        {
            if (string.IsNullOrEmpty(ownerGuid)) throw new ArgumentException("Owner GUID required.", nameof(ownerGuid));
            lock (Sync)
            {
                if (gateOwner != null && !string.Equals(gateOwner, ownerGuid, StringComparison.Ordinal))
                    throw new InvalidOperationException("Assassin gatehouse rule already belongs to " + gateOwner);
                EnsureInstalled();
                gateOwner = ownerGuid;
                PublishFlags(enabled ? flags | 2 : flags & ~2);
            }
        }

        /// <summary>
        /// Checks only the optional building-ID exception for one tile. Callers must still enforce
        /// Vanilla's direction, surface and wall requirements and their own player policy.
        /// </summary>
        public static bool IsDirectGatehouseClimbEndpoint(int tileId)
        {
            if (!IsInstalled || !DirectGatehouseClimbingEnabled || (uint)tileId >= TileCount) return false;
            ushort buildingId = ((ushort*)((byte*)module + BuildingGridRva))[tileId];
            if (buildingId == 0 || buildingId > 3999) return false;
            if ((((uint*)((byte*)module + TileFlagsRva))[tileId] & (uint)TilePropertyFlag.IsWall) == 0) return false;
            int record = buildingId * BuildingStride;
            short alive = *(short*)((byte*)module + BuildingAliveBaseRva + record);
            if (alive == (short)AliveState.None || alive == (short)AliveState.MarkedForDeletion) return false;
            eStructs type = *(eStructs*)((byte*)module + BuildingTypeBaseRva + record);
            return type == eStructs.STRUCT_GATE_MAIN || type == eStructs.STRUCT_GATE_INNER;
        }

        // TEMP_GATE_ROUTE_ACCEPTANCE: remove this native reader with the temporary observer.
        /// <summary>Reads the physical connection/surface branch only; never invokes a native step or changes state.</summary>
        public static AssassinTransitionKind ClassifyNativeTransition(int fromTile, int toTile, int direction, bool climbEnabled)
        {
            if (!supported || module == IntPtr.Zero || (uint)fromTile >= TileCount ||
                (uint)toTile >= TileCount || (uint)direction > 7) return AssassinTransitionKind.Unknown;
            byte* connections = (byte*)module + AssassinPathNativeDefinition.ConnectionsRva;
            byte* masks = (byte*)module + AssassinPathNativeDefinition.DirectionMasksRva;
            uint* surfaces = (uint*)((byte*)module + TileFlagsRva);
            ushort* buildings = (ushort*)((byte*)module + BuildingGridRva);
            // The special-surface predicate is deliberately not called by a read-only observer.
            bool surfaceAccepted = (surfaces[toTile] & 0x4A5014B1u) == 0;
            bool endpointsAccepted = (buildings[fromTile] == 0 || IsDirectGatehouseClimbEndpoint(fromTile)) &&
                (buildings[toTile] == 0 || IsDirectGatehouseClimbEndpoint(toTile));
            return AssassinGateTransitionPolicy.Classify(direction, connections[fromTile], connections[toTile],
                masks[direction], masks[direction ^ 4], surfaces[fromTile], surfaces[toTile],
                surfaceAccepted, climbEnabled, endpointsAccepted);
        }

        private static void PublishFlags(int value)
        {
            Marshal.WriteInt32(policyFlags, value); // Aligned data publication; executable memory never changes.
            Volatile.Write(ref flags, value);
        }
    }
}
