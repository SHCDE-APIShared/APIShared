using System;
using System.Diagnostics;
using System.Threading;

namespace APIShared.Economy
{
    // Cold owner of the existing run boundary and of its pre-publication NULL
    // fallback only. Normal non-NULL OnRun calls read Current and enter the gate
    // directly; this is neither another hook nor an all-run tracking service.
    internal sealed class WorkerProductionRunPublication
    {
        private readonly object publicationGate = new object();
        private readonly Func<object, WorkerProductionRunGate> create;
        private readonly Func<bool, int> fallbackOriginal;
        private WorkerProductionRunGate current;
        private int fallbackCalls;
        private bool admissionClosed, bindingInProgress, bindingReady;

        // There is one process owner in production. Reject binding from any NULL
        // fallback on this thread, including nested internal owner calls: waiting
        // for that call's own finally would otherwise drain itself forever.
        [ThreadStatic] private static int fallbackDepth;

        internal WorkerProductionRunPublication(Func<object, WorkerProductionRunGate> create,
            Func<bool, int> fallbackOriginal)
        {
            this.create = create ?? throw new ArgumentNullException(nameof(create));
            this.fallbackOriginal = fallbackOriginal ?? throw new ArgumentNullException(nameof(fallbackOriginal));
        }

        internal WorkerProductionRunGate Current => Volatile.Read(ref current);

        // Only the existing cold WorldSafety owner calls this method. The factory
        // constructs a gate; beforePublish is its fixed logger assignment, never a
        // consumer callback. A failed construction/assignment leaves Current alone.
        // Unbound initialization retains the old replacement behavior. Closing a
        // NULL admission interval reserves that exact gate; successful binding
        // retains it for process life, even when no suspension handle remains.
        internal WorkerProductionRunGate Initialize(object monitor, Action beforePublish)
        {
            if (monitor == null) throw new ArgumentNullException(nameof(monitor));
            if (beforePublish == null) throw new ArgumentNullException(nameof(beforePublish));
            lock (publicationGate)
            {
                WorkerProductionRunGate candidate = current;
                bool retain = admissionClosed || (candidate != null && candidate.HasNativeRunSuspensionBinding);
                if (retain)
                {
                    if (candidate == null || !ReferenceEquals(candidate.EngineMonitor, monitor))
                        throw new InvalidOperationException("The reserved native-run boundary monitor changed.");
                }
                else
                {
                    candidate = create(monitor);
                    if (candidate == null || !ReferenceEquals(candidate.EngineMonitor, monitor))
                        throw new InvalidOperationException("The native-run boundary factory returned another monitor.");
                }
                beforePublish();
                Volatile.Write(ref current, candidate);
                return candidate;
            }
        }

        // OnRun invokes this only after an earlier NULL read. Admission and the
        // complete old Original (including its Begin/End and tail work) form one
        // counted interval. Original and a late-NULL gate run occur OUTSIDE the
        // publication monitor. Closing reroutes late NULL captures to Current;
        // it never opens another uncounted path to the old Original.
        internal int RunFallback(bool mpFrameSkip)
        {
            bool counted = false, depthEntered = false;
            try
            {
                WorkerProductionRunGate routed;
                lock (publicationGate)
                {
                    if (admissionClosed)
                    {
                        routed = current;
                        // Closing normally reserves a non-NULL gate. If that
                        // invariant is ever lost, fail closed before Original.
                        if (routed == null) return 0;
                    }
                    else
                    {
                        routed = null;
                        if (fallbackCalls == int.MaxValue)
                            throw new InvalidOperationException("The native-run NULL fallback limit was reached.");
                        fallbackCalls++;
                        counted = true;
                    }
                }
                if (routed != null) return routed.Run(mpFrameSkip);
                fallbackDepth++;
                depthEntered = true;
                return fallbackOriginal(mpFrameSkip);
            }
            finally
            {
                if (depthEntered) fallbackDepth--;
                if (counted)
                {
                    lock (publicationGate)
                    {
                        fallbackCalls--;
                        if (fallbackCalls == 0 && bindingInProgress) Monitor.PulseAll(publicationGate);
                    }
                }
            }
        }

        // Finite synchronization budget, shared by publication acquisition and
        // draining. It is not an outer guard's hard deadline: trusted cold-owner
        // factory/logger/identity reads have their own fixed nonblocking contract.
        // Gate's identity providers are only EngineMonitor.Current metadata and a
        // volatile Current read in production; no subscriber callback or I/O runs
        // under this monitor. No engine monitor is acquired while holding it.
        internal bool TryBind(int millisecondsTimeout, object actualMonitor, out string reason)
        {
            if (!ValidateEntry(millisecondsTimeout, actualMonitor, out reason)) return false;
            long started = Stopwatch.GetTimestamp();
            if (!Monitor.TryEnter(publicationGate, Remaining(started, millisecondsTimeout)))
            { reason = "The native-run publication deadline expired."; return false; }
            bool ownsClosing = false;
            WorkerProductionRunGate candidate = null;
            try
            {
                while (bindingInProgress)
                {
                    int remaining = Remaining(started, millisecondsTimeout);
                    if (remaining == 0 || !Monitor.Wait(publicationGate, remaining))
                    { reason = "The native-run binding deadline expired."; return false; }
                }
                if (PositiveBudgetExpired(started, millisecondsTimeout))
                { reason = "The native-run publication deadline expired before binding."; return false; }
                candidate = current;
                if (candidate == null || !ReferenceEquals(candidate.EngineMonitor, actualMonitor))
                { reason = "The current native-run publication or monitor is unavailable."; return false; }
                if (bindingReady)
                {
                    reason = string.Empty;
                    return true;
                }
                bindingInProgress = true;
                admissionClosed = true;
                ownsClosing = true;
                while (fallbackCalls != 0)
                {
                    int remaining = Remaining(started, millisecondsTimeout);
                    if (remaining == 0 || !Monitor.Wait(publicationGate, remaining))
                    { reason = "The native-run NULL fallback drain deadline expired."; return false; }
                }
                if (PositiveBudgetExpired(started, millisecondsTimeout))
                { reason = "The native-run binding deadline expired before binding."; return false; }
                if (!candidate.TryBindNativeRunSuspension(out reason)) return false;
                // A trusted identity read is not an interruptible deadline. If it
                // finishes late after marking the gate, retain CLOSED admission
                // but do not publish readiness. A later successful bind may finish
                // this cold publication; acquisition cannot bypass the failure.
                if (PositiveBudgetExpired(started, millisecondsTimeout))
                { reason = "The native-run binding deadline expired before readiness."; return false; }
                bindingReady = true;
                reason = string.Empty;
                return true;
            }
            finally
            {
                if (ownsClosing)
                {
                    // Before successful Gate binding a failed first attempt restores
                    // old admission and replacement. A bound gate can never reopen
                    // NULL Original, even if a trusted cold read unexpectedly failed.
                    admissionClosed = bindingReady || candidate.HasNativeRunSuspensionBinding;
                    bindingInProgress = false;
                    Monitor.PulseAll(publicationGate);
                }
                Monitor.Exit(publicationGate);
            }
        }

        // Acquire never binds implicitly. In particular a failed/timed-out drain
        // cannot be bypassed through a gate that merely exists. Copy the exact
        // ready owner under publicationGate, then release it before the finite
        // engine acquisition, consuming only the remainder of the same budget.
        internal bool TryAcquire(int millisecondsTimeout, object actualMonitor,
            out WorkerProductionRunGate.NativeRunSuspension suspension, out string reason)
        {
            suspension = null;
            if (!ValidateEntry(millisecondsTimeout, actualMonitor, out reason)) return false;
            long started = Stopwatch.GetTimestamp();
            if (!Monitor.TryEnter(publicationGate, Remaining(started, millisecondsTimeout)))
            { reason = "The native-run publication deadline expired."; return false; }
            WorkerProductionRunGate candidate;
            try
            {
                candidate = current;
                if (!bindingReady || !admissionClosed || candidate == null ||
                    !candidate.HasNativeRunSuspensionBinding || !ReferenceEquals(candidate.EngineMonitor, actualMonitor))
                { reason = "The native-run suspension publication has not completed binding."; return false; }
            }
            finally { Monitor.Exit(publicationGate); }
            int remaining = Remaining(started, millisecondsTimeout);
            if (millisecondsTimeout > 0 && remaining == 0)
            { reason = "The native-run acquisition deadline expired before engine acquisition."; return false; }
            return candidate.TryAcquireNativeRunSuspension(remaining,
                out suspension, out reason);
        }

        private static bool ValidateEntry(int millisecondsTimeout, object actualMonitor, out string reason)
        {
            if (millisecondsTimeout < 0)
            { reason = "A finite nonnegative native-run publication timeout is required."; return false; }
            if (actualMonitor == null)
            { reason = "The actual engine monitor is unavailable."; return false; }
            if (fallbackDepth != 0 || Monitor.IsEntered(actualMonitor))
            { reason = "Native-run binding or acquisition cannot drain its own active run."; return false; }
            reason = string.Empty;
            return true;
        }

        private static int Remaining(long started, int millisecondsTimeout)
        {
            double remaining = millisecondsTimeout -
                (Stopwatch.GetTimestamp() - started) * (1000.0 / Stopwatch.Frequency);
            return remaining <= 0 ? 0 : (int)Math.Ceiling(remaining);
        }

        private static bool PositiveBudgetExpired(long started, int millisecondsTimeout) =>
            millisecondsTimeout > 0 && Remaining(started, millisecondsTimeout) == 0;
    }
}
