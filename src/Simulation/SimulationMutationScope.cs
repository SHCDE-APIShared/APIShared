using System;

namespace APIShared.Simulation
{
    // A thread-local admission marker, never a dispatcher or an engine lock. Only the
    // already validated native tick and authenticated Chore publishers enter it.
    internal static class SimulationMutationScope
    {
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static int tickDepth;
        [ThreadStatic] private static int choreDepth;
        internal static bool IsActive => depth > 0;
        internal static bool IsTickActive => tickDepth > 0;
        internal static bool IsAuthenticatedChore => choreDepth > 0;
        internal static Scope Enter() { depth++; return new Scope(true, false); }
        internal static Scope EnterTick() { depth++; tickDepth++; return new Scope(true, true); }
        internal static Scope EnterAuthenticatedChore()
        {
            depth++;
            choreDepth++;
            return new Scope(true, false, true);
        }
        internal readonly struct Scope : IDisposable
        {
            private readonly bool entered;
            private readonly bool tick;
            private readonly bool chore;
            internal Scope(bool entered, bool tick, bool chore = false)
            { this.entered = entered; this.tick = tick; this.chore = chore; }
            public void Dispose()
            {
                if (!entered) return;
                if (tick) tickDepth--;
                if (chore) choreDepth--;
                depth--;
            }
        }
    }
}
