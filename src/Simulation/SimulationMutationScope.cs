using System;

namespace APIShared.Simulation
{
    // A thread-local admission marker, never a dispatcher or an engine lock. Only the
    // already validated native tick and authenticated Chore publishers enter it.
    internal static class SimulationMutationScope
    {
        [ThreadStatic] private static int depth;
        internal static bool IsActive => depth > 0;
        internal static Scope Enter() { depth++; return new Scope(true); }
        internal readonly struct Scope : IDisposable
        {
            private readonly bool entered;
            internal Scope(bool entered) { this.entered = entered; }
            public void Dispose() { if (entered) depth--; }
        }
    }
}
