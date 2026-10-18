using System;
using BepInEx.Logging;

namespace APIShared.Units
{
    /// <summary>Process-owned notification that a created, recruited, assigned or disbanded unit has
    /// finished its native creation or type transition and can receive per-unit changes.</summary>
    public static class UnitSettledEvents
    {
        internal static readonly UnitSettledTracker Tracker = new UnitSettledTracker(
            UnitSettledScriptExtenderSource.Reader, UnitSettledScriptExtenderSource.Activate);

        /// <summary>Registers a callback for the process lifetime. There is no unregistration; use your own
        /// logical settings to ignore notifications.</summary>
        /// <remarks>
        /// <para><b>Triggers.</b> Tracking starts at Script Extender <c>OnUnitCreate</c> Post with a positive
        /// returned unit ID (<see cref="UnitSettledCause.Created"/>) and at every <c>OnUnitTransition</c>
        /// (raised by SE only in Pre: mercenary post and European barracks recruitment, worker assignment,
        /// disband). Recruitment reuses the peasant's unit slot and initializes the requested type in that
        /// slot many simulation ticks after the purchase; at the transition event the slot still has its old type.</para>
        /// <para><b>Settled.</b> A tracked unit settles at the first SE <c>OnTick</c> after its trigger at which
        /// its slot passes <c>UnitAccess.IsReallyAlive</c>, has a nonzero GlobalId,
        /// still has the owner and (if it had one) the GlobalId read at the trigger, and, for transitions,
        /// has a unit type different from the type at the trigger. Each trigger produces at most one
        /// notification; a unit with two triggers (for example created, then recruited) can be reported twice.</para>
        /// <para><b>Dropped without notification</b> and counted in <see cref="DroppedUnits"/>: the slot is
        /// unresolved or dead at the trigger or before settling (AliveState None or MarkedForDeletion, or the
        /// native death marker), its owner or GlobalId changes, it does not settle within 3000 tick
        /// notifications (for example a transition whose final type equals the old type), more than 8192 units
        /// are waiting, or SE raises <c>OnUnloadMap</c>, <c>OnLoadMap</c> or <c>OnLoadSave</c> Pre while it waits.</para>
        /// <para><b>Order and thread.</b> Callbacks run synchronously on the thread that raises SE <c>OnTick</c>
        /// (the game's simulation update loop), outside registry locks, never on the trigger's thread and not
        /// while the game is paused. Per tick, units are delivered in trigger order; each unit reaches every
        /// registration in ascending <paramref name="order"/>, then ordinal owner GUID, then registration ID,
        /// before the next unit. Registrations added during a notification start with the next tick.</para>
        /// <para><b>Demand and replay.</b> The first successful registration in the process subscribes to SE; there
        /// is no SE subscription or per-tick work before it, and the subscriptions stay for the process lifetime.
        /// Nothing is replayed: triggers raised before that first registration are never observed. A later
        /// registration also receives units that were already waiting. Recruits whose transition was raised
        /// before a save was written are not reported after loading it, and units restored from a save are
        /// reported only if the game recreates them through its spawn function; scan existing units at
        /// mission start or save load yourself.</para>
        /// <para><b>Multiplayer.</b> Triggers are raised by native simulation code and ticks by its update loop on
        /// every peer, so peers that register the same callbacks before the mission see the same notifications
        /// in the same order. Keep callbacks deterministic: no local-player, Unity, clock or
        /// unsynchronized-setting decisions.</para>
        /// <para><b>Callbacks</b> may change the settled unit's simulation state (for example health or speed).
        /// They must not block, issue player commands or touch Unity/Noesis objects. Exceptions are isolated per
        /// registration, counted in <see cref="CallbackFailures"/> and logged at exponentially spaced counts.</para>
        /// </remarks>
        /// <param name="ownerGuid">Your plugin GUID.</param>
        /// <param name="registrationId">Stable ID, unique within your owner GUID.</param>
        /// <param name="callback">Receives each settled unit; the notification is a value copy.</param>
        /// <param name="reason">Empty on success; otherwise why the registration was rejected.</param>
        /// <param name="order">Ascending delivery order among registrations (default 0).</param>
        /// <returns>False for missing arguments, a duplicate owner/ID pair or unavailable SE events (a later
        /// registration retries the subscription); otherwise true.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Action<UnitSettledNotification> callback, out string reason, int order = 0) =>
            Tracker.TryRegister(ownerGuid, registrationId, callback, order, out reason);

        /// <summary>Total isolated callback exceptions; not a gameplay failure count.</summary>
        public static long CallbackFailures => Tracker.CallbackFailures;

        /// <summary>Total tracked triggers that ended without a notification (see
        /// <see cref="TryRegister"/>). Diagnostic only; it is not reset per mission.</summary>
        public static long DroppedUnits => Tracker.DroppedUnits;

        // APISharedPlugin.Awake: callback failures are reported through APIShared's log.
        internal static void Initialize(ManualLogSource log)
        {
            UnitSettledScriptExtenderSource.Log = log;
            Tracker.SetErrorSink((registration, error) =>
                NativeApiLog.Error(log, "Unit-settled callback failed: " + registration + ": " + error));
        }
    }
}
