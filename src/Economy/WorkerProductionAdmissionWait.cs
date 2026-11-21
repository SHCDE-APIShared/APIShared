using System;
using System.Diagnostics;

namespace APIShared.Economy
{
    // State for one mission. The owner serializes scheduling, cancellation and
    // delivery under the engine monitor, including lifecycle publisher threads.
    internal sealed class WorkerProductionAdmissionWait
    {
        internal const int TimeoutSeconds = 30;
        private readonly Func<long> now;
        private readonly Func<Action, TimeSpan, IDisposable> schedule;
        private readonly Func<bool> retry;
        private readonly Action timeout;
        private readonly long deadline;
        private IDisposable pending;
        private int sequence;
        private bool cancelled;

        internal WorkerProductionAdmissionWait(Func<long> now, Func<Action, TimeSpan, IDisposable> schedule,
            Func<bool> retry, Action timeout)
        {
            this.now = now;
            this.schedule = schedule;
            this.retry = retry;
            this.timeout = timeout;
            deadline = now() + TimeoutSeconds * Stopwatch.Frequency;
        }

        internal bool Expired => now() >= deadline;

        internal void Arm()
        {
            if (cancelled || pending != null) return;
            long remaining = deadline - now();
            var delay = TimeSpan.FromSeconds(Math.Min(1, Math.Max(0, (double)remaining / Stopwatch.Frequency)));
            int expected = ++sequence;
            pending = schedule(() => Advance(expected), delay);
        }

        internal void Cancel()
        {
            cancelled = true;
            pending?.Dispose();
            pending = null;
        }

        private void Advance(int expected)
        {
            if (cancelled || expected != sequence || pending == null) return;
            pending.Dispose();
            pending = null;
            if (Expired)
            {
                Cancel();
                timeout();
                return;
            }
            try { if (!retry()) Cancel(); }
            finally { Arm(); }
        }
    }
}
