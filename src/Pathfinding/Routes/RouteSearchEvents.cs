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
        /// <summary>Whether any observer is registered. A publisher may test this first to avoid creating a context
        /// for a search nobody observes; <see cref="BeginSearch"/> returns null in the same case.</summary>
        public static bool HasObservers => Registry.HasObservers;

        /// <summary>Publishes one additional weighted route calculation to the registered observers.</summary>
        /// <remarks>
        /// For mods that run their own preference search beside Vanilla's pathing. Call synchronously on the thread
        /// that performs the calculation, immediately before it. All Pre callbacks run inside this call; afterwards
        /// <see cref="RouteSearchOperation.Pre"/> is frozen and holds the effective costs and skip decision. When it
        /// asks to skip, do not run the calculation and do not call Complete. Otherwise run it with the effective
        /// costs and call <see cref="RouteSearchOperation.Complete"/> exactly once, also after an ordinary failure.
        /// A publisher exception that ends the calculation needs no Complete. A search published from inside an
        /// observer callback is skipped without notification. Initial costs must lie in 1..MaximumEdgeCost.
        /// </remarks>
        /// <param name="context">Immutable identity of the operation; its SourceGuid is the publishing mod.</param>
        /// <param name="groundEdgeCost">The publisher's own preference cost of an eligible ground edge.</param>
        /// <param name="moatEdgeCost">The publisher's own preference cost of an eligible moat edge.</param>
        /// <returns>The operation, or null when no observer is registered: then calculate with the own costs.</returns>
        public static RouteSearchOperation BeginSearch(RouteSearchContext context, long groundEdgeCost, long moatEdgeCost)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (groundEdgeCost <= 0 || groundEdgeCost > context.MaximumEdgeCost)
                throw new ArgumentOutOfRangeException(nameof(groundEdgeCost));
            if (moatEdgeCost <= 0 || moatEdgeCost > context.MaximumEdgeCost)
                throw new ArgumentOutOfRangeException(nameof(moatEdgeCost));
            RouteSearchEventRegistry.Invocation invocation = Registry.Begin(context, groundEdgeCost, moatEdgeCost);
            return invocation == null ? null : new RouteSearchOperation(invocation);
        }
    }

    /// <summary>One published route calculation returned by <see cref="RouteSearchEvents.BeginSearch"/>.</summary>
    public sealed class RouteSearchOperation
    {
        private readonly RouteSearchEventRegistry.Invocation invocation;
        internal RouteSearchOperation(RouteSearchEventRegistry.Invocation invocation) { this.invocation = invocation; }
        /// <summary>The frozen Pre arguments: effective costs and whether the calculation must be skipped.</summary>
        public RouteSearchPreEventArgs Pre => invocation.Pre;
        /// <summary>Reports the executed calculation to Post observers. Ignored after the first call and for a skipped
        /// calculation. Runs the Post callbacks synchronously on the caller's thread; their failures are isolated.</summary>
        /// <param name="success">Whether the publisher produced a route that passed its own live-edge checks.</param>
        /// <param name="reason">The publisher's result description (diagnostic text, not a stable enum).</param>
        /// <param name="routeLength">Edges in the accepted route, or zero on failure.</param>
        /// <param name="expandedNodes">Search nodes expanded for this calculation.</param>
        public void Complete(bool success, string reason, int routeLength, int expandedNodes) =>
            invocation.Complete(success, reason, routeLength, expandedNodes);
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
            private readonly bool hasPostObservers;
            internal RouteSearchPreEventArgs Pre { get; }
            internal Invocation(RouteSearchEventRegistry owner, Registration[] snapshot, RouteSearchPreEventArgs pre)
            {
                this.owner = owner; this.snapshot = snapshot; Pre = pre;
                foreach (Registration entry in snapshot)
                    if (entry.Post != null) { hasPostObservers = true; break; }
            }
            internal void Complete(bool success, string reason, int routeLength, int expandedNodes)
            {
                if (completed || Pre.SkipOriginalFunction) return;
                completed = true;
                if (!hasPostObservers) return;
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
