using System;
using System.Threading;

namespace APIShared.Economy
{
    // Existing run/buffer owner. Suspension is only NativeRun/preDLL exclusion:
    // Director counters, Timer/MP callbacks and foreign native calls are not paused.
    internal sealed class WorkerProductionRunGate
    {
        private readonly object engineGate;
        private readonly Func<long> generation;
        private readonly Func<bool> enabled, faulted, admitted;
        private readonly Func<bool, int> original;
        private readonly Action observeFault;
        private readonly Func<object> currentEngineMonitor;
        private readonly Func<WorkerProductionRunGate> publishedRunBoundary;
        private long suspensionEpoch;
        private int suspensionHolds, suspensionBound;

        internal WorkerProductionRunGate(object engineGate, Func<long> generation, Func<bool> enabled,
            Func<bool> faulted, Func<bool> admitted, Func<bool, int> original, Action observeFault)
            : this(engineGate, generation, enabled, faulted, admitted, original, observeFault, null, null) { }

        internal WorkerProductionRunGate(object engineGate, Func<long> generation, Func<bool> enabled,
            Func<bool> faulted, Func<bool> admitted, Func<bool, int> original, Action observeFault,
            Func<object> currentEngineMonitor)
            : this(engineGate, generation, enabled, faulted, admitted, original, observeFault,
                currentEngineMonitor, null) { }

        internal WorkerProductionRunGate(object engineGate, Func<long> generation, Func<bool> enabled,
            Func<bool> faulted, Func<bool> admitted, Func<bool, int> original, Action observeFault,
            Func<object> currentEngineMonitor, Func<WorkerProductionRunGate> publishedRunBoundary)
        {
            this.engineGate = engineGate ?? throw new ArgumentNullException(nameof(engineGate));
            this.generation = generation ?? throw new ArgumentNullException(nameof(generation));
            this.enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
            this.faulted = faulted ?? throw new ArgumentNullException(nameof(faulted));
            this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted));
            this.original = original ?? throw new ArgumentNullException(nameof(original));
            this.observeFault = observeFault ?? throw new ArgumentNullException(nameof(observeFault));
            this.currentEngineMonitor = currentEngineMonitor;
            this.publishedRunBoundary = publishedRunBoundary;
        }

        internal object EngineMonitor => engineGate;
        internal bool HasNativeRunSuspensionBinding => Volatile.Read(ref suspensionBound) != 0;

        // Cold publication helper used by WorldSafety's existing owner. Unbound
        // initialization retains its old replace behavior. Once opted in, that
        // exact gate must survive later initialization and outstanding handles.
        internal static WorkerProductionRunGate RetainNativeRunSuspensionBinding(
            WorkerProductionRunGate current, object actualEngineMonitor)
        {
            if (current == null || !current.HasNativeRunSuspensionBinding) return null;
            if (!ReferenceEquals(current.engineGate, actualEngineMonitor))
                throw new InvalidOperationException("The bound native-run suspension monitor changed.");
            return current;
        }

        // Caller holds WorldSafety's cold publication lock. Binding is process-lived;
        // it grants no pause, native memory access, world/MP or restoration authority.
        internal bool TryBindNativeRunSuspension(out string reason)
        {
            if (!MatchesCurrentEngineMonitor() || !IsCurrentPublication())
            { reason = "The current engine monitor does not match the run boundary."; return false; }
            Volatile.Write(ref suspensionBound, 1);
            reason = string.Empty;
            return true;
        }

        // Trusted caller only, after shared startup binding. The finite timeout
        // excludes -1. Never acquire from an engine/run callback: Monitor is
        // reentrant and such an acquisition would not prove the active run ended.
        // On success the caller retains a hold across its qualified Original/reset
        // chain and disposes it after all protected work. No buffer is borrowed.
        internal bool TryAcquireNativeRunSuspension(int millisecondsTimeout,
            out NativeRunSuspension suspension, out string reason)
        {
            suspension = null;
            if (millisecondsTimeout < 0)
            { reason = "A finite nonnegative native-run suspension timeout is required."; return false; }
            if (!HasNativeRunSuspensionBinding || !MatchesCurrentEngineMonitor() || !IsCurrentPublication())
            { reason = "The native-run suspension binding is unavailable or stale."; return false; }
            if (Monitor.IsEntered(engineGate))
            { reason = "Native-run suspension cannot be acquired while owning the engine monitor."; return false; }
            if (!Monitor.TryEnter(engineGate, millisecondsTimeout))
            { reason = "The native-run suspension deadline expired."; return false; }
            try
            {
                if (!MatchesCurrentEngineMonitor() || !IsCurrentPublication())
                { reason = "The engine monitor changed during native-run suspension acquisition."; return false; }
                if (Volatile.Read(ref suspensionHolds) == int.MaxValue)
                { reason = "The native-run suspension hold limit was reached."; return false; }
                // Allocate before publishing debt. Release is lock-free and may race
                // here, so acquisition uses the same atomic counter operations.
                var candidate = new NativeRunSuspension(this);
                Interlocked.Increment(ref suspensionEpoch);
                Interlocked.Increment(ref suspensionHolds);
                suspension = candidate;
                reason = string.Empty;
                return true;
            }
            finally { Monitor.Exit(engineGate); }
        }

        private bool MatchesCurrentEngineMonitor()
        {
            if (currentEngineMonitor == null) return false;
            try { return ReferenceEquals(currentEngineMonitor(), engineGate); }
            catch { return false; } // Rare binding failure; never inspect via reflection per Run.
        }

        private bool IsCurrentPublication() =>
            publishedRunBoundary == null || ReferenceEquals(publishedRunBoundary(), this);

        // Epoch is an opaque process-local change witness, independent of the
        // production/world epoch. Advance before opening the last hold, so queued
        // calls cannot borrow an earlier suspension interval. No monitor wait,
        // callback, production-flag restoration or native access occurs on release.
        private void ReleaseNativeRunSuspension()
        {
            Interlocked.Increment(ref suspensionEpoch);
            Interlocked.Decrement(ref suspensionHolds);
        }

        internal sealed class NativeRunSuspension : IDisposable
        {
            private WorkerProductionRunGate owner;
            internal NativeRunSuspension(WorkerProductionRunGate owner) { this.owner = owner; }
            internal bool IsDisposed => Volatile.Read(ref owner) == null;

            // One exact hold, once; may be disposed on another thread or after
            // production generation changes. Released handles cannot clear a later
            // hold, production fault or replacement world. The bound gate itself
            // remains process-lived; external monitor replacement is unsupported.
            public void Dispose()
            {
                if (Volatile.Read(ref owner) == null) return;
                Interlocked.Exchange(ref owner, null)?.ReleaseNativeRunSuspension();
            }
        }

        internal int Run(bool mpFrameSkip)
        {
            long expectedGeneration = generation();
            long expectedSuspensionEpoch = Volatile.Read(ref suspensionEpoch);
            lock (engineGate)
            {
                if (expectedGeneration != generation()) return 0;
                if (enabled())
                {
                    // Preserve the existing fault observation/termination retry even
                    // when an independent suspension is also retaining this run.
                    if (faulted()) { observeFault(); return 0; }
                    if (!admitted()) return 0;
                }
                // OnRun can capture an older unbound gate before initialization
                // replaces it. Preserve that old default forwarding while unbound;
                // once a current suspension owner binds, reject stale captures
                // UNDER the engine monitor before they can reserve a buffer.
                WorkerProductionRunGate published = publishedRunBoundary?.Invoke();
                if (published != null && published.HasNativeRunSuspensionBinding &&
                    !ReferenceEquals(published, this)) return 0;
                if (expectedSuspensionEpoch != Volatile.Read(ref suspensionEpoch) ||
                    Volatile.Read(ref suspensionHolds) != 0) return 0;
                int result = original(mpFrameSkip);
                return expectedGeneration != generation() || faulted() ||
                    expectedSuspensionEpoch != Volatile.Read(ref suspensionEpoch) ? 0 : result;
            }
        }
    }
}
