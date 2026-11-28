using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.WorldMutations
{
    internal readonly struct WorldMutationWorld
    {
        internal readonly long Session;
        internal readonly object NativeIdentity;
        internal WorldMutationWorld(long session, object identity) { Session = session; NativeIdentity = identity; }
    }

    // Process-owned registry. All game identity access is injected through one immutable native-world snapshot.
    // The same implementation is exercised without loading or calling the native game in offline tests.
    internal sealed class WorldMutationRegistry
    {
        internal sealed class Registration
        {
            internal string Owner, Id;
            internal int Order, Reported;
            internal WorldMutationSource Sources;
            internal IWorldMutationObserver Observer;
            internal Func<bool> Enabled;
        }
        internal sealed class Batch
        {
            internal long Id, Session, Generation;
            internal int Depth;
            internal bool Failed, WrapperFailed;
            internal Batch Previous;
            internal Registration[] Registrations;
            internal WorldMutationBatchCompletion Completion;
        }
        internal sealed class Invocation
        {
            internal WorldMutationContext Context;
            internal WorldMutationCompletion Completion;
            internal Batch Batch;
            internal long PreviousInvocation;
            internal readonly List<Active> Active = new List<Active>();
        }
        internal sealed class Active
        {
            internal Registration Registration;
            internal IWorldMutationInvocation Receipt;
        }
        private readonly object sync = new object();
        private readonly Func<WorldMutationWorld> readWorld;
        private readonly Action<Exception> report;
        private Registration[] registrations = Array.Empty<Registration>();
        private int available;
        private long sequence, generation, session;
        private object nativeIdentity;
        [ThreadStatic] private static Dictionary<WorldMutationRegistry, ThreadState> threads;
        private sealed class ThreadState { internal Batch Batch; internal long Invocation; }
        // Current-thread source mutation scope; does not allocate a TLS entry on an idle path.
        internal bool HasOpenBatch => threads != null && threads.TryGetValue(this, out var value) && value.Batch != null;

        internal WorldMutationRegistry(Func<WorldMutationWorld> readWorld, Action<Exception> report)
        { this.readWorld = readWorld ?? throw new ArgumentNullException(nameof(readWorld)); this.report = report; }
        internal WorldMutationSource AvailableSources => (WorldMutationSource)Volatile.Read(ref available);
        internal void SetAvailable(WorldMutationSource sources) => Volatile.Write(ref available, (int)sources);
        internal bool Register(string owner, string id, IWorldMutationObserver observer, WorldMutationSource sources,
            Func<bool> enabled, int order, out string reason)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || observer == null ||
                sources == WorldMutationSource.None || (sources & ~WorldMutationSource.All) != 0)
            { reason = "A nonempty owner/ID, observer and declared source mask are required."; return false; }
            lock (sync)
            {
                foreach (var existing in registrations)
                    if (existing.Owner == owner && existing.Id == id)
                    { reason = "Duplicate world-mutation owner and registration ID."; return false; }
                var next = new Registration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[next.Length - 1] = new Registration { Owner = owner, Id = id, Observer = observer,
                    Sources = sources, Enabled = enabled, Order = order };
                Array.Sort(next, (a, b) => { int compare = a.Order.CompareTo(b.Order);
                    if (compare == 0) compare = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
                    return compare != 0 ? compare : StringComparer.Ordinal.Compare(a.Id, b.Id); });
                Volatile.Write(ref registrations, next);
            }
            reason = string.Empty;
            return true;
        }
        private bool Enabled(Registration registration)
        {
            try { return registration.Enabled == null || registration.Enabled(); }
            catch (Exception error) { Report(registration, error); return false; }
        }
        internal bool HasDemand(WorldMutationSource sources)
        {
            if ((AvailableSources & sources) == 0) return false;
            var snapshot = Volatile.Read(ref registrations);
            foreach (var registration in snapshot)
                if ((registration.Sources & sources) != 0 && Enabled(registration)) return true;
            return false;
        }
        private bool HasAnyDemand()
        {
            var sources = AvailableSources;
            foreach (var registration in Volatile.Read(ref registrations))
                if ((registration.Sources & sources) != 0 && Enabled(registration)) return true;
            return false;
        }
        internal bool TryGetWorld(out long currentSession, out long currentGeneration)
        {
            var current = readWorld();
            lock (sync)
            {
                if (!ReferenceEquals(nativeIdentity, current.NativeIdentity) || session != current.Session)
                {
                    nativeIdentity = current.NativeIdentity;
                    session = current.Session;
                    generation = checked(generation + 1);
                }
                currentSession = session; currentGeneration = generation;
                return nativeIdentity != null && currentSession != 0;
            }
        }
        internal void Invalidate()
        {
            lock (sync) { nativeIdentity = null; session = 0; generation = checked(generation + 1); }
        }
        private ThreadState Thread()
        {
            if (threads == null) threads = new Dictionary<WorldMutationRegistry, ThreadState>();
            if (!threads.TryGetValue(this, out var value)) threads.Add(this, value = new ThreadState());
            return value;
        }
        internal long EnterBatch(WorldMutationSource source)
        {
            // No TLS dictionary, context, closure or buffer allocation without active demand.
            if (!HasAnyDemand()) return 0;
            TryGetWorld(out long currentSession, out long currentGeneration);
            var thread = Thread();
            var batch = thread.Batch;
            if (batch != null && batch.Session == currentSession && batch.Generation == currentGeneration)
            { batch.Depth++; return batch.Id; }
            batch = new Batch { Id = Interlocked.Increment(ref sequence), Session = currentSession,
                Generation = currentGeneration, Depth = 1, Previous = batch,
                Registrations = Volatile.Read(ref registrations) };
            batch.Completion = new WorldMutationBatchCompletion(batch.Id, currentSession, currentGeneration, source);
            thread.Batch = batch;
            return batch.Id;
        }
        internal void LeaveBatch(long token, bool returnedNormally)
        {
            if (token == 0) return;
            var thread = Thread();
            var batch = thread.Batch;
            if (batch == null || batch.Id != token) return;
            if (!returnedNormally) batch.WrapperFailed = true;
            if (--batch.Depth != 0) return;
            thread.Batch = batch.Previous;
            bool current = TryGetWorld(out long currentSession, out long currentGeneration) &&
                currentSession == batch.Session && currentGeneration == batch.Generation;
            batch.Completion.Set(returnedNormally && !batch.Failed && !batch.WrapperFailed, batch.Failed, current);
            foreach (var registration in batch.Registrations)
            {
                // Batch membership is snapshotted. Disabled registrations receive no new publications.
                if (!Enabled(registration)) continue;
                try { registration.Observer.OnBatchCompleted(batch.Completion); }
                catch (Exception error) { Report(registration, error); }
            }
        }
        internal Invocation Begin(WorldMutationSource source, long[] args)
        {
            if (!HasDemand(source)) return null;
            var thread = Thread();
            if (thread.Batch == null) throw new InvalidOperationException("A world-mutation original requires its outer batch scope.");
            TryGetWorld(out long currentSession, out long currentGeneration);
            var state = new Invocation { Batch = thread.Batch, PreviousInvocation = thread.Invocation };
            state.Context = new WorldMutationContext(source, Interlocked.Increment(ref sequence), thread.Invocation,
                thread.Batch.Id, currentSession, currentGeneration, args);
            state.Completion = new WorldMutationCompletion(state.Context);
            thread.Invocation = state.Context.InvocationId;
            foreach (var registration in thread.Batch.Registrations)
            {
                if ((registration.Sources & source) == 0 || !Enabled(registration)) continue;
                try
                {
                    var receipt = registration.Observer.TryBegin(state.Context);
                    if (receipt != null) state.Active.Add(new Active { Registration = registration, Receipt = receipt });
                }
                catch (Exception error) { Report(registration, error); }
            }
            return state;
        }
        internal void Complete(Invocation state, bool originalInvoked, bool originalReturned, long result, Exception error)
        {
            if (state == null) return;
            state.Completion.Set(originalInvoked, originalReturned, result, error);
            if (!originalInvoked || !originalReturned) state.Batch.Failed = true;
            try
            {
                for (int index = state.Active.Count - 1; index >= 0; index--)
                {
                    var active = state.Active[index];
                    try { active.Receipt.Complete(state.Completion); }
                    catch (Exception callbackError) { Report(active.Registration, callbackError); }
                }
            }
            finally { Thread().Invocation = state.PreviousInvocation; }
        }
        private void Report(Registration registration, Exception error)
        {
            // Reporting is itself foreign code. Neither interpolation nor a reporter failure may escape.
            if (Interlocked.Exchange(ref registration.Reported, 1) != 0) return;
            try { report?.Invoke(error); } catch { }
        }
    }
}
