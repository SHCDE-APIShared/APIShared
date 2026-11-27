using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using APIShared.Economy;
using APIShared.Simulation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace APISharedTests
{
    // The fake original is the complete old null-boundary call chain, including
    // its finally/tail. Threads and monitors here are fixture-owned, never game code.
    [TestClass]
    public sealed class NativeRunPublicationTests
    {
        private sealed class World
        {
            internal readonly object Engine = new object();
            internal readonly WorkerProductionRunPublication Publication;
            private readonly EngineRunObserverRegistry observers = new EngineRunObserverRegistry();
            private readonly Func<bool, int> fallbackBody;
            internal int Created, Published, FallbackCalls, NativeCalls, Begins, Ends, Tails;
            internal bool ReturnZero;
            internal Exception Failure;
            internal Func<bool, int> FallbackBody;
            internal Func<object> ReadEngine;
            internal Action BeginAction, EndAction, FallbackTail, Creating;

            internal World()
            {
                fallbackBody = FallbackCore;
                Assert.IsTrue(observers.TryRegister("fixture", "null-run-pair", Begin, End, out _, 0));
                Publication = new WorkerProductionRunPublication(Create, Fallback);
            }

            private WorkerProductionRunGate Create(object monitor)
            {
                Interlocked.Increment(ref Created);
                Creating?.Invoke();
                return new WorkerProductionRunGate(monitor, () => 7, () => true, () => false,
                    () => true, Native, () => { }, () => ReadEngine == null ? Engine : ReadEngine(),
                    () => Publication.Current);
            }

            private int Native(bool skip)
            {
                Assert.IsTrue(Monitor.IsEntered(Engine));
                Interlocked.Increment(ref NativeCalls);
                return skip ? 73 : 41;
            }

            private int Fallback(bool skip)
            {
                Interlocked.Increment(ref FallbackCalls);
                try { return observers.Invoke(skip, fallbackBody); }
                finally
                {
                    try { FallbackTail?.Invoke(); }
                    finally { Interlocked.Increment(ref Tails); }
                }
            }

            private int FallbackCore(bool skip)
            {
                if (FallbackBody != null) return FallbackBody(skip);
                if (Failure != null) throw Failure;
                return ReturnZero ? 0 : skip ? 73 : 41;
            }

            private void Begin()
            {
                Interlocked.Increment(ref Begins);
                BeginAction?.Invoke();
            }

            private void End()
            {
                Interlocked.Increment(ref Ends);
                EndAction?.Invoke();
            }

            internal WorkerProductionRunGate Initialize() =>
                Publication.Initialize(Engine, () => Interlocked.Increment(ref Published));
        }

        private sealed class Call
        {
            private readonly Func<int> action;
            internal readonly Thread Thread;
            internal int Result = -1;
            internal Exception Failure;

            internal Call(Func<int> action)
            {
                this.action = action;
                Thread = new Thread(Execute) { IsBackground = true };
            }

            private void Execute()
            {
                try { Result = action(); }
                catch (Exception failure) { Failure = failure; }
            }

            internal void Join()
            {
                Assert.IsTrue(Thread.Join(5000), "The fixture call did not finish within its bounded wait.");
            }
        }

        private static void WaitForDrain(Call binder)
        {
            // With an old fallback held and no contended engine/publication lock,
            // this thread's only blocking operation is the helper's drain wait.
            Assert.IsTrue(SpinWait.SpinUntil(() => !binder.Thread.IsAlive ||
                (binder.Thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 5000));
            Assert.IsTrue(binder.Thread.IsAlive, "Binding must wait for the complete counted original.");
            Assert.IsNull(binder.Failure);
        }

        [TestMethod]
        public void NullOriginalKeepsZeroExceptionIdentityAndCompleteFinally()
        {
            var world = new World();
            Assert.AreEqual(73, world.Publication.RunFallback(true));
            world.ReturnZero = true;
            Assert.AreEqual(0, world.Publication.RunFallback(false));
            var failure = new InvalidOperationException("original identity");
            world.ReturnZero = false; world.Failure = failure;
            Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() =>
                world.Publication.RunFallback(false)));
            Assert.AreEqual(3, world.FallbackCalls);
            Assert.AreEqual(3, world.Begins); Assert.AreEqual(3, world.Ends); Assert.AreEqual(3, world.Tails);
            world.Initialize();
            Assert.IsTrue(world.Publication.TryBind(1000, world.Engine, out string reason), reason);
            Assert.AreEqual(41, world.Publication.RunFallback(false),
                "An exception must release admission debt before a later successful binding.");
            Assert.AreEqual(1, world.NativeCalls);
        }

        [TestMethod]
        public void LateNullCaptureUsesCurrentGateAndCannotBypassItsHold()
        {
            var world = new World();
            var gate = world.Initialize();
            Assert.IsTrue(world.Publication.TryBind(1000, world.Engine, out string reason), reason);
            Assert.IsTrue(world.Publication.TryAcquire(1000, world.Engine, out var lease, out reason), reason);
            try
            {
                Assert.AreEqual(0, world.Publication.RunFallback(true));
                Assert.AreEqual(0, world.NativeCalls); Assert.AreEqual(0, world.FallbackCalls);
                Assert.AreSame(gate, world.Initialize(), "A bound gate is process-lived, including during a hold.");
            }
            finally { lease.Dispose(); }
            Assert.AreEqual(73, world.Publication.RunFallback(true));
            Assert.AreEqual(1, world.NativeCalls); Assert.AreEqual(0, world.FallbackCalls);
            Assert.AreSame(gate, world.Initialize(), "Release does not reopen replacement or null admission.");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ClosingDrainsCompleteTailAndReservesGateAgainstInitializationAndCompetingBind(bool tailThrows)
        {
            var world = new World();
            var tailFailure = new InvalidOperationException("tail identity");
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                world.FallbackTail = () =>
                {
                    entered.Set();
                    if (!release.Wait(5000)) throw new TimeoutException("fixture tail release");
                    if (tailThrows) throw tailFailure;
                };
                var oldNull = new Call(() => world.Publication.RunFallback(false));
                var binder = new Call(() => world.Publication.TryBind(4000, world.Engine, out _) ? 1 : 0);
                oldNull.Thread.Start();
                try
                {
                    Assert.IsTrue(entered.Wait(5000));
                    var gate = world.Initialize();
                    binder.Thread.Start(); WaitForDrain(binder);
                    Assert.IsFalse(gate.HasNativeRunSuspensionBinding);
                    Assert.IsFalse(world.Publication.TryBind(0, world.Engine, out _),
                        "A competing attempt cannot wait forever or cancel the first closing owner.");
                    Assert.AreSame(gate, world.Initialize());
                    Assert.AreEqual(1, world.Created, "Closing reserves the exact candidate before the drain releases its lock.");
                    Assert.AreEqual(73, world.Publication.RunFallback(true),
                        "A late null capture must route through the monitor-owning gate while the older tail drains.");
                    Assert.AreEqual(1, world.FallbackCalls); Assert.AreEqual(1, world.NativeCalls);
                }
                finally
                {
                    release.Set(); oldNull.Join();
                    if ((binder.Thread.ThreadState & System.Threading.ThreadState.Unstarted) == 0) binder.Join();
                }
                Assert.AreSame(tailThrows ? tailFailure : null, oldNull.Failure);
                Assert.IsNull(binder.Failure); Assert.AreEqual(1, binder.Result);
                Assert.AreEqual(1, world.Ends); Assert.AreEqual(1, world.Tails);
                Assert.IsTrue(world.Publication.Current.HasNativeRunSuspensionBinding);
                Assert.IsTrue(world.Publication.TryBind(1000, world.Engine, out string reason), reason);
            }
        }

        [TestMethod]
        public void FailedFirstDrainReopensNullAdmissionAndUnboundReplacementBeforeRetry()
        {
            var world = new World();
            int bodyCalls = 0;
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                world.FallbackBody = skip =>
                {
                    if (Interlocked.Increment(ref bodyCalls) == 1)
                    {
                        entered.Set();
                        if (!release.Wait(5000)) throw new TimeoutException("fixture old original release");
                    }
                    return skip ? 73 : 41;
                };
                var oldNull = new Call(() => world.Publication.RunFallback(false));
                oldNull.Thread.Start();
                try
                {
                    Assert.IsTrue(entered.Wait(5000));
                    var first = world.Initialize();
                    Assert.IsFalse(world.Publication.TryBind(0, world.Engine, out _));
                    Assert.IsFalse(first.HasNativeRunSuspensionBinding);
                    Assert.IsFalse(world.Publication.TryAcquire(0, world.Engine, out var failedLease, out _));
                    Assert.IsNull(failedLease, "Acquisition cannot bypass an unsuccessful bind.");
                    Assert.AreEqual(73, world.Publication.RunFallback(true));
                    Assert.AreEqual(2, world.FallbackCalls); Assert.AreEqual(0, world.NativeCalls);
                    Assert.AreNotSame(first, world.Initialize(), "A failed first attempt must release the reserved unbound publication.");
                }
                finally { release.Set(); oldNull.Join(); }
                Assert.IsNull(oldNull.Failure);
                Assert.IsTrue(world.Publication.TryBind(1000, world.Engine, out string reason), reason);
                Assert.IsTrue(world.Publication.TryAcquire(1000, world.Engine, out var lease, out reason), reason);
                lease.Dispose();
                Assert.AreEqual(41, world.Publication.RunFallback(false));
                Assert.AreEqual(1, world.NativeCalls);
            }
        }

        [TestMethod]
        public void SelfDrainAndEngineReentryAreRejectedThroughoutTheCompleteFallback()
        {
            var world = new World();
            world.Initialize();
            int beginRejected = 0, bodyRejected = 0, endRejected = 0, tailRejected = 0;
            world.BeginAction = () =>
            {
                if (!world.Publication.TryBind(0, world.Engine, out _) &&
                    !world.Publication.TryAcquire(0, world.Engine, out _, out _)) beginRejected++;
            };
            world.FallbackBody = skip =>
            {
                if (!world.Publication.TryBind(0, world.Engine, out _) &&
                    !world.Publication.TryAcquire(0, world.Engine, out _, out _)) bodyRejected++;
                return 41;
            };
            world.EndAction = () =>
            {
                if (!world.Publication.TryBind(0, world.Engine, out _) &&
                    !world.Publication.TryAcquire(0, world.Engine, out _, out _)) endRejected++;
            };
            world.FallbackTail = () =>
            {
                if (!world.Publication.TryBind(0, world.Engine, out _) &&
                    !world.Publication.TryAcquire(0, world.Engine, out _, out _)) tailRejected++;
            };
            Assert.AreEqual(41, world.Publication.RunFallback(false));
            Assert.AreEqual(1, beginRejected); Assert.AreEqual(1, bodyRejected);
            Assert.AreEqual(1, endRejected); Assert.AreEqual(1, tailRejected);
            lock (world.Engine)
            {
                Assert.IsFalse(world.Publication.TryBind(0, world.Engine, out _));
                Assert.IsFalse(world.Publication.TryAcquire(0, world.Engine, out _, out _));
            }
            Assert.IsFalse(world.Publication.TryBind(-1, world.Engine, out _));
            Assert.IsFalse(world.Publication.TryAcquire(-1, world.Engine, out _, out _));
            Assert.IsTrue(world.Publication.TryBind(1000, world.Engine, out string reason), reason);
        }

        [TestMethod]
        public void PositiveBudgetExpiryAfterTrustedReadKeepsAdmissionClosedButNotReadyUntilRetry()
        {
            var world = new World();
            var gate = world.Initialize();
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                // Production's identity read is fixed and nonblocking. Holding the
                // fixture read demonstrates the deliberately weaker timeout claim:
                // it cannot interrupt a trusted read, but must not publish late
                // readiness after that read has already marked the gate bound.
                world.ReadEngine = () =>
                {
                    entered.Set();
                    if (!release.Wait(5000)) throw new TimeoutException("fixture identity read release");
                    return world.Engine;
                };
                var binder = new Call(() => world.Publication.TryBind(500, world.Engine, out _) ? 1 : 0);
                binder.Thread.Start();
                try
                {
                    Assert.IsTrue(entered.Wait(5000));
                    Thread.Sleep(600); // Expire the positive budget inside the controlled trusted read.
                }
                finally { release.Set(); binder.Join(); }
                Assert.IsNull(binder.Failure); Assert.AreEqual(0, binder.Result);
            }
            world.ReadEngine = null;
            Assert.IsTrue(gate.HasNativeRunSuspensionBinding);
            Assert.AreSame(gate, world.Initialize(), "A late gate mark must retain the exact closed owner.");
            Assert.IsFalse(world.Publication.TryAcquire(1000, world.Engine, out var failedLease, out _));
            Assert.IsNull(failedLease, "A marked gate is insufficient until bounded readiness publication succeeds.");
            Assert.AreEqual(41, world.Publication.RunFallback(false));
            Assert.AreEqual(0, world.FallbackCalls, "Late failure must never reopen the old NULL Original.");
            Assert.AreEqual(1, world.NativeCalls);
            Assert.IsTrue(world.Publication.TryBind(0, world.Engine, out string reason), reason);
            Assert.IsTrue(world.Publication.TryAcquire(0, world.Engine, out var lease, out reason), reason);
            lease.Dispose();
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void WaitingForNativeMonitorDoesNotRetainPublicationMonitor(bool acquiring)
        {
            var world = new World();
            var gate = world.Initialize();
            Assert.IsTrue(world.Publication.TryBind(1000, world.Engine, out string reason), reason);
            WorkerProductionRunGate.NativeRunSuspension lease = null;
            var native = new Call(() => acquiring ?
                (world.Publication.TryAcquire(4000, world.Engine, out lease, out _) ? 1 : 0) :
                world.Publication.RunFallback(false));
            var initializer = new Call(() => ReferenceEquals(gate, world.Initialize()) ? 1 : 0);
            try
            {
                lock (world.Engine)
                {
                    native.Thread.Start();
                    Assert.IsTrue(SpinWait.SpinUntil(() => !native.Thread.IsAlive ||
                        (native.Thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 5000));
                    Assert.IsTrue(native.Thread.IsAlive);
                    initializer.Thread.Start();
                    Assert.IsTrue(initializer.Thread.Join(1000),
                        "A native monitor wait must not block cold publication behind the private publication monitor.");
                }
            }
            finally
            {
                if ((native.Thread.ThreadState & System.Threading.ThreadState.Unstarted) == 0) native.Join();
                if ((initializer.Thread.ThreadState & System.Threading.ThreadState.Unstarted) == 0) initializer.Join();
                lease?.Dispose();
            }
            Assert.IsNull(native.Failure); Assert.IsNull(initializer.Failure);
            Assert.AreEqual(acquiring ? 1 : 41, native.Result);
            Assert.AreEqual(1, initializer.Result);
        }

        [TestMethod]
        public void PublicationLockContendersUseFiniteBudgetAndFailedColdCallbackDoesNotPublish()
        {
            var world = new World();
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                world.Creating = () =>
                {
                    entered.Set();
                    if (!release.Wait(5000)) throw new TimeoutException("fixture factory release");
                };
                var initializer = new Call(() => { world.Initialize(); return 1; });
                initializer.Thread.Start();
                try
                {
                    Assert.IsTrue(entered.Wait(5000));
                    Assert.IsFalse(world.Publication.TryBind(0, world.Engine, out _));
                    Assert.IsFalse(world.Publication.TryAcquire(0, world.Engine, out var lease, out _));
                    Assert.IsNull(lease);
                }
                finally { release.Set(); initializer.Join(); }
                Assert.IsNull(initializer.Failure); Assert.AreEqual(1, initializer.Result);
            }
            var first = world.Publication.Current;
            var failure = new InvalidOperationException("cold callback identity");
            world.Creating = null;
            Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() =>
                world.Publication.Initialize(world.Engine, () =>
                {
                    Assert.AreSame(first, world.Publication.Current);
                    Assert.AreEqual(2, world.Created, "The candidate must already exist before the fixed cold callback.");
                    throw failure;
                })));
            Assert.AreSame(first, world.Publication.Current, "A failed callback cannot publish a partially prepared replacement.");
            Assert.AreNotSame(first, world.Initialize());
        }

        [TestMethod]
        public void WarmNullFallbackHasNoPerCallAllocationsOrClosureFactories()
        {
            var world = new World();
            for (int i = 0; i < 2000; i++) world.Publication.RunFallback(false);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++) world.Publication.RunFallback(false);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated);
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < 20000; i++) world.Publication.RunFallback(false);
            long ticks = Stopwatch.GetTimestamp() - started;
            Console.WriteLine("nativeRunPublication nullFallback allocated={0} timingTicks={1} frequency={2}",
                allocated, ticks, Stopwatch.Frequency);

            var method = typeof(WorkerProductionRunPublication).GetMethod(nameof(WorkerProductionRunPublication.RunFallback),
                BindingFlags.Instance | BindingFlags.NonPublic);
            using (var definition = new DynamicMethodDefinition(method))
            {
                var code = definition.Definition.Body.Instructions;
                Assert.IsFalse(code.Any(i => i.OpCode == OpCodes.Newarr || i.OpCode == OpCodes.Box ||
                    i.OpCode == OpCodes.Ldftn || i.OpCode == OpCodes.Ldvirtftn));
                // A rare invariant exception may have a cold constructor. Closure,
                // delegate or context allocation would also affect the entry IL.
                Assert.IsFalse(code.Any(i => i.OpCode == OpCodes.Newobj &&
                    i.Operand is Mono.Cecil.MethodReference created &&
                    created.DeclaringType.FullName != typeof(InvalidOperationException).FullName));
                Assert.IsFalse(code.Any(i => i.Operand is Mono.Cecil.MethodReference called &&
                    called.DeclaringType.FullName.StartsWith("System.Reflection.", StringComparison.Ordinal)));
            }
        }
    }
}
