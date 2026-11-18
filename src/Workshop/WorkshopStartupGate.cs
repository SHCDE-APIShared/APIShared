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

}
