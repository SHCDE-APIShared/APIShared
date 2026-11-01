using System;
using BepInEx.Logging;

namespace APIShared.Simulation
{
    /// <summary>Process-owned simulation notifications carrying the native, save-preserved map tick.</summary>
    /// <remarks>
    /// In the audited Script Extender implementation, GameTimeManagerAPI.OnTick carries Director's local
    /// engine-run counter. That argument can differ between multiplayer peers because of frame skips and
    /// FPS compensation, and must not schedule tick-dependent multiplayer mutations. This adapter retains
    /// the existing synchronous simulation callback but reads GetElapsedMapTicks once per publication.
    /// It does not change the Extender's frame provider, timestamps or timers.
    /// </remarks>
    public static class SimulationTickEvents
    {
        internal static readonly SimulationTickRegistry Registry = new SimulationTickRegistry(
            SimulationTickSource.TryRead, SimulationTickSource.Activate, SimulationTickSource.Report);

        /// <summary>Registers a callback for the process lifetime; use logical activation to suspend work.</summary>
        /// <remarks>
        /// Call after APIShared native initialization. Invalid arguments, duplicate owner/ID pairs and an
        /// unavailable source return false with a reason; failed activation can be retried. Callbacks run
        /// synchronously in the Extender's native time-update callback, only during a fully started mission
        /// (including the editor), ordered by order, then ordinal owner GUID and registration ID, outside
        /// registry locks. Each callback receives the same signed Int32 representation of the native map
        /// counter. Exceptions are isolated per registration. Duplicate or older ticks in the same session
        /// and reentrant publications are suppressed; counter comparison supports Int32 wraparound.
        /// Registrations added during publication join the next tick. There is no replay, catch-up,
        /// unregistration or Unity-thread dispatch. No notification is emitted without a valid source and
        /// ready mission. Paused/skipped simulation steps do not advance this counter.
        /// Multiplayer users must register identical callbacks before the mission and use synchronized
        /// settings and deterministic decisions; the event does not synchronize arbitrary caller state.
        /// Callbacks must not block, touch Unity/Noesis or alter native scenario/timeline vectors.
        /// Delivery still depends on the Extender reaching its event: an upstream exception can prevent it.
        /// </remarks>
        /// <param name="ownerGuid">Your stable mod GUID.</param>
        /// <param name="registrationId">Stable ID, unique within this owner.</param>
        /// <param name="callback">Receives the native map tick on the simulation publisher's thread.</param>
        /// <param name="reason">Empty on success; otherwise why registration failed.</param>
        /// <param name="order">Ascending callback order, default zero.</param>
        /// <returns>Whether the callback was registered.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId, Action<int> callback,
            out string reason, int order = 0) =>
            Registry.TryRegister(ownerGuid, registrationId, callback, order, out reason);

        /// <summary>Reads the native map tick only while the source and a fully started mission are available.</summary>
        /// <remarks>
        /// Does not activate notifications. Zero is a valid counter, not an availability sentinel.
        /// Read on the simulation thread, or at mission completion while simulation is stopped/locked;
        /// this method acquires no engine lock and does not marshal threads. The signed Int32 counter
        /// preserves the save's phase, can wrap, and belongs to the current mission rather than the process.
        /// </remarks>
        /// <param name="mapTick">Native counter on success; zero on failure and must then be ignored.</param>
        /// <returns>False if no ready mission or validated source is available, or reading fails.</returns>
        public static bool TryGetCurrentTick(out int mapTick) => Registry.TryGetCurrentTick(out mapTick);

        internal static void Initialize(SHCDESE.API.LowLevel.CrusaderLibraryLoadContext context, ManualLogSource log) =>
            SimulationTickSource.Initialize(context, log);
    }
}
