using System;
using System.Threading;

namespace APIShared.Events
{
    /// <summary>Invocation-local mutable input. Setters are valid only during Pre on the publishing thread.</summary>
    public abstract class InterceptionPreEventArgs
    {
        private bool frozen, skip;
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private object state;
        internal InterceptionPreEventArgs() { }
        /// <summary>A veto is sticky across successful callbacks. A failing callback's veto is rolled back.</summary>
        public bool SkipOriginalFunction { get => skip; set { CheckMutable(); skip |= value; } }
        /// <summary>Private state for this registration's matching Post. Never shared with another registration.</summary>
        public object State { get => state; set { CheckMutable(); state = value; } }
        internal void CheckMutable()
        {
            if (frozen || thread != Thread.CurrentThread.ManagedThreadId)
                throw new InvalidOperationException("Pre arguments are mutable only inside their synchronous notification.");
        }
        internal void Freeze() { frozen = true; state = null; ClearCapture(); }
        internal virtual void ClearCapture() { }
        internal void ResetState() { state = null; }
        internal bool Veto => skip;
        internal void RejectAfterPre() { skip = true; }
        internal abstract object Capture();
        internal abstract void Restore(object snapshot);
        internal void RestoreVeto(bool value) { skip = value; state = null; }
    }

    // One registry per named interception point. Immutable snapshots prevent registration during
    // Pre from receiving a mismatched Post. The original call lives outside callback error handling.
    internal sealed class InterceptionEvent<TPre, TPost> where TPre : InterceptionPreEventArgs
    {
        private readonly object sync = new object();
        private Registration[] registrations = Array.Empty<Registration>();
        private readonly Action install;
        private readonly Func<bool> hasDemand;
        private bool installed;
        private long failures;
        private FilteredSnapshot lastFiltered;
        private sealed class FilteredSnapshot
        {
            internal FilteredSnapshot(Registration[] source, ulong mask, Registration[] result) { Source = source; Mask = mask; Result = result; }
            internal readonly Registration[] Source;
            internal readonly ulong Mask;
            internal readonly Registration[] Result;
        }
        [ThreadStatic] private static Invocation notification;
        // Cached demand is opt-in and requires ownership of every activation transition.
        internal InterceptionEvent(Action install = null, Func<bool> hasDemand = null) { this.install = install; this.hasDemand = hasDemand; }
        internal long CallbackFailures => Interlocked.Read(ref failures);
        internal bool HasObservers
        {
            get
            {
                if (hasDemand != null) return hasDemand();
                foreach (Registration entry in Volatile.Read(ref registrations))
                    if (IsActive(entry)) return true;
                return false;
            }
        }
        internal bool CanNotify => notification == null && HasObservers;
        internal bool BlocksNestedCall(int key) => notification?.BlocksNestedCall(this, key) == true;
        internal bool TryRegister(string owner, string id, Action<TPre> pre, Action<TPost> post,
            out string reason, int order = 0, Action<TPre, object> accepted = null, Func<bool> isActive = null,
            Func<TPre, object, bool> finalValidation = null, Func<int, bool> blocksNested = null)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) ||
                (pre == null && post == null && accepted == null && finalValidation == null))
            { reason = "Owner GUID, registration ID and at least one callback are required."; return false; }
            lock (sync)
            {
                if (OrderedRegistrations.Contains(registrations, Registration.Key, owner, id))
                { reason = "This owner already registered that ID."; return false; }
                if (!installed)
                {
                    try { install?.Invoke(); installed = true; }
                    catch (Exception ex) { reason = "Interception unavailable: " + ex.Message; return false; }
                }
                Volatile.Write(ref registrations, OrderedRegistrations.WithAdded(
                    registrations, new Registration(owner, id, pre, post, order, accepted, isActive,
                        finalValidation, blocksNested), Registration.Key));
            }
            reason = string.Empty; return true;
        }

        internal Invocation Begin(TPre args)
        {
            Registration[] snapshot = notification == null ? Volatile.Read(ref registrations) : Array.Empty<Registration>();
            // Freeze demand with the
            // invocation so enabling/disabling during Pre cannot mismatch Accepted/Post.
            // Each predicate runs exactly once, in registration order. A dormant service
            // registration would otherwise force a new filtered array on every call of a frequent
            // publisher; an identical (registration array, active set) reuses the previously
            // built immutable result. Invocations never mutate their snapshot array.
            int count = snapshot.Length;
            if (count != 0)
            {
                Span<bool> active = count <= 256 ? stackalloc bool[count] : new bool[count];
                int activeCount = 0;
                ulong activeMask = 0;
                for (int index = 0; index < count; index++)
                {
                    Registration entry = snapshot[index];
                    active[index] = IsActive(entry);
                    if (!active[index]) continue;
                    activeCount++;
                    if (index < 64) activeMask |= 1UL << index;
                }
                if (activeCount == 0) snapshot = Array.Empty<Registration>();
                else if (activeCount != count)
                {
                    FilteredSnapshot cached = count <= 64 ? Volatile.Read(ref lastFiltered) : null;
                    if (cached != null && ReferenceEquals(cached.Source, snapshot) && cached.Mask == activeMask)
                        snapshot = cached.Result;
                    else
                    {
                        var filtered = new Registration[activeCount];
                        for (int index = 0, next = 0; index < count; index++)
                            if (active[index]) filtered[next++] = snapshot[index];
                        if (count <= 64) Volatile.Write(ref lastFiltered, new FilteredSnapshot(snapshot, activeMask, filtered));
                        snapshot = filtered;
                    }
                }
            }
            var invocation = new Invocation(this, snapshot, args);
            var previous = notification;
            notification = invocation;
            try
            {
                for (int index = 0; index < snapshot.Length; index++)
                {
                    if (snapshot[index].Pre == null) continue;
                    args.ResetState();
                    object before = args.Capture();
                    bool skip = args.Veto;
                    try { snapshot[index].Pre?.Invoke(args); if (invocation.States.Length != 0) invocation.States[index] = args.State; }
                    catch (Exception ex)
                    {
                        args.Restore(before); args.RestoreVeto(skip);
                        Interlocked.Increment(ref failures);
                        LogFailure(snapshot[index], "Pre", ex);
                    }
                }
            }
            finally { notification = previous; args.Freeze(); }
            return invocation;
        }

        private bool IsActive(Registration entry)
        {
            if (entry.IsActive == null) return true;
            if (entry.FinalValidation == null) return entry.IsActive();
            try { return entry.IsActive(); }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                LogFailure(entry, "IsActive", ex);
                return true;
            }
        }

        private static void LogFailure(Registration registration, string phase, Exception error)
        {
            if (!FailureThrottle.ShouldReport(ref registration.Failures)) return;
            // Logging must never compromise the original or another observer.
            try { InterceptionDiagnostics.Report(registration.Owner + "/" + registration.Id + " " + phase, error); } catch { }
        }

        internal sealed class Invocation
        {
            private readonly InterceptionEvent<TPre, TPost> owner;
            private readonly Registration[] snapshot;
            private readonly bool hasFinalValidators;
            private bool completed;
            private bool acceptedNotified;
            internal readonly object[] States;
            internal readonly TPre Pre;
            internal bool HasPostObservers { get; }
            internal Invocation(InterceptionEvent<TPre, TPost> owner, Registration[] snapshot, TPre pre)
            {
                this.owner = owner; this.snapshot = snapshot; Pre = pre;
                bool needsStates = false;
                foreach (Registration entry in snapshot) {
                    if (entry.Post != null) HasPostObservers = true;
                    if (entry.FinalValidation != null) hasFinalValidators = true;
                    if (entry.Pre != null && (entry.Post != null || entry.Accepted != null || entry.FinalValidation != null))
                        needsStates = true;
                }
                States = needsStates ? new object[snapshot.Length] : Array.Empty<object>();
            }
            internal bool BlocksNestedCall(InterceptionEvent<TPre, TPost> registry, int key)
            {
                if (owner != registry || !hasFinalValidators) return false;
                foreach (var entry in snapshot)
                    if (entry.BlocksNested?.Invoke(key) == true) return true;
                return false;
            }
            internal void ValidateFinalInputs()
            {
                if (!hasFinalValidators || Pre.SkipOriginalFunction) return;
                var previous = notification;
                notification = this;
                try
                {
                    for (int index = 0; index < snapshot.Length; index++)
                    {
                        var entry = snapshot[index];
                        if (entry.FinalValidation == null) continue;
                        try
                        {
                            if (entry.FinalValidation(Pre, States.Length == 0 ? null : States[index])) continue;
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref owner.failures);
                            LogFailure(entry, "FinalValidation", ex);
                        }
                        Pre.RejectAfterPre();
                        return;
                    }
                }
                finally { notification = previous; }
            }
            internal void NotifyAccepted()
            {
                if (acceptedNotified || completed || Pre.SkipOriginalFunction) return;
                acceptedNotified = true;
                var previous = notification;
                notification = this;
                try
                {
                    for (int index = 0; index < snapshot.Length; index++)
                    {
                        try { snapshot[index].Accepted?.Invoke(Pre, States.Length == 0 ? null : States[index]); }
                        catch (Exception ex) { Interlocked.Increment(ref owner.failures); LogFailure(snapshot[index], "Accepted", ex); }
                    }
                }
                finally { notification = previous; }
            }
            private static readonly Func<Func<object, TPost>, object, TPost> LegacyPostFactory = (factory, state) => factory(state);
            internal void Complete(Func<object, TPost> createPost) => Complete(createPost, LegacyPostFactory);
            // Explicit factory context avoids a closure at the caller. Both entrypoints use this loop.
            internal void Complete<TContext>(TContext context, Func<TContext, object, TPost> createPost)
            {
                if (completed) return;
                completed = true;
                var previous = notification;
                notification = this;
                try
                {
                    for (int index = 0; index < snapshot.Length; index++)
                    {
                        if (snapshot[index].Post == null) continue;
                        try { snapshot[index].Post(createPost(context, States.Length == 0 ? null : States[index])); }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref owner.failures);
                            LogFailure(snapshot[index], "Post", ex);
                        }
                    }
                }
                finally { notification = previous; }
            }
        }
        internal sealed class Registration
        {
            internal long Failures;
            internal readonly string Owner, Id;
            internal readonly int Order;
            internal readonly Action<TPre> Pre;
            internal readonly Action<TPost> Post;
            internal readonly Action<TPre, object> Accepted;
            internal readonly Func<bool> IsActive;
            internal readonly Func<TPre, object, bool> FinalValidation;
            internal readonly Func<int, bool> BlocksNested;
            internal static readonly Func<Registration, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id, entry.Order);
            internal Registration(string owner, string id, Action<TPre> pre, Action<TPost> post, int order,
                Action<TPre, object> accepted, Func<bool> isActive, Func<TPre, object, bool> finalValidation,
                Func<int, bool> blocksNested)
            {
                Owner = owner; Id = id; Pre = pre; Post = post; Order = order; Accepted = accepted;
                IsActive = isActive; FinalValidation = finalValidation; BlocksNested = blocksNested;
            }
        }
    }

    internal static class InterceptionDiagnostics
    {
        internal static Action<string, Exception> Error;
        internal static void Report(string registration, Exception error)
        { try { Error?.Invoke(registration, error); } catch { } }
    }
}
