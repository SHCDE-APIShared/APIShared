using APIShared;
using APIShared.Internal;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class UiWorkTests
    {
        private sealed class Context : SynchronizationContext
        {
            internal readonly Queue<Action> Queue = new Queue<Action>();
            public override void Post(SendOrPostCallback callback, object state) => Queue.Enqueue(() => callback(state));
            internal void Drain() { while (Queue.Count != 0) Queue.Dequeue()(); }
        }

        [TestMethod]
        public void PublicClientSharesDeferredFifoWithLegacyNetworkRoute()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var contextField = typeof(UnityMainThreadDispatch).GetField("unityContext", flags);
            var threadField = typeof(UnityMainThreadDispatch).GetField("unityThreadId", flags);
            var scheduledField = typeof(UnityMainThreadDispatch).GetField("dispatchScheduled", flags);
            var schedulerField = typeof(UiWorkRuntime).GetField("scheduler", flags);
            object contextBefore = contextField.GetValue(null), threadBefore = threadField.GetValue(null),
                scheduledBefore = scheduledField.GetValue(null), schedulerBefore = schedulerField.GetValue(null);
            var context = new Context();
            try
            {
                contextField.SetValue(null, null); threadField.SetValue(null, 0); scheduledField.SetValue(null, 0);
                schedulerField.SetValue(null, null);
                var client = ApiShared.ForMod("independent-ui-fixture");
                Assert.IsFalse(client.Ui.TryPost(() => { }));
                Assert.IsFalse(client.Ui.TryRegisterBeforeRender("refresh", () => { }, out _, out _));
                contextField.SetValue(null, context); threadField.SetValue(null, Thread.CurrentThread.ManagedThreadId);
                var calls = new List<int>();
                Assert.IsTrue(client.Ui.TryPost(() => { calls.Add(1); client.Ui.TryPost(() => calls.Add(4)); }));
                Assert.IsTrue(client.Network.TryPostPresentation(() => calls.Add(2)));
                Assert.IsTrue(client.Ui.TryPost(() => { throw new Exception("isolated"); }));
                Assert.IsTrue(client.Ui.TryPost(() => calls.Add(3)));
                Assert.AreEqual(0, calls.Count, "Even Unity callers must be deferred.");
                Assert.AreEqual(1, context.Queue.Count, "The existing dispatcher coalesces its drain posts.");
                context.Drain(); CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, calls);
                var scheduler = new UiWorkScheduler(() => 0, 1000, null);
                schedulerField.SetValue(null, scheduler);
                int refreshes = 0;
                Assert.IsTrue(client.Ui.TryRegisterBeforeRender("refresh", () => refreshes++, out var work, out _));
                work.Request(); work.Request(); scheduler.Pump(0); Assert.AreEqual(1, refreshes);
            }
            finally
            {
                context.Drain(); contextField.SetValue(null, contextBefore); threadField.SetValue(null, threadBefore);
                scheduledField.SetValue(null, scheduledBefore); schedulerField.SetValue(null, schedulerBefore);
            }
        }
    }
}
