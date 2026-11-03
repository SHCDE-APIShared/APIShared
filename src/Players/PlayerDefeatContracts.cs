using System;

namespace APIShared
{
    /// <summary>Immutable notification that a previously confirmed living player lord disappeared or died.</summary>
    public sealed class PlayerLordDeathNotification
    {
        /// <summary>Creates a lord-death notification.</summary>
        public PlayerLordDeathNotification(
            long sessionId,
            int playerId,
            int lordUnitId,
            int lordGlobalId,
            int simulationTick)
        {
            SessionId = sessionId;
            PlayerId = playerId;
            LordUnitId = lordUnitId;
            LordGlobalId = lordGlobalId;
            SimulationTick = simulationTick;
        }

        /// <summary>Gets the mission-lifecycle session identifier.</summary>
        public long SessionId { get; }
        /// <summary>Gets the one-based player identifier.</summary>
        public int PlayerId { get; }
        /// <summary>Gets the one-based unit identifier of the last confirmed living lord.</summary>
        public int LordUnitId { get; }
        /// <summary>Gets the stable global identity of the last confirmed living lord.</summary>
        public int LordGlobalId { get; }
        /// <summary>Gets the local Script Extender engine-run tick on which the transition was observed; not the saved native map tick.</summary>
        public int SimulationTick { get; }
    }

    /// <summary>Immutable notification that a player entered Vanilla's official loss state.</summary>
    public sealed class PlayerDefeatNotification
    {
        /// <summary>Creates an official player-defeat notification.</summary>
        public PlayerDefeatNotification(long sessionId, int playerId, int simulationTick)
        {
            SessionId = sessionId;
            PlayerId = playerId;
            SimulationTick = simulationTick;
        }

        /// <summary>Gets the mission-lifecycle session identifier.</summary>
        public long SessionId { get; }
        /// <summary>Gets the one-based player identifier.</summary>
        public int PlayerId { get; }
        /// <summary>Gets the local Script Extender engine-run tick on which the transition was observed; not the saved native map tick.</summary>
        public int SimulationTick { get; }
    }

    /// <summary>Owner-bound observer for process-wide player defeat transitions.</summary>
    public interface IPlayerDefeatCapability
    {
        /// <summary>
        /// Registers one process-lifetime observer under an owner-local stable ID. Either callback
        /// may be null, but at least one callback is required. Existing state is never replayed.
        /// Observation starts with the first subscriber; its first simulation tick establishes
        /// the baseline. Transitions before that baseline are not reported retroactively.
        /// </summary>
        bool TryRegisterObserver(
            string registrationId,
            Action<PlayerLordDeathNotification> onLordDied,
            Action<PlayerDefeatNotification> onDefeated,
            out NativeCapabilityDiagnostic diagnostic);
    }

    /// <summary>Immutable observation of one player's elimination state in one mission.</summary>
    public sealed class PlayerDefeatSnapshot
    {
        internal PlayerDefeatSnapshot(long sessionId, int playerId, int simulationTick,
            bool readable, bool officialLoss, bool hasLivingLord, bool lordDeathConfirmed)
        {
            SessionId = sessionId;
            PlayerId = playerId;
            SimulationTick = simulationTick;
            Readable = readable;
            OfficialLoss = officialLoss;
            HasLivingLord = hasLivingLord;
            LordDeathConfirmed = lordDeathConfirmed;
        }

        /// <summary>Mission-lifecycle session identifier; never transfer a result to another session.</summary>
        public long SessionId { get; }
        /// <summary>One-based player identifier, from 1 through 8.</summary>
        public int PlayerId { get; }
        /// <summary>Local engine-run tick of the last observable state change, not a saved native map tick.
        /// Unchanged observations reuse the snapshot and retain this tick. End-of-session invalidation retains it too.</summary>
        public int SimulationTick { get; }
        /// <summary>Whether the latest observation could read this player's state. False grants no elimination rights.</summary>
        public bool Readable { get; }
        /// <summary>Whether the latest readable observation has Vanilla's official loss state, including a loaded baseline.</summary>
        public bool OfficialLoss { get; }
        /// <summary>Whether the latest readable observation confirmed a living lord with the correct player identity.</summary>
        public bool HasLivingLord { get; }
        /// <summary>Whether a previously confirmed living lord died or disappeared during this session.
        /// An absent lord in an initial or unreadable observation does not establish this fact.</summary>
        public bool LordDeathConfirmed { get; }
        /// <summary>True only for a readable official loss or a confirmed lord death; absence alone is insufficient.</summary>
        public bool IsEliminated => Readable && (OfficialLoss || LordDeathConfirmed);
    }

    /// <summary>Current-state access, implemented by the binding returned from TryGetPlayerDefeat
    /// (cast the <see cref="IPlayerDefeatCapability"/>); independent of its transition callbacks.</summary>
    public interface IPlayerDefeatStateCapability : IPlayerDefeatCapability
    {
        /// <summary>Thread-safe managed snapshot lookup, with no native reads or implicit observation.
        /// Returns false before the first observation, for an invalid player/session, or after session end.
        /// A true result can be unreadable; callers must check IsEliminated and their current session.</summary>
        bool TryGetObservedState(long sessionId, int playerId, out PlayerDefeatSnapshot snapshot);

        /// <summary>Registers a process-lifetime observer under an owner-local stable ID, shared with transition registrations.
        /// Replays available current snapshots in player order, then publishes changed snapshots and unreadable invalidations.
        /// Initial official losses are state, not replayed transition events. Observers run in owner/ID order outside service locks,
        /// on the thread draining the ordered FIFO (possibly the registering or lifecycle thread); no UI dispatch is implied.
        /// Reentrant publications follow the current delivery, and exceptions are isolated. Already queued immutable snapshots
        /// may describe an ended session: recheck session and current state before acting. Unchanged ticks do not notify.</summary>
        bool TryRegisterStateObserver(string registrationId, Action<PlayerDefeatSnapshot> onChanged,
            out NativeCapabilityDiagnostic diagnostic);
    }
}
