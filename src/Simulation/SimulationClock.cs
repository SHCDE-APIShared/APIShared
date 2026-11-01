using System;

namespace APIShared.Simulation
{
    /// <summary>Native map time scoped to a current mission, including completed native initialization.</summary>
    public static class SimulationClock
    {
        /// <summary>Reads map time after native loading has succeeded for the expected mission.</summary>
        /// <remarks>
        /// Available in NativeLoaded callbacks and afterwards, including editor missions. Returns false
        /// before native completion, during a nested/repeated native load, after retirement, for a stale
        /// expected context, or when the validated source is unavailable. Zero and negative Int32 values
        /// are legitimate ticks, never availability sentinels. Initialization phases do not replay.
        /// Call synchronously on the simulation thread or in a lifecycle callback while native state is
        /// stopped/locked. This does not take an engine lock, dispatch threads, or authorize mutations
        /// in an otherwise unsuitable callback. The expected session ID is process-local, not peer data.
        /// </remarks>
        /// <param name="expectedMission">Context from the central mission lifecycle.</param>
        /// <param name="mapTick">Native counter on success; ignore its value on failure.</param>
        /// <returns>Whether this read belongs to the expected, natively loaded mission.</returns>
        public static bool TryGetCurrentTick(MissionContext expectedMission, out int mapTick)
        {
            mapTick = 0;
            try { return SimulationTickSource.TryRead(expectedMission, out mapTick); }
            catch (Exception ex) { SimulationTickSource.Report("clock-read", ex); return false; }
        }

        /// <summary>Computes a forward distance of at most Int32.MaxValue ticks, allowing signed wrap.</summary>
        /// <remarks>Both values must belong to the same mission. A larger unsigned difference is
        /// ambiguous/backwards and returns false. This helper performs no game access.</remarks>
        /// <param name="earlierTick">Start tick in the same mission.</param>
        /// <param name="currentTick">Current tick in the same mission.</param>
        /// <param name="elapsedTicks">Forward distance on success; zero otherwise.</param>
        /// <returns>Whether the difference is within the supported forward half-range.</returns>
        public static bool TryGetElapsedTicks(int earlierTick, int currentTick, out int elapsedTicks)
        {
            uint distance = unchecked((uint)(currentTick - earlierTick));
            elapsedTicks = distance <= int.MaxValue ? (int)distance : 0;
            return distance <= int.MaxValue;
        }
    }
}
