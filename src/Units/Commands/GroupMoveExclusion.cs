using BepInEx.Logging;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace APIShared.Commands
{
    /// <summary>Clears Vanilla's AI exclusion mark of a unit before the caller's own group move.</summary>
    /// <remarks>
    /// <para>The native group move (the Script Extender's <c>GameTribeManagerAPI.IssueMoveHereCommand</c>,
    /// the player's move order and the AI's own moves) skips every member whose 16-bit mark at
    /// <c>GameUnit+0x428</c> (Extender <c>N000000D8</c>) is not 0. Only the AI writes the mark, for every
    /// member of its group right before its own next group order. Regrouping, tribe creation and player
    /// orders keep it. A unit taken out of an AI group and ordered again can therefore stay behind
    /// silently, while the move still returns true. Vanilla's AI clears the mark of every living member
    /// right before its own plain group moves; this helper does the same for one unit.</para>
    /// <para>Opt in only for the units you order yourself, immediately before your group move. The mark is
    /// simulation state: call on the simulation thread in a context that every multiplayer peer executes
    /// alike (a native simulation tick callback or a tick-synchronous Chore), never from local input or UI.
    /// Do not clear it when you repeat an AI order on the AI's own group: there the mark is the AI's
    /// current member selection. Existing Script Extender calls are not changed.</para>
    /// <para>Available after APIShared's native initialization for the audited CrusaderDE.dll and Script
    /// Extender unit layout only; otherwise every call writes nothing and returns false.</para>
    /// </remarks>
    public static unsafe class GroupMoveExclusion
    {
        private static GroupMoveExclusionState state = GroupMoveExclusionState.Unavailable("Native initialization has not completed.");
        private static int initialized;

        /// <summary>Whether the native image and the installed unit layout match the audited contract.</summary>
        public static bool IsAvailable => Volatile.Read(ref state).IsAvailable;

        /// <summary>Why the helper is unavailable; empty when available.</summary>
        public static string UnavailabilityReason => Volatile.Read(ref state).Reason;

        /// <summary>Clears the group-move exclusion mark of one unit that passes Vanilla's life predicate.</summary>
        /// <param name="unitId">One-based unit game ID of a unit you are about to order.</param>
        /// <param name="wasMarked">True when a non-zero mark was replaced by 0; false otherwise.</param>
        /// <returns>True when the unit's mark is 0 afterwards. False, without any write, when the helper is
        /// unavailable, the ID does not resolve or the unit fails <see cref="UnitAccess.IsReallyAlive(GameUnit*)"/>
        /// (the same members Vanilla's own clearing writes).</returns>
        public static bool TryClear(int unitId, out bool wasMarked)
        {
            wasMarked = false;
            var current = Volatile.Read(ref state);
            // An unavailable helper never instantiates the Extender's unit manager.
            if (!current.IsAvailable || !UnitAccess.TryGetById(unitId, out GameUnit* unit, out _)) return false;
            return TryClear(current, unit, out wasMarked);
        }

        internal static bool TryClear(GroupMoveExclusionState current, GameUnit* unit, out bool wasMarked)
        {
            wasMarked = false;
            if (current == null || !current.IsAvailable || !UnitAccess.IsReallyAlive(unit)) return false;
            if (unit->N000000D8 == 0) return true;
            unit->N000000D8 = 0;
            wasMarked = true;
            return true;
        }

        // Called once by the native initialization steps, after the CrusaderDE.dll hash is known.
        internal static void Initialize(string hash, ManualLogSource log)
        {
            if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0) return;
            GroupMoveExclusionState validated;
            try { validated = GroupMoveExclusionState.Validate(hash, typeof(GameUnit), typeof(AliveState)); }
            catch (Exception error) { validated = GroupMoveExclusionState.Unavailable("Validation failed: " + error.GetType().Name + ": " + error.Message); }
            Volatile.Write(ref state, validated);
            if (validated.IsAvailable)
                NativeApiLog.Debug(log, "GROUP_MOVE_EXCLUSION available: GameUnit+0x428 (N000000D8, UInt16), life predicate +0x88/+0x29C; no hooks installed.");
            else
                NativeApiLog.Warning(log, "GROUP_MOVE_EXCLUSION unavailable: " + validated.Reason + " Other APIShared services remain independent.");
        }
    }

    // Immutable publication: the helper stays fail-closed until hash and layout are confirmed.
    internal sealed class GroupMoveExclusionState
    {
        // Audit of the native field and its readers/writers: UpdateToNewDLL.md, "Group-move exclusion mark".
        internal const int UnitSize = 0x490, MarkOffset = 0x428, AliveStateOffset = 0x88, DeathMarkerOffset = 0x29C;
        internal readonly bool IsAvailable;
        internal readonly string Reason;

        private GroupMoveExclusionState(bool available, string reason) { IsAvailable = available; Reason = reason; }

        internal static GroupMoveExclusionState Unavailable(string reason) => new GroupMoveExclusionState(false, reason);

        // The native group move compares the mark as a WORD, AliveState as a WORD equal to 2 and the
        // death marker as a WORD; the typed accessors must address exactly these slots.
        internal static GroupMoveExclusionState Validate(string hash, Type unitType, Type aliveStateType)
        {
            if (!string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase))
                return Unavailable("Unsupported CrusaderDE.dll SHA-256.");
            if (unitType == null || aliveStateType == null || !aliveStateType.IsEnum ||
                Marshal.SizeOf(unitType) != UnitSize ||
                !HasField(unitType, nameof(GameUnit.N000000D8), typeof(ushort), MarkOffset) ||
                !HasField(unitType, nameof(GameUnit.r_AliveState), aliveStateType, AliveStateOffset) ||
                !HasField(unitType, nameof(GameUnit.r_IsKilledByProjectile), typeof(ushort), DeathMarkerOffset) ||
                Enum.GetUnderlyingType(aliveStateType) != typeof(short) ||
                Enum.GetName(aliveStateType, (short)2) != nameof(AliveState.IsAlive))
                return Unavailable("Installed Script Extender GameUnit layout differs from the audited layout.");
            return new GroupMoveExclusionState(true, string.Empty);
        }

        private static bool HasField(Type type, string name, Type fieldType, int offset)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field != null && field.FieldType == fieldType && Marshal.OffsetOf(type, name).ToInt32() == offset;
        }
    }
}
