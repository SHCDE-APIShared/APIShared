using APIShared.GameModes;
using System;
using System.Threading;

namespace APIShared
{
    // The delegates are prepared once at acquisition and read only the existing owner state.
    // Clock reads are volatile at the owner. Current is deliberately not made a new publisher:
    // exact context/clock identity plus the final observations reject mixed ready metadata.
    internal sealed class NativeWorldReadReader
    {
        private readonly Func<MissionLifecycleState.NativeClockState> readClock;
        private readonly Func<MissionContext> readReady;

        internal NativeWorldReadReader(object syncRoot,
            Func<MissionLifecycleState.NativeClockState> readClock, Func<MissionContext> readReady)
        {
            SyncRoot = syncRoot ?? throw new ArgumentNullException(nameof(syncRoot));
            this.readClock = readClock ?? throw new ArgumentNullException(nameof(readClock));
            this.readReady = readReady ?? throw new ArgumentNullException(nameof(readReady));
        }

        internal object SyncRoot { get; }

        internal bool TryRead(NativeWorldReadSnapshot expected, bool requireExpected,
            out NativeWorldReadSnapshot current, out string reason)
        {
            current = default;
            if (!Monitor.IsEntered(SyncRoot))
            { reason = "The calling thread must hold the native engine monitor."; return false; }
            if (requireExpected && expected.World == null)
            { reason = "A previously successful native-world read is required."; return false; }

            var before = readClock();
            var ready = readReady();
            if (before?.World == null || ready == null || !ReferenceEquals(before.Context, ready) ||
                ready.SessionId <= 0 || before.World.Mission != ready.SessionId || before.World.Sequence <= 0)
            { reason = "No coherent managed-ready native world is published."; return false; }
            if (ready.Mode.Kind == GameModeKind.Unknown || ready.Mode.HasConflictingCustomizedOrigin)
            { reason = "The current native world's mode is unknown or conflicting."; return false; }
            if (requireExpected && !ReferenceEquals(expected.World, before.World))
            { reason = "The expected native world has been retired or replaced."; return false; }

            var afterReady = readReady();
            var after = readClock();
            if (!ReferenceEquals(ready, afterReady) || !ReferenceEquals(before, after))
            { reason = "The native-world publication changed during the read."; return false; }

            current = new NativeWorldReadSnapshot(before.World, ready);
            reason = string.Empty;
            return true;
        }
    }
}
