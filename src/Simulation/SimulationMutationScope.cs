using System;

namespace APIShared.Simulation
{
    // A thread-local admission marker, never a dispatcher or an engine lock. Only the
    // already validated native tick and authenticated Chore publishers enter it.
    internal static class SimulationMutationScope
    {
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static int tickDepth;
        internal static bool IsActive => depth > 0;
        internal static bool IsTickActive => tickDepth > 0;
        internal static Scope Enter() { depth++; return new Scope(true, false); }
        internal static Scope EnterTick() { depth++; tickDepth++; return new Scope(true, true); }
        internal readonly struct Scope : IDisposable
        {
            private readonly bool entered;
            private readonly bool tick;
            internal Scope(bool entered, bool tick) { this.entered = entered; this.tick = tick; }
            public void Dispose()
            {
                if (!entered) return;
                if (tick) tickDepth--;
                depth--;
            }
        }
    }
}
