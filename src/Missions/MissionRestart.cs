using System;

namespace APIShared
{
    /// <summary>Explicit same-mission restarts, sharing the normal process-wide mission lifecycle.</summary>
    public static class MissionRestart
    {
        /// <summary>True on the Unity thread while executing an explicitly linked restart, including
        /// its managed preparation. Not a permission to change settings or a multiplayer transport.</summary>
        public static bool IsInProgress => MissionLifecycleService.RestartInProgress;

        /// <summary>Checks a direct same-lobby map restart against the current completed mission.
        /// Call on Unity before confirming a synchronized restart. The check neither reserves a
        /// launch nor sends packets. Run and the map loader validate again at commitment.</summary>
        public static bool TryValidateMultiplayerMap(EngineInterface.MultiplayerSetupData setup,
            FileHeader map, int coopTrail, int coopMission, out string reason) =>
            MissionLifecycleService.TryValidateMultiplayerRestart(setup, map, coopTrail, coopMission, out reason);

        /// <summary>Runs the complete restart action once on the Unity thread, after linking the ready
        /// source session to a new attempt and before publishing its End. Nested restart calls share
        /// that attempt. Delayed multiplayer completion remains owned by the mission lifecycle.</summary>
        /// <remarks>Requires the initialized lifecycle and a ready non-editor source. Wrap preparation
        /// as well as loading. Vanilla exceptions propagate; unsuccessful loading ends the attempt.
        /// No settings are serialized, persisted or sent by this method. Settings owners use the
        /// immutable ReplacementContext to transfer their existing state, and clean up on failure/end.</remarks>
        /// <exception cref="ArgumentException">The owner GUID is blank.</exception>
        /// <exception cref="ArgumentNullException">The action is null.</exception>
        /// <exception cref="InvalidOperationException">The service/thread/source is unavailable, or a direct
        /// multiplayer map restart did not finish loading (its source mission has already ended).</exception>
        public static void Run(string ownerGuid, Action restart)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid)) throw new ArgumentException("A restart requires an owner GUID.", nameof(ownerGuid));
            if (restart == null) throw new ArgumentNullException(nameof(restart));
            MissionLifecycleService.RunRestart(restart);
        }
    }
}
