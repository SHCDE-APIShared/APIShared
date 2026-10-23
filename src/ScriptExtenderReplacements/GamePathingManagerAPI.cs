using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Threading;

namespace APIShared.ScriptExtenderReplacements
{
    /// <summary>Opt-in replacements for the Script Extender's unit-type pathfinding table methods.</summary>
    /// <remarks>Use after APIShared readiness and check IsAvailable. Spans are immediate writable native
    /// views with process lifetime. Call on the game's appropriate thread; callers own synchronization.
    /// Writes affect all players and subsequent searches, without rebuilding existing paths. No SE method
    /// is hooked. Only the seven documented table methods are provided by this replacement.</remarks>
    public sealed unsafe class GamePathingManagerAPI
    {
        /// <summary>Valid enum count, excluding the CHIMP_NUM_TYPES sentinel; not the native row stride.</summary>
        public const int PATHFINDING_UNIT_TYPE_COUNT = (int)eChimps.CHIMP_NUM_TYPES;
        /// <summary>Native profile capacity and permission row stride, including one non-enum storage slot.</summary>
        public const int NATIVE_PATHFINDING_UNIT_TYPE_CAPACITY = 90;
        /// <summary>First supported connection class, as in the Script Extender.</summary>
        public const int FIRST_PATH_CONNECTION_CLASS = (int)PathConnectionClass.LadderClimb;
        /// <summary>Last supported connection class, as in the Script Extender.</summary>
        public const int LAST_PATH_CONNECTION_CLASS = (int)PathConnectionClass.Connection6;

        private PathfindingTableState state;
        /// <summary>The process-wide replacement; obtaining it does not initialize native game managers.</summary>
        public static GamePathingManagerAPI Instance { get; } =
            new GamePathingManagerAPI(PathfindingTableState.Unavailable("Native initialization has not completed."));

        internal GamePathingManagerAPI(PathfindingTableState initialState) { state = initialState; }
        internal void Publish(PathfindingTableState validatedState) => Volatile.Write(ref state, validatedState);

        /// <summary>Whether both native tables have passed image, bounds and address validation.</summary>
        public bool IsAvailable => Volatile.Read(ref state).IsAvailable;
        /// <summary>Why access is unavailable; empty on success. No implicit SE fallback is performed.</summary>
        public string UnavailabilityReason => Volatile.Read(ref state).Reason;

        /// <summary>Returns all 90 live writable profile slots, or an empty span when unavailable.</summary>
        /// <remarks>Slot 89 is storage only, not an additional valid eChimps value.</remarks>
        public Span<PathfindingProfile> GetUnitTypePathfindingProfiles()
        {
            var current = Volatile.Read(ref state);
            return current.IsAvailable
                ? new Span<PathfindingProfile>((void*)current.Profiles, NATIVE_PATHFINDING_UNIT_TYPE_CAPACITY)
                : Span<PathfindingProfile>.Empty;
        }

        /// <summary>Returns the 540 live writable permission values: six class-major rows of 90 Int32s.</summary>
        /// <returns>An empty span when unavailable. Nonzero permission values mean allowed.</returns>
        public Span<int> GetUnitTypePathfindingConnectionClasses()
        {
            var current = Volatile.Read(ref state);
            return current.IsAvailable
                ? new Span<int>((void*)current.Permissions, PathfindingTableState.PermissionCount)
                : Span<int>.Empty;
        }

        /// <summary>Returns a 90-entry live writable row for class 1..6, or empty for invalid/unavailable access.</summary>
        /// <param name="connectionClass">Native connection class. Class zero is not exposed.</param>
        public Span<int> GetUnitTypePathfindingConnectionClassPermissions(PathConnectionClass connectionClass)
        {
            var current = Volatile.Read(ref state);
            int row = (int)connectionClass - FIRST_PATH_CONNECTION_CLASS;
            if ((uint)row >= PathfindingTableState.ConnectionClassCount || !current.IsAvailable)
                return Span<int>.Empty;
            return new Span<int>((int*)current.Permissions + row * NATIVE_PATHFINDING_UNIT_TYPE_CAPACITY,
                NATIVE_PATHFINDING_UNIT_TYPE_CAPACITY);
        }

        /// <summary>Gets a valid unit type's live profile; invalid/unavailable access returns default (Large).</summary>
        /// <param name="unitType">Valid enum value 0..88, excluding the count sentinel.</param>
        public PathfindingProfile GetUnitTypePathfindingProfile(eChimps unitType)
        {
            var current = Volatile.Read(ref state);
            return IsValidUnitType(unitType) && current.IsAvailable
                ? ((PathfindingProfile*)current.Profiles)[(int)unitType] : default;
        }

        /// <summary>Writes Large or Default for a valid unit type, returning false without writing on failure.</summary>
        /// <param name="unitType">Valid enum value 0..88, excluding the count sentinel.</param>
        /// <param name="profile">Large or Default, as in the SE; no other table or override is changed.</param>
        public bool SetUnitTypePathfindingProfile(eChimps unitType, PathfindingProfile profile)
        {
            var current = Volatile.Read(ref state);
            if (!IsValidUnitType(unitType) || (uint)profile > (uint)PathfindingProfile.Default || !current.IsAvailable)
                return false;
            ((PathfindingProfile*)current.Profiles)[(int)unitType] = profile;
            return true;
        }

        /// <summary>Reads permission using the native 90-entry stride; invalid/unavailable access returns false.</summary>
        /// <param name="unitType">Valid enum value 0..88.</param>
        /// <param name="connectionClass">Native class 1..6.</param>
        public bool CanUnitTypeUsePathConnectionClass(eChimps unitType, PathConnectionClass connectionClass)
        {
            if (!IsValidUnitType(unitType)) return false;
            var permissions = GetUnitTypePathfindingConnectionClassPermissions(connectionClass);
            return !permissions.IsEmpty && permissions[(int)unitType] != 0;
        }

        /// <summary>Writes exactly one native permission as 0 or 1; invalid/unavailable access returns false.</summary>
        /// <param name="unitType">Valid enum value 0..88.</param>
        /// <param name="connectionClass">Native class 1..6.</param>
        /// <param name="canUse">Whether subsequent native searches may use this class for this type.</param>
        public bool SetUnitTypeCanUsePathConnectionClass(eChimps unitType, PathConnectionClass connectionClass, bool canUse)
        {
            if (!IsValidUnitType(unitType)) return false;
            var permissions = GetUnitTypePathfindingConnectionClassPermissions(connectionClass);
            if (permissions.IsEmpty) return false;
            permissions[(int)unitType] = canUse ? 1 : 0;
            return true;
        }

        private static bool IsValidUnitType(eChimps unitType) => (uint)unitType < PATHFINDING_UNIT_TYPE_COUNT;
    }
}
