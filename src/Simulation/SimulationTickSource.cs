using System;
using System.Threading;
using BepInEx.Logging;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.GameGlobals;

namespace APIShared.Simulation
{
    // Initialized after native API publication, before consumers' LibraryLoaded handlers.
    // Only public SE APIs read time; the public resolved address is checked for readiness/bounds.
    // Source subscription and the static registry root each other until process exit.
    internal static class SimulationTickSource
    {
        private static GameTimeManagerAPI time;
        private static IMissionLifecycleCapability missions;
        private static ManualLogSource logger;
        private static ulong tickAddress;
        private static SimulationTickReader reader;
        private static MissionSimulationClockReader scopedReader;
        private static int ready;

        internal static void Initialize(CrusaderLibraryLoadContext context, ManualLogSource log)
        {
            if (Volatile.Read(ref ready) != 0) return;
            logger = log;
            if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out var lifecycle, out var diagnostic))
                throw new InvalidOperationException("Simulation ticks need mission lifecycle: " + diagnostic);
            ulong address = GameGlobalsManager.Instance.CurrentGameTickVA;
            ulong image = unchecked((ulong)context.ModuleHandle.ToInt64());
            // The SE getter loads UInt64 then returns its low Int32. Validate the entire read range.
            if (address < image || context.Memory.Length < sizeof(ulong) ||
                address - image > (ulong)(context.Memory.Length - sizeof(ulong)))
                throw new InvalidOperationException("Native map-tick address is unavailable or outside the loaded image.");
            time = GameTimeManagerAPI.Instance;
            missions = lifecycle;
            tickAddress = address;
            reader = new SimulationTickReader(CurrentMission, time.GetElapsedMapTicks, AddressAvailable, () => MissionLifecycleService.NativeClock);
            scopedReader = new MissionSimulationClockReader(() => MissionLifecycleService.NativeClock, time.GetElapsedMapTicks, AddressAvailable);
            Volatile.Write(ref ready, 1);
        }

        internal static void Activate(Action<int> callback)
        {
            if (Volatile.Read(ref ready) == 0)
                throw new InvalidOperationException("APIShared native simulation tick source is not initialized.");
            time.OnTick += callback;
        }

        internal static bool TryRead(out long sessionId, out int mapTick)
        {
            sessionId = 0;
            mapTick = 0;
            return Volatile.Read(ref ready) != 0 && reader.TryRead(out sessionId, out mapTick);
        }

        internal static bool TryRead(MissionContext expected, out int mapTick)
        {
            mapTick = 0;
            return Volatile.Read(ref ready) != 0 && scopedReader.TryRead(expected, out mapTick);
        }

        private static MissionContext CurrentMission()
        {
            var current = missions.Current;
            return current != null && MissionLifecycleService.NativeClock?.Context.SessionId == current.SessionId ? current : null;
        }

        private static bool AddressAvailable() => GameGlobalsManager.Instance.CurrentGameTickVA == tickAddress;

        internal static void Report(string operation, Exception error) => Internal.RuntimeHealth.Failure(
            logger, "simulation-ticks:" + operation, true, "Simulation tick callback failed: " + error);
    }

    // Immutable lifecycle tokens also detect invalidate/reload/read races within the same attempt.
    internal sealed class MissionSimulationClockReader
    {
        private readonly Func<MissionLifecycleState.NativeClockState> state;
        private readonly Func<int> read;
        private readonly Func<bool> available;
        internal MissionSimulationClockReader(Func<MissionLifecycleState.NativeClockState> state, Func<int> read, Func<bool> available)
        { this.state = state; this.read = read; this.available = available; }
        internal bool TryRead(MissionContext expected, out int mapTick)
        {
            mapTick = 0;
            if (expected == null || !available()) return false;
            var clock = state();
            if (clock == null || clock.Context.SessionId != expected.SessionId) return false;
            int value = read();
            if (!ReferenceEquals(clock, state()) || !available()) return false;
            mapTick = value;
            return true;
        }
    }

    // The injected functions are only the external lifecycle/memory boundaries, not a second clock.
    internal sealed class SimulationTickReader
    {
        private readonly Func<MissionContext> mission;
        private readonly Func<int> read;
        private readonly Func<bool> available;

        private readonly Func<object> generation;
        internal SimulationTickReader(Func<MissionContext> mission, Func<int> read, Func<bool> available, Func<object> generation = null)
        { this.mission = mission; this.read = read; this.available = available; this.generation = generation; }

        internal bool TryRead(out long sessionId, out int mapTick)
        {
            sessionId = 0;
            mapTick = 0;
            if (!available()) return false;
            object token = generation?.Invoke();
            if (generation != null && token == null) return false;
            MissionContext current = mission(); // Current excludes pending/failed initialization.
            if (current == null) return false;
            int tick = read();
            // A concurrent mission replacement must not pair an old session with the new world's counter.
            if (!ReferenceEquals(current, mission()) || !available() ||
                (generation != null && !ReferenceEquals(token, generation()))) return false;
            sessionId = current.SessionId;
            mapTick = tick;
            return true;
        }

    }
}
