using APIShared.Statistics;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace APISharedTests
{
    internal static class EngineReadbackTests
    {
        internal static void Run(Action<bool, string> check)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var type = typeof(EngineReadbacks);
            var initialize = (Action<object>)Delegate.CreateDelegate(typeof(Action<object>), type.GetMethod("Initialize", flags));
            var enter = (Action)Delegate.CreateDelegate(typeof(Action), type.GetMethod("EnterRun", flags));
            var exit = (Action)Delegate.CreateDelegate(typeof(Action), type.GetMethod("ExitRun", flags));
            var drain = (Action)Delegate.CreateDelegate(typeof(Action), type.GetMethod("BeforeBufferPublication", flags));
            object previous = type.GetField("gate", flags).GetValue(null), gate = new object();
            initialize(gate);
            var order = new List<int>(); string owner = "readback-fixture-" + Guid.NewGuid();
            check(EngineReadbacks.TryRegister(owner, "a", () => order.Add(1), out var a), "Register a prepared reader.");
            check(EngineReadbacks.TryRegister(owner, "b", () => order.Add(2), out var b), "Register an independent reader.");
            check(!EngineReadbacks.TryRegister(owner, "a", () => { }, out _), "Duplicate owner-local ID rejected.");
            try
            {
                a.Request(); a.Request(); b.Request();
                drain(); check(order.Count == 0, "An unrelated buffer call cannot run callbacks.");
                enter(); try { drain(); check(order.Count == 0, "Run without engine ownership cannot read."); }
                finally { exit(); }
                lock (gate) { drain(); check(order.Count == 0, "Engine ownership outside a run cannot read."); }
                var producer = new Thread(() => { lock (gate) { enter(); try { drain(); drain(); } finally { exit(); } } });
                producer.Start(); producer.Join();
                check(order.Count == 2 && order[0] == 1 && order[1] == 2, "Simulation thread drains ordered requests once.");
                a.Request(); a.Cancel();
                lock (gate) { enter(); try { drain(); } finally { exit(); } }
                check(order.Count == 2, "Cancellation prevents a queued callback.");
                EngineReadbackRegistration repeat = null; int calls = 0;
                EngineReadbacks.TryRegister(owner, "repeat", () => { if (++calls == 1) repeat.Request(); }, out repeat);
                repeat.Request();
                lock (gate) { enter(); try { drain(); check(calls == 1, "Reentrant request does not repeat in the same publication."); drain(); } finally { exit(); } }
                check(calls == 2, "Request made during callback survives to next publication.");
                EngineReadbacks.TryRegister(owner, "failure", () => { throw new InvalidOperationException("fixture"); }, out var failed);
                failed.Request(); b.Request();
                lock (gate) { enter(); try { drain(); } finally { exit(); } }
                check(order.Count == 3 && failed.TryTakeError(out var error) && error is InvalidOperationException && !failed.TryTakeError(out _), "Failure isolated and handed off once outside engine lock.");
                // Race cancellation with repeated requests: no false dormant count or lost final request.
                int observed = 0;
                EngineReadbacks.TryRegister(owner, "race", () => observed++, out var race);
                var cancel = new Thread(() => { for (int i = 0; i < 10000; i++) race.Cancel(); });
                cancel.Start(); for (int i = 0; i < 10000; i++) race.Request(); cancel.Join();
                race.Request(); lock (gate) { enter(); try { drain(); } finally { exit(); } }
                check(observed == 1, "Concurrent cancellation retains a subsequent request.");
                var pending = (bool)type.GetProperty("HasPending", flags).GetValue(null);
                check(!pending, "All queued work accounted for after drain.");
                var allocationMethod = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
                if (allocationMethod != null)
                {
                    var bytes = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), allocationMethod);
                    // Warm the exact production Request/drain path; callback and array are already prepared.
                    EngineReadbacks.TryRegister(owner, "allocation", () => { }, out var allocation);
                    lock (gate)
                    {
                        enter();
                        try
                        {
                            for (int i = 0; i < 100; i++) { allocation.Request(); drain(); }
                            long before = bytes();
                            for (int i = 0; i < 10000; i++) { allocation.Request(); drain(); }
                            check(bytes() == before, "Prepared broker and callback drain allocate no managed memory.");
                        }
                        finally { exit(); }
                    }
                }
            }
            finally { a.Cancel(); b.Cancel(); initialize(previous); }
        }
    }
}
