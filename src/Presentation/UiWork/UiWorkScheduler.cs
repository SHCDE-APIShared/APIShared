using System;
using System.Collections.Generic;
using System.Threading;
using APIShared.Events;

namespace APIShared.Presentation
{
    // The Unity boundary is injected once, so thread/startup guarantees can be tested
    // without invoking Unity native APIs. A failed attachment never publishes readiness.
    internal sealed class UiWorkFramePump
    {
        private readonly UiWorkScheduler scheduler;
        private readonly Func<bool> isUnityThread;
        private readonly Func<int> frame;
        private bool attached;
        internal UiWorkFramePump(UiWorkScheduler scheduler, Func<bool> isUnityThread, Func<int> frame)
        { this.scheduler = scheduler; this.isUnityThread = isUnityThread; this.frame = frame; }
        internal void Attach(Action<Action> subscribe)
        {
            if (!isUnityThread()) throw new InvalidOperationException("UI work must initialize on the validated Unity thread.");
            if (attached) return;
            subscribe(OnBeforeRender); attached = true;
        }
        private void OnBeforeRender()
        {
            if (attached && isUnityThread()) scheduler.Pump(frame());
        }
    }

    // Game-independent scheduling state. Only Pump invokes callbacks; callers supply the Unity frame.
    // Published snapshots make idle/waiting/continuous frames allocation-free. Rare demand changes
    // copy arrays, and callbacks run outside the lock after current requests have been consumed.
    internal sealed class UiWorkScheduler
    {
        private const long Immediate = long.MinValue;
        private readonly object sync = new object();
        private readonly List<Entry> entries = new List<Entry>();
        private readonly Func<long> clock;
        private readonly long frequency;
        private readonly Action<string, Exception> report;
        private Snapshot snapshot = new Snapshot(Array.Empty<Entry>(), long.MaxValue);
        private bool hasFrame;
        private int lastFrame;

        internal UiWorkScheduler(Func<long> clock, long frequency, Action<string, Exception> report)
        {
            if (clock == null || frequency <= 0) throw new ArgumentException("A monotonic clock and positive frequency are required.");
            this.clock = clock; this.frequency = frequency; this.report = report;
        }

        internal bool TryRegister(string owner, string id, Action callback, int order,
            out IUiFrameWorkRegistration registration, out string reason)
        {
            registration = null;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || callback == null)
            { reason = "Owner GUID, registration ID and callback are required."; return false; }
            lock (sync)
            {
                foreach (Entry entry in entries)
                    if (entry.Owner == owner && entry.Id == id)
                    { reason = "This owner already registered that ID."; return false; }
                var added = new Entry(this, owner, id, callback, order);
                entries.Add(added);
                entries.Sort((a, b) => RegistrationKey.Compare(Entry.Key(a), Entry.Key(b)));
                registration = added;
            }
            reason = string.Empty; return true;
        }

        private void Publish()
        {
            int count = 0; long next = long.MaxValue;
            foreach (Entry entry in entries)
            {
                if (entry.Continuous || entry.Deadline == Immediate) count++;
                if (entry.Deadline != Immediate && entry.Deadline < next) next = entry.Deadline;
            }
            Entry[] active = count == 0 ? Array.Empty<Entry>() : new Entry[count];
            int index = 0;
            foreach (Entry entry in entries)
                if (entry.Continuous || entry.Deadline == Immediate) active[index++] = entry;
            Volatile.Write(ref snapshot, new Snapshot(active, next));
        }

        internal void Pump(int frame)
        {
            Snapshot observed = Volatile.Read(ref snapshot);
            if (observed.Active.Length == 0 && observed.Next == long.MaxValue) return;
            long now = clock();
            if (observed.Active.Length == 0 && now < observed.Next) return;
            Entry[] ready;
            lock (sync)
            {
                if (hasFrame && lastFrame == frame) return;
                observed = snapshot;
                bool timedDue = observed.Next != long.MaxValue && now >= observed.Next;
                if (observed.Active.Length == 0 && !timedDue) return;
                hasFrame = true; lastFrame = frame;
                ready = observed.Active;
                if (timedDue)
                {
                    int count = 0;
                    foreach (Entry entry in entries)
                        if (entry.Continuous || entry.Deadline == Immediate || entry.Deadline != long.MaxValue && entry.Deadline <= now) count++;
                    ready = new Entry[count]; int index = 0;
                    foreach (Entry entry in entries)
                        if (entry.Continuous || entry.Deadline == Immediate || entry.Deadline != long.MaxValue && entry.Deadline <= now) ready[index++] = entry;
                }
                bool consumed = false;
                foreach (Entry entry in ready)
                    if (entry.Deadline == Immediate || entry.Deadline != long.MaxValue && entry.Deadline <= now)
                    { entry.Deadline = long.MaxValue; consumed = true; }
                if (consumed) Publish();
            }
            foreach (Entry entry in ready)
            {
                try { entry.Callback(); }
                catch (Exception error)
                {
                    if (FailureThrottle.ShouldReport(ref entry.Failures))
                        try { report?.Invoke(entry.Owner + "/" + entry.Id, error); } catch { }
                }
            }
        }

        private sealed class Snapshot
        {
            internal readonly Entry[] Active;
            internal readonly long Next;
            internal Snapshot(Entry[] active, long next) { Active = active; Next = next; }
        }

        private sealed class Entry : IUiFrameWorkRegistration
        {
            private readonly UiWorkScheduler scheduler;
            internal readonly string Owner, Id;
            internal readonly int Order;
            internal readonly Action Callback;
            internal bool Continuous;
            internal long Deadline = long.MaxValue, Failures;
            internal Entry(UiWorkScheduler scheduler, string owner, string id, Action callback, int order)
            { this.scheduler = scheduler; Owner = owner; Id = id; Callback = callback; Order = order; }
            internal static readonly Func<Entry, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id, entry.Order);
            public void Request() => SetDeadline(Immediate);
            public void RequestAfter(TimeSpan delay)
            {
                if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
                if (delay == TimeSpan.Zero) { Request(); return; }
                // Decimal conversion rounds upward without double precision accepting an early deadline.
                decimal ticks;
                try { ticks = decimal.Ceiling((decimal)delay.Ticks * scheduler.frequency / TimeSpan.TicksPerSecond); }
                catch (OverflowException) { throw new ArgumentOutOfRangeException(nameof(delay), "The monotonic delay would overflow."); }
                long now = scheduler.clock();
                if (ticks >= long.MaxValue || now >= long.MaxValue - ticks)
                    throw new ArgumentOutOfRangeException(nameof(delay), "The monotonic deadline would overflow.");
                SetDeadline(now + (long)ticks);
            }
            private void SetDeadline(long value)
            {
                lock (scheduler.sync)
                {
                    if (value >= Deadline) return;
                    Deadline = value; scheduler.Publish();
                }
            }
            public void CancelPending()
            {
                lock (scheduler.sync)
                {
                    if (Deadline == long.MaxValue) return;
                    Deadline = long.MaxValue; scheduler.Publish();
                }
            }
            public void SetContinuous(bool enabled)
            {
                lock (scheduler.sync)
                {
                    if (Continuous == enabled) return;
                    Continuous = enabled; scheduler.Publish();
                }
            }
        }
    }
}
