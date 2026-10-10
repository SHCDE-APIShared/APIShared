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
        internal void Freeze() { frozen = true; state = null; }
        internal void ResetState() { state = null; }
        internal bool Veto => skip;
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
        private bool installed;
        private long failures;
        [ThreadStatic] private static int notificationDepth;
        internal InterceptionEvent(Action install = null) { this.install = install; }
        internal long CallbackFailures => Interlocked.Read(ref failures);
        internal bool HasObservers => Volatile.Read(ref registrations).Length != 0;
        internal bool TryRegister(string owner, string id, Action<TPre> pre, Action<TPost> post,
            out string reason, int order = 0, Action<TPre, object> accepted = null)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || (pre == null && post == null && accepted == null))
            { reason = "Owner GUID, registration ID and at least one callback are required."; return false; }
            lock (sync)
            {
                foreach (Registration entry in registrations)
                    if (entry.Owner == owner && entry.Id == id)
                    { reason = "This owner already registered that ID."; return false; }
                if (!installed)
                {
                    try { install?.Invoke(); installed = true; }
                    catch (Exception ex) { reason = "Interception unavailable: " + ex.Message; return false; }
                }
                var next = new Registration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[next.Length - 1] = new Registration(owner, id, pre, post, order, accepted);
                Array.Sort(next, (a, b) => {
                    int comparison = a.Order.CompareTo(b.Order);
                    if (comparison == 0) comparison = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
                    return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Id, b.Id);
                });
                Volatile.Write(ref registrations, next);
            }
            reason = string.Empty; return true;
        }

        internal Invocation Begin(TPre args)
        {
            Registration[] snapshot = notificationDepth == 0 ? Volatile.Read(ref registrations) : Array.Empty<Registration>();
            var invocation = new Invocation(this, snapshot, args);
            notificationDepth++;
            try
            {
                for (int index = 0; index < snapshot.Length; index++)
                {
                    args.ResetState();
                    object before = args.Capture();
                    bool skip = args.Veto;
                    try { snapshot[index].Pre?.Invoke(args); invocation.States[index] = args.State; }
                    catch (Exception ex)
                    {
                        args.Restore(before); args.RestoreVeto(skip);
                        Interlocked.Increment(ref failures);
                        LogFailure(snapshot[index], "Pre", ex);
                    }
                }
            }
            finally { notificationDepth--; args.Freeze(); }
            return invocation;
        }

        private static void LogFailure(Registration registration, string phase, Exception error)
        {
            long count = Interlocked.Increment(ref registration.Failures);
            if ((count & (count - 1)) != 0) return;
            // Logging must never compromise the original or another observer.
            try { InterceptionDiagnostics.Report(registration.Owner + "/" + registration.Id + " " + phase, error); } catch { }
        }

        internal sealed class Invocation
        {
            private readonly InterceptionEvent<TPre, TPost> owner;
            private readonly Registration[] snapshot;
            private bool completed;
            private bool acceptedNotified;
            internal readonly object[] States;
            internal readonly TPre Pre;
            internal Invocation(InterceptionEvent<TPre, TPost> owner, Registration[] snapshot, TPre pre)
            { this.owner = owner; this.snapshot = snapshot; Pre = pre; States = new object[snapshot.Length]; }
            internal void NotifyAccepted()
            {
                if (acceptedNotified || completed || Pre.SkipOriginalFunction) return;
                acceptedNotified = true;
                notificationDepth++;
                try
                {
                    for (int index = 0; index < snapshot.Length; index++)
                    {
                        try { snapshot[index].Accepted?.Invoke(Pre, States[index]); }
                        catch (Exception ex) { Interlocked.Increment(ref owner.failures); LogFailure(snapshot[index], "Accepted", ex); }
                    }
                }
                finally { notificationDepth--; }
            }
            internal void Complete(Func<object, TPost> createPost)
            {
                if (completed) return;
                completed = true;
                notificationDepth++;
                try
                {
                    for (int index = 0; index < snapshot.Length; index++)
                    {
                        if (snapshot[index].Post == null) continue;
                        try { snapshot[index].Post(createPost(States[index])); }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref owner.failures);
                            LogFailure(snapshot[index], "Post", ex);
                        }
                    }
                }
                finally { notificationDepth--; }
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
            internal Registration(string owner, string id, Action<TPre> pre, Action<TPost> post, int order, Action<TPre, object> accepted)
            { Owner = owner; Id = id; Pre = pre; Post = post; Order = order; Accepted = accepted; }
        }
    }

    internal static class InterceptionDiagnostics
    {
        internal static Action<string, Exception> Error;
        internal static void Report(string registration, Exception error)
        { try { Error?.Invoke(registration, error); } catch { } }
    }
}
