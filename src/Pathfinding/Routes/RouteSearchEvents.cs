using System;
using System.Threading;

namespace APIShared.Pathfinding
{
    /// <summary>Process-owned Pre/Post events for participating mods' additional weighted route calculations.</summary>
    public static class RouteSearchEvents
    {
        internal static readonly RouteSearchEventRegistry Registry = new RouteSearchEventRegistry();
        /// <summary>Registers callbacks for the process lifetime, ordered by exact owner GUID then registration ID. Runs synchronously on the search caller's thread, outside the registry lock, without readiness requirements, replay or thread dispatch. Callbacks must not issue game commands or mutate the world. Invalid or throwing Pre contributions are rolled back; callback failures are isolated. Post follows executed calculations, including an ordinary failure, but not skipped calculations or thrown publisher exceptions. A later valid Pre may change earlier costs or skip decisions. Registrations added during notification join the next operation.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Action<RouteSearchPreEventArgs> pre, Action<RouteSearchPostEventArgs> post, out string reason) =>
            Registry.TryRegister(ownerGuid, registrationId, pre, post, out reason);
        /// <summary>Total isolated callback failures and invalid cost contributions; not a gameplay failure count.</summary>
        public static long CallbackFailures => Registry.CallbackFailures;
    }

    internal sealed class RouteSearchEventRegistry
    {
        private readonly object sync = new object();
        private Registration[] registrations = Array.Empty<Registration>();
        private long failures;
        [ThreadStatic] private static int notificationDepth;
        internal long CallbackFailures => Interlocked.Read(ref failures);
        internal bool HasObservers => Volatile.Read(ref registrations).Length != 0;

        internal bool TryRegister(string owner, string id, Action<RouteSearchPreEventArgs> pre,
            Action<RouteSearchPostEventArgs> post, out string reason)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || (pre == null && post == null))
            { reason = "Owner GUID, registration ID and at least one callback are required."; return false; }
            lock (sync)
            {
                foreach (Registration existing in registrations)
                    if (existing.Owner == owner && existing.Id == id)
                    { reason = "This owner already registered that ID."; return false; }
                var next = new Registration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[next.Length - 1] = new Registration(owner, id, pre, post);
                Array.Sort(next, (a, b) => {
                    int comparison = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
                    return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Id, b.Id);
                });
                Volatile.Write(ref registrations, next);
            }
            reason = string.Empty; return true;
        }

        internal Invocation Begin(RouteSearchContext context, long groundCost, long moatCost)
        {
            Registration[] snapshot = Volatile.Read(ref registrations);
            if (snapshot.Length == 0) return null;
            var args = new RouteSearchPreEventArgs(context, groundCost, moatCost);
            var invocation = new Invocation(this, snapshot, args);
            if (notificationDepth != 0)
            { args.SkipOriginalFunction = true; args.Freeze(); return invocation; }
            notificationDepth++;
            try
            {
                foreach (Registration registration in snapshot)
                {
                    if (registration.Pre == null) continue;
                    long previousGround = args.GroundEdgeCost, previousMoat = args.MoatEdgeCost;
                    bool previousSkip = args.SkipOriginalFunction;
                    try
                    {
                        registration.Pre(args);
                        if (args.GroundEdgeCost <= 0 || args.MoatEdgeCost <= 0 ||
                            args.GroundEdgeCost > context.MaximumEdgeCost || args.MoatEdgeCost > context.MaximumEdgeCost)
                            throw new ArgumentOutOfRangeException("route preference cost");
                    }
                    catch
                    {
                        args.GroundEdgeCost = previousGround; args.MoatEdgeCost = previousMoat;
                        args.SkipOriginalFunction = previousSkip;
                        Interlocked.Increment(ref failures);
                    }
                }
            }
            finally { notificationDepth--; args.Freeze(); }
            return invocation;
        }

        internal sealed class Invocation
        {
            private readonly RouteSearchEventRegistry owner;
            private readonly Registration[] snapshot;
            private bool completed;
            internal RouteSearchPreEventArgs Pre { get; }
            internal Invocation(RouteSearchEventRegistry owner, Registration[] snapshot, RouteSearchPreEventArgs pre)
            { this.owner = owner; this.snapshot = snapshot; Pre = pre; }
            internal void Complete(bool success, string reason, int routeLength, int expandedNodes)
            {
                if (completed || Pre.SkipOriginalFunction) return;
                completed = true;
                var args = new RouteSearchPostEventArgs(Pre.Context, Pre.GroundEdgeCost, Pre.MoatEdgeCost,
                    success, reason, routeLength, expandedNodes);
                notificationDepth++;
                try
                {
                    foreach (Registration registration in snapshot)
                        try { registration.Post?.Invoke(args); }
                        catch { Interlocked.Increment(ref owner.failures); }
                }
                finally { notificationDepth--; }
            }
        }
        internal sealed class Registration
        {
            internal readonly string Owner, Id;
            internal readonly Action<RouteSearchPreEventArgs> Pre;
            internal readonly Action<RouteSearchPostEventArgs> Post;
            internal Registration(string owner, string id, Action<RouteSearchPreEventArgs> pre, Action<RouteSearchPostEventArgs> post)
            { Owner = owner; Id = id; Pre = pre; Post = post; }
        }
    }
}
