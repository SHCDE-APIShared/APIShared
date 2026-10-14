using System;
using System.Collections.Generic;
using System.Threading;
using SHCDESE.Interop;

namespace APIShared.Units
{
    // Immediate facts about one unit slot. A default value means the ID did not resolve.
    internal struct UnitSettleProbe
    {
        internal bool Found;
        // Vanilla combat life predicate (UnitAccess.IsReallyAlive).
        internal bool Alive;
        // AliveState None or MarkedForDeletion, or the native death marker. Recruitment's transient
        // AliveState 4 and NeedsInit are neither alive nor dead: those units keep waiting.
        internal bool Dead;
        internal int GlobalId;
        internal int PlayerId;
        internal eChimps UnitType;
    }

    // Must not throw; the production reader returns a default probe for an unresolved ID.
    internal interface IUnitSettleReader
    {
        UnitSettleProbe Read(int unitId);
    }

    // Dependency-free core of UnitSettledEvents: owner-ordered registrations, the pending queue and
    // the settle rule. The SE adapter feeds it trigger events from any thread and ticks from the
    // simulation update loop; callbacks run on the ticking thread outside both locks.
    internal sealed class UnitSettledTracker
    {
        internal const int MaximumObservedTicks = 3000;
        internal const int MaximumPendingUnits = 8192;

        private readonly object registrationSync = new object();
        private readonly object queueSync = new object();
        private readonly IUnitSettleReader reader;
        private readonly Action activate;
        private bool activated;
        private Registration[] registrations = Array.Empty<Registration>();
        // Guarded by queueSync. Trigger order is preserved; settled entries leave in that order.
        private readonly List<Pending> pending = new List<Pending>(64);
        // Filled under queueSync and read only by the tick that set dispatching.
        private readonly List<UnitSettledNotification> ready = new List<UnitSettledNotification>(64);
        private bool dispatching;
        // Lock-free hint for the per-tick fast path; written under queueSync.
        private int pendingCount;
        private long failures, dropped;
        private Action<string, Exception> errorSink;

        // activate runs once, under the registration lock, when the first valid registration arrives.
        internal UnitSettledTracker(IUnitSettleReader reader, Action activate)
        {
            this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
            this.activate = activate;
        }

        internal long CallbackFailures => Interlocked.Read(ref failures);
        internal long DroppedUnits => Interlocked.Read(ref dropped);
        internal int PendingUnits => Volatile.Read(ref pendingCount);
        internal bool IsActivated { get { lock (registrationSync) return activated; } }
        internal void SetErrorSink(Action<string, Exception> sink) => Volatile.Write(ref errorSink, sink);

        internal bool TryRegister(string owner, string id, Action<UnitSettledNotification> callback, int order, out string reason)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || callback == null)
            { reason = "Owner GUID, registration ID and a callback are required."; return false; }
            lock (registrationSync)
            {
                foreach (Registration existing in registrations)
                    if (existing.Owner == owner && existing.Id == id)
                    { reason = "This owner already registered that ID."; return false; }
                if (!activated)
                {
                    // Demand starts here: no Script Extender subscription exists before this point.
                    try { activate?.Invoke(); activated = true; }
                    catch (Exception ex) { reason = "Script Extender unit events are unavailable: " + ex.Message; return false; }
                }
                var next = new Registration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[next.Length - 1] = new Registration(owner, id, order, callback);
                Array.Sort(next, (a, b) => {
                    int comparison = a.Order.CompareTo(b.Order);
                    if (comparison == 0) comparison = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
                    return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Id, b.Id);
                });
                Volatile.Write(ref registrations, next);
            }
            reason = string.Empty; return true;
        }

        // SE OnUnitCreate Post / OnUnitTransition Pre. The slot is read now so that later identity,
        // owner and type comparisons refer to the state at the trigger.
        internal void Enqueue(int unitId, UnitSettledCause cause)
        {
            if (unitId <= 0) return;
            UnitSettleProbe unit = reader.Read(unitId);
            lock (queueSync)
            {
                if (!unit.Found || unit.Dead || pending.Count >= MaximumPendingUnits)
                { Interlocked.Increment(ref dropped); return; }
                pending.Add(new Pending(unitId, cause, unit.GlobalId, unit.PlayerId, unit.UnitType));
                Volatile.Write(ref pendingCount, pending.Count);
            }
        }

        // SE OnTick. Idle ticks perform one volatile read; settlement work stays in a separate method
        // so no compiler-generated state precedes this guard.
        internal void Tick(int simulationTick)
        {
            if (Volatile.Read(ref pendingCount) == 0) return;
            SettleAndDispatch(simulationTick);
        }

        // SE OnUnloadMap/OnLoadMap/OnLoadSave Pre: queued unit IDs refer to the previous world.
        internal void Discard()
        {
            lock (queueSync)
            {
                if (pending.Count == 0) return;
                Interlocked.Add(ref dropped, pending.Count);
                pending.Clear();
                Volatile.Write(ref pendingCount, 0);
            }
        }

        private void SettleAndDispatch(int simulationTick)
        {
            lock (queueSync)
            {
                // Callbacks never re-enter notification; a nested tick leaves the queue for the next one.
                if (dispatching) return;
                int keep = 0;
                for (int index = 0; index < pending.Count; index++)
                {
                    Pending entry = pending[index];
                    UnitSettleProbe unit;
                    try { unit = reader.Read(entry.UnitId); }
                    catch { unit = default; }
                    switch (Evaluate(ref entry, in unit))
                    {
                        case Decision.Wait:
                            pending[keep++] = entry;
                            break;
                        case Decision.Settle:
                            ready.Add(new UnitSettledNotification(entry.UnitId, unit.GlobalId, unit.PlayerId, unit.UnitType,
                                entry.Cause == UnitSettledCause.Created ? default(eChimps) : entry.TriggerType,
                                entry.Cause, simulationTick, entry.Observed));
                            break;
                        default:
                            Interlocked.Increment(ref dropped);
                            break;
                    }
                }
                pending.RemoveRange(keep, pending.Count - keep);
                Volatile.Write(ref pendingCount, keep);
                if (ready.Count == 0) return;
                dispatching = true;
            }
            try { Dispatch(); }
            finally
            {
                ready.Clear();
                lock (queueSync) dispatching = false;
            }
        }

        // Units in trigger order; for each unit every registration in order/owner/ID order. Triggers
        // raised by callbacks wait in the queue for the next tick.
        private void Dispatch()
        {
            Registration[] snapshot = Volatile.Read(ref registrations);
            for (int unit = 0; unit < ready.Count; unit++)
            {
                UnitSettledNotification notification = ready[unit];
                for (int index = 0; index < snapshot.Length; index++)
                {
                    try { snapshot[index].Callback(notification); }
                    catch (Exception ex) { Fail(snapshot[index], ex); }
                }
            }
        }

        // The settle rule documented at UnitSettledEvents.TryRegister.
        internal static Decision Evaluate(ref Pending entry, in UnitSettleProbe unit)
        {
            if (!unit.Found || unit.Dead || unit.PlayerId != entry.PlayerId ||
                entry.GlobalId != 0 && unit.GlobalId != entry.GlobalId)
                return Decision.Drop;
            entry.Observed++;
            if (unit.Alive && unit.GlobalId != 0 &&
                (entry.Cause == UnitSettledCause.Created || unit.UnitType != entry.TriggerType))
                return Decision.Settle;
            return entry.Observed >= MaximumObservedTicks ? Decision.Drop : Decision.Wait;
        }

        private void Fail(Registration registration, Exception error)
        {
            Interlocked.Increment(ref failures);
            long count = Interlocked.Increment(ref registration.Failures);
            if ((count & (count - 1)) != 0) return;
            // Logging must never compromise another observer or the simulation tick.
            try { Volatile.Read(ref errorSink)?.Invoke(registration.Owner + "/" + registration.Id + " (failure " + count + ")", error); }
            catch { }
        }

        internal enum Decision { Wait, Settle, Drop }

        internal struct Pending
        {
            internal readonly int UnitId;
            internal readonly UnitSettledCause Cause;
            internal readonly int GlobalId;
            internal readonly int PlayerId;
            internal readonly eChimps TriggerType;
            internal int Observed;

            internal Pending(int unitId, UnitSettledCause cause, int globalId, int playerId, eChimps triggerType)
            {
                UnitId = unitId; Cause = cause; GlobalId = globalId; PlayerId = playerId; TriggerType = triggerType;
                Observed = 0;
            }
        }

        private sealed class Registration
        {
            internal readonly string Owner, Id;
            internal readonly int Order;
            internal readonly Action<UnitSettledNotification> Callback;
            internal long Failures;
            internal Registration(string owner, string id, int order, Action<UnitSettledNotification> callback)
            { Owner = owner; Id = id; Order = order; Callback = callback; }
        }
    }
}
