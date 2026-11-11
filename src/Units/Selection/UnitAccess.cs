using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace APIShared
{
    /// <summary>Why a unit game ID could not be resolved. Does not describe unit identity or life state.</summary>
    public enum UnitLookupFailure
    {
        /// <summary>The lookup succeeded.</summary>
        None,
        /// <summary>The ID is zero or negative; it is not a unit game ID.</summary>
        InvalidId,
        /// <summary>No unit manager is available.</summary>
        ManagerUnavailable,
        /// <summary>The ID exceeds the manager's current capacity.</summary>
        OutOfRange,
        /// <summary>The manager could not return a non-null pointer.</summary>
        Unavailable
    }

    /// <summary>Checked access to the Script Extender's one-based unit IDs.</summary>
    /// <remarks>Returned pointers are immediate views, never cached. Callers must validate identity,
    /// ownership and life state themselves and use the existing game-thread contracts.</remarks>
    public static unsafe class UnitAccess
    {
        // Consumers receive the failure reason directly and own diagnostic policy/logging.

        /// <summary>Tests Vanilla's combat life predicate, excluding dying units and visible corpses.</summary>
        /// <remarks>AliveState can remain IsAlive throughout the death animation and corpse phase.
        /// Vanilla additionally tests the 16-bit death marker at GameUnit+0x29C. Despite its
        /// Script Extender name r_IsKilledByProjectile, melee death sets this marker too.
        /// The adjacent WORD at +0x29E is unrelated. Health and animation state are deliberately not tested.
        /// Use immediate game-thread views; this does not validate slot identity or ownership.</remarks>
        /// <param name="unit">The immediate Script Extender unit view.</param>
        /// <returns>True when AliveState is IsAlive and the native death marker is zero.</returns>
        public static bool IsReallyAlive(in GameUnit unit) =>
            unit.r_AliveState == AliveState.IsAlive && unit.r_IsKilledByProjectile == 0;

        /// <summary>Tests Vanilla's combat life predicate; a null pointer returns false.</summary>
        /// <param name="unit">An immediate valid unit pointer, or null.</param>
        /// <returns>True only for a non-null unit passing the combat life predicate.</returns>
        public static bool IsReallyAlive(GameUnit* unit) => unit != null && IsReallyAlive(in *unit);

        /// <summary>Gets one-based game IDs of units passing Vanilla's combat life predicate.</summary>
        /// <remarks>Use on the game thread. The result is a momentary list, not an identity or
        /// lifetime guarantee; revalidate identity and life before later actions.</remarks>
        /// <returns>Unit game IDs in the Script Extender query's native array order.</returns>
        public static int[] GetAllReallyAliveUnits()
        {
            var results = new List<int>();
            GameUnitManagerAPI.Instance.QueryUnits()
                .Where((in GameUnit unit) => IsReallyAlive(in unit))
                .ToIdList(results);
            return results.ToArray();
        }

        /// <summary>Resolves a unit ID without passing invalid IDs to the Script Extender.</summary>
        /// <param name="unitId">One-based game ID, never a span index.</param>
        /// <param name="unit">The immediate pointer on success; null on any failure.</param>
        /// <param name="failure">The failure reason, or None on success.</param>
        /// <param name="sourceFile">Retained caller metadata; consumers own diagnostics.</param>
        /// <param name="member">Compiler-supplied caller name.</param>
        /// <param name="line">Compiler-supplied call-site line.</param>
        /// <returns>True only when a non-null pointer was returned.</returns>
        public static bool TryGetById(int unitId, out GameUnit* unit, out UnitLookupFailure failure,
            [CallerFilePath] string sourceFile = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        {
            unit = null;
            // Do not instantiate the lazy manager for an absent/sentinel ID.
            if (unitId <= 0) return Reject(unitId, UnitLookupFailure.InvalidId, out failure, sourceFile, member, line);
            return TryGetById(GameUnitManagerAPI.Instance, unitId, out unit, out failure, sourceFile, member, line);
        }

        /// <summary>Resolves a unit ID using the caller's already captured manager.</summary>
        /// <param name="manager">The captured manager; null is safely rejected.</param>
        /// <param name="unitId">One-based game ID.</param>
        /// <param name="unit">The immediate pointer on success; null on any failure.</param>
        /// <param name="failure">The failure reason, or None on success.</param>
        /// <param name="sourceFile">Compiler-supplied source path.</param>
        /// <param name="member">Compiler-supplied caller name.</param>
        /// <param name="line">Compiler-supplied call-site line.</param>
        /// <returns>True only when a non-null pointer was returned.</returns>
        public static bool TryGetById(GameUnitManagerAPI manager, int unitId, out GameUnit* unit, out UnitLookupFailure failure,
            [CallerFilePath] string sourceFile = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        {
            unit = null;
            if (unitId <= 0) return Reject(unitId, UnitLookupFailure.InvalidId, out failure, sourceFile, member, line);
            if (manager == null) return Reject(unitId, UnitLookupFailure.ManagerUnavailable, out failure, sourceFile, member, line);
            if (!manager.IsValidId(unitId)) return Reject(unitId, UnitLookupFailure.OutOfRange, out failure, sourceFile, member, line);
            if (!manager.TryGetUnitById(unitId, out unit) || unit == null)
            {
                unit = null;
                return Reject(unitId, UnitLookupFailure.Unavailable, out failure, sourceFile, member, line);
            }
            failure = UnitLookupFailure.None;
            return true;
        }

        private static bool Reject(int unitId, UnitLookupFailure reason, out UnitLookupFailure failure,
            string sourceFile, string member, int line)
        {
            failure = reason;
            return false;
        }
    }
}
