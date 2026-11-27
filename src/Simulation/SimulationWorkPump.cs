using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Simulation
{
    // Rare deterministic work after DLL_RunTick has returned, before its filled buffer is published.
    // Shares MarkedUnitSelectionAPI's existing run/buffer owner. Never drains inside a native callback.
    // This is not a clock: callers retain native-time and session/identity guards themselves.
    internal static class SimulationWorkPump
    {
        private static readonly object sync = new object();
        private static readonly Queue<Action> pending = new Queue<Action>();
        private static object engineGate;
        private static int demand;
        [ThreadStatic] private static int runDepth;
        [ThreadStatic] private static bool draining;
        internal static bool IsAvailable => Volatile.Read(ref engineGate) != null;
        internal static bool HasPending => Volatile.Read(ref demand) != 0;
        internal static bool IsQuiescent => draining;
        internal static void Initialize(object gate) => Volatile.Write(ref engineGate, gate);
        internal static void EnterRun() => runDepth++;
        internal static void ExitRun() => runDepth--;
        internal static bool Request(Action action)
        {
            if (action == null || !IsAvailable) return false;
            lock (sync)
            {
                // Unit capacity bounds the only initial consumer. A refusal is visible to that consumer.
                if (pending.Count >= 10000) return false;
                pending.Enqueue(action);
                Volatile.Write(ref demand, 1);
                return true;
            }
        }
        internal static void Drain()
        {
            object gate = Volatile.Read(ref engineGate);
            if (!HasPending || gate == null || !Monitor.IsEntered(gate) || runDepth != 1 || draining) return;
            int count;
            lock (sync) count = pending.Count;
            draining = true;
            try
            {
                using (SimulationMutationScope.Enter())
                    for (int i = 0; i < count; i++)
                    {
                        Action action;
                        lock (sync)
                        {
                            if (pending.Count == 0) break;
                            action = pending.Dequeue();
                            if (pending.Count == 0) Volatile.Write(ref demand, 0);
                        }
                        try { action(); }
                        catch (Exception error)
                        {
                            try { Internal.RuntimeHealth.Failure(ApiSharedRuntime.ProcessInstance.Logger,
                                "simulation-work", true, "Quiescent simulation work failed: " + error); }
                            catch { /* Health reporting must not escape into the engine wrapper. */ }
                        }
                    }
            }
            finally { draining = false; }
        }
    }
}
