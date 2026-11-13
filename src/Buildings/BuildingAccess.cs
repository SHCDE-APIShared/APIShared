using SHCDESE.API;
using SHCDESE.Interop;

namespace APIShared
{
    /// <summary>Why a building game ID could not be resolved; not an identity or life-state verdict.</summary>
    public enum BuildingLookupFailure
    {
        /// <summary>The lookup succeeded.</summary>
        None,
        /// <summary>The ID is zero or negative, so it is not a building game ID.</summary>
        InvalidId,
        /// <summary>No building manager is available.</summary>
        ManagerUnavailable,
        /// <summary>The ID exceeds the manager's current capacity.</summary>
        OutOfRange,
        /// <summary>The manager could not return a non-null pointer.</summary>
        Unavailable
    }

    /// <summary>Checked, silent access to the Script Extender's one-based building IDs.</summary>
    /// <remarks>Call under the existing game-thread/simulation synchronization contract. Returned pointers
    /// are immediate views and must not be cached across simulation work or mission changes. Success
    /// checks neither identity, ownership nor life state, including NeedsInit; callers retain these checks.
    /// No rejected lookup is logged. Consumers decide whether the returned failure warrants diagnostics.</remarks>
    public static unsafe class BuildingAccess
    {
        /// <summary>Resolves a building ID without accessing the manager for a nonpositive ID.</summary>
        /// <param name="buildingId">One-based game ID, never a zero-based span index.</param>
        /// <param name="building">The immediate pointer on success; null on every failure.</param>
        /// <param name="failure">The failure reason, or None on success.</param>
        /// <returns>True only when the current manager returns a non-null pointer.</returns>
        public static bool TryGetById(int buildingId, out GameBuilding* building, out BuildingLookupFailure failure)
        {
            building = null;
            if (buildingId <= 0) { failure = BuildingLookupFailure.InvalidId; return false; }
            return TryGetById(GameBuildingManagerAPI.Instance, buildingId, out building, out failure);
        }

        /// <summary>Resolves a building ID using the caller's already captured manager.</summary>
        /// <param name="manager">The captured manager; null is safely rejected.</param>
        /// <param name="buildingId">One-based game ID, passed unchanged to the manager.</param>
        /// <param name="building">The immediate pointer on success; null on every failure.</param>
        /// <param name="failure">The failure reason, or None on success.</param>
        /// <returns>True only when the ID fits the current capacity and a non-null pointer is returned.</returns>
        public static bool TryGetById(GameBuildingManagerAPI manager, int buildingId,
            out GameBuilding* building, out BuildingLookupFailure failure)
        {
            building = null;
            if (buildingId <= 0) { failure = BuildingLookupFailure.InvalidId; return false; }
            if (manager == null) { failure = BuildingLookupFailure.ManagerUnavailable; return false; }
            if (!manager.IsValidId(buildingId)) { failure = BuildingLookupFailure.OutOfRange; return false; }
            if (!manager.TryGetBuildingById(buildingId, out building) || building == null)
            {
                building = null;
                failure = BuildingLookupFailure.Unavailable;
                return false;
            }
            failure = BuildingLookupFailure.None;
            return true;
        }
    }
}
