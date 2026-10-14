using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace APIShared.Workshop
{
    // The Steam initialization publisher must not return before the decision.
    // Pump only our continuations, never Unity's unrelated startup callbacks.
    internal sealed class WorkshopStartupGate : SynchronizationContext
    {
        private readonly BlockingCollection<Action> callbacks = new BlockingCollection<Action>();
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        private int active = 1;
        internal bool IsOpenOnOwnerThread => Volatile.Read(ref active) != 0 &&
            Thread.CurrentThread.ManagedThreadId == ownerThread && ReferenceEquals(Current, this);

        // Called only after explicit consent in the blocking startup workflow.
        // The returned authority expires even if an async callback retains it.
        internal static WorkshopStartupAuthorization AuthorizeConfirmedUpdate()
        {
            var gate = Current as WorkshopStartupGate;
            if (gate == null || !gate.IsOpenOnOwnerThread)
                throw WorkshopDiagnostics.Blocked("Automatic Workshop startup scope is no longer active.");
            return new WorkshopStartupAuthorization(gate);
        }
        public override void Post(SendOrPostCallback callback, object state) => callbacks.Add(() => callback(state));

        internal static void Run(Func<Task> work)
        {
            var previous = Current;
            var gate = new WorkshopStartupGate();
            SetSynchronizationContext(gate);
            try {
                var task = work();
                while (!task.IsCompleted) {
                    if (gate.callbacks.TryTake(out var callback, 50)) callback();
                }
                task.GetAwaiter().GetResult();
            } finally {
                Volatile.Write(ref gate.active, 0);
                SetSynchronizationContext(previous);
                gate.callbacks.Dispose();
            }
        }
    }

    // Internal startup-only authority. Public consumer workflows do not use it.
    internal sealed class WorkshopStartupAuthorization
    {
        private readonly WorkshopStartupGate gate;
        private string transaction;
        private bool consumed;
        internal WorkshopStartupAuthorization(WorkshopStartupGate gate) { this.gate = gate; }
        internal void Validate()
        {
            if (!gate.IsOpenOnOwnerThread || consumed)
                throw WorkshopDiagnostics.Blocked("Automatic Workshop startup consent expired; prepare and confirm again.");
        }
        internal void ConfirmHandoff(string directory)
        {
            Validate();
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A confirmed transaction is required.", nameof(directory));
            if (transaction != null) throw new InvalidOperationException("Startup consent already has a handoff.");
            transaction = directory;
        }
        internal void Quit(Action<string> log, Action quit, string reason)
        {
            Validate();
            if (transaction == null) throw new InvalidOperationException("No updater handoff was confirmed; keeping the game running.");
            if (log == null || quit == null || string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Quit requires logging, an action and a reason.");
            consumed = true;
            log("Workshop quit reason=" + reason + "; transaction=" + transaction + "; handoff=confirmed.");
            quit();
        }
    }
}
