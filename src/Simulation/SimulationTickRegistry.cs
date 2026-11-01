using System;
using System.Threading;

namespace APIShared.Simulation
{
    internal delegate bool ReadSimulationTick(out long sessionId, out int mapTick);

    // External boundaries are injected; the same registry is exercised by offline two-peer fixtures.
    internal sealed class SimulationTickRegistry
    {
        private readonly object sync = new object();
        private readonly ReadSimulationTick read;
        private readonly Action<Action<int>> activate;
        private readonly Action<string, Exception> error;
        private Registration[] registrations = Array.Empty<Registration>();
        private bool activated, hasTick;
        private long lastSession, failures;
        private int lastTick, publishing;

        internal SimulationTickRegistry(ReadSimulationTick read, Action<Action<int>> activate,
            Action<string, Exception> error = null)
        {
            this.read = read ?? throw new ArgumentNullException(nameof(read));
            this.activate = activate ?? throw new ArgumentNullException(nameof(activate));
            this.error = error;
        }

        internal long CallbackFailures => Interlocked.Read(ref failures);

        internal bool TryRegister(string owner, string id, Action<int> callback, int order, out string reason)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || callback == null)
            { reason = "Owner GUID, registration ID and a callback are required."; return false; }
            lock (sync)
            {
                foreach (Registration existing in registrations)
                    if (existing.Owner == owner && existing.Id == id)
                    { reason = "This owner already registered that ID."; return false; }
                if (!activated)
                {
                    try { activate(OnSourceTick); activated = true; }
                    catch (Exception ex) { reason = "Simulation tick source unavailable: " + ex.Message; return false; }
                }
                var next = new Registration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[next.Length - 1] = new Registration(owner, id, callback, order);
                Array.Sort(next, Compare);
                Volatile.Write(ref registrations, next);
            }
            reason = string.Empty;
            return true;
        }

        internal bool TryGetCurrentTick(out int mapTick)
        {
            mapTick = 0;
            try { if (read(out _, out int value)) { mapTick = value; return true; } }
            catch (Exception ex) { Report("read", ex); }
            return false;
        }

        // The incoming SE tick is deliberately ignored: it counts local engine runs, not map steps.
        internal void OnSourceTick(int ignoredLocalTick)
        {
            Registration[] snapshot = Volatile.Read(ref registrations);
            if (snapshot.Length == 0 || Interlocked.CompareExchange(ref publishing, 1, 0) != 0) return;
            try
            {
                if (!read(out long session, out int tick)) return;
                if (hasTick && session == lastSession && unchecked(tick - lastTick) <= 0) return;
                lastSession = session;
                lastTick = tick;
                hasTick = true;
                foreach (Registration registration in snapshot)
                    try { registration.Callback(tick); }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failures);
                        Report(registration.Owner + "/" + registration.Id, ex);
                    }
            }
            catch (Exception ex) { Report("source", ex); }
            finally { Volatile.Write(ref publishing, 0); }
        }

        private void Report(string operation, Exception ex)
        {
            try { error?.Invoke(operation, ex); }
            catch { /* Diagnostics never interrupt another callback or the native simulation. */ }
        }

        private static int Compare(Registration a, Registration b)
        {
            int result = a.Order.CompareTo(b.Order);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.Id, b.Id);
        }

        private sealed class Registration
        {
            internal readonly string Owner, Id;
            internal readonly Action<int> Callback;
            internal readonly int Order;
            internal Registration(string owner, string id, Action<int> callback, int order)
            { Owner = owner; Id = id; Callback = callback; Order = order; }
        }
    }
}
