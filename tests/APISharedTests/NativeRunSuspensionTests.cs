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
    [TestClass]
    public sealed class NativeRunSuspensionTests
    {
        private sealed class World
        {
            internal readonly object Engine = new object();
            internal object CurrentEngine;
            internal readonly WorkerProductionRunGate Gate;
            internal WorkerProductionRunGate PublishedGate;
            internal readonly EngineRunObserverRegistry Observers = new EngineRunObserverRegistry();
            internal long Generation = 7;
            internal bool Enabled = true, Admitted = true, Faulted, ReturnZero;
            internal int Calls, Begins, Ends, FaultObservations, GenerationSeen;
            internal bool OriginalOwnedMonitor = true;
            internal Exception OriginalFailure;
            private readonly Func<bool, int> vanilla;

            internal World()
            {
                CurrentEngine = Engine;
                vanilla = Vanilla;
                Assert.IsTrue(Observers.TryRegister("fixture", "pair", () => Begins++, () => Ends++, out _, 0));
                Gate = NewGate();
                PublishedGate = Gate;
            }
            internal WorkerProductionRunGate NewGate() => new WorkerProductionRunGate(Engine, ReadGeneration, () => Enabled,
                () => Faulted, () => Admitted, Original, () => FaultObservations++, () => CurrentEngine, () => PublishedGate);
            private long ReadGeneration()
            {
                if (Volatile.Read(ref GenerationSeen) == 0) Volatile.Write(ref GenerationSeen, 1);
                return Volatile.Read(ref Generation);
            }
            private int Original(bool skip) => Observers.Invoke(skip, vanilla);
            private int Vanilla(bool skip)
            {
                Calls++;
                OriginalOwnedMonitor &= Monitor.IsEntered(Engine);
                if (OriginalFailure != null) throw OriginalFailure;
                if (ReturnZero) return 0;
                return skip ? 73 : 41;
            }
            internal WorkerProductionRunGate.NativeRunSuspension Acquire()
            {
                Assert.IsTrue(Gate.TryBindNativeRunSuspension(out string binding), binding);
                Assert.IsTrue(Gate.TryAcquireNativeRunSuspension(1000, out var lease, out string reason), reason);
                return lease;
            }
        }

        [TestMethod]
        public void HoldRejectsBeforeOriginalAndDoesNotRestoreProductionState()
        {
            var world = new World();
            var lease = world.Acquire();
            Assert.AreEqual(0, world.Gate.Run(true));
            Assert.AreEqual(0, world.Calls); Assert.AreEqual(0, world.Begins); Assert.AreEqual(0, world.Ends);
            world.Generation++; world.Admitted = false;
            lease.Dispose();
            Assert.AreEqual(8L, world.Generation); Assert.IsFalse(world.Admitted);
            Assert.AreEqual(0, world.Gate.Run(true), "Disposal must not reopen independent production admission.");
            world.Admitted = true;
            Assert.AreEqual(73, world.Gate.Run(true)); Assert.AreEqual(1, world.Calls);
            Assert.IsTrue(world.OriginalOwnedMonitor);
        }

        [TestMethod]
        public void OverlapAndConcurrentDuplicateDisposalReleaseOnlyTheirOwnHolds()
        {
            var world = new World();
            var first = world.Acquire(); var second = world.Acquire();
            using (var start = new ManualResetEventSlim())
            {
                var a = new Thread(() => { start.Wait(5000); first.Dispose(); });
                var b = new Thread(() => { start.Wait(5000); first.Dispose(); });
                a.Start(); b.Start(); start.Set();
                Assert.IsTrue(a.Join(5000)); Assert.IsTrue(b.Join(5000));
            }
            Assert.IsTrue(first.IsDisposed); Assert.AreEqual(0, world.Gate.Run(false));
            second.Dispose(); Assert.AreEqual(41, world.Gate.Run(false));
            var later = world.Acquire(); first.Dispose(); second.Dispose();
            Assert.AreEqual(0, world.Gate.Run(false), "Released stale handles cannot clear a later hold.");
            later.Dispose(); Assert.AreEqual(41, world.Gate.Run(false));
        }

        [TestMethod]
        public void QueuedRunCannotBorrowAReleasedSuspensionEpoch()
        {
            var world = new World();
            world.Gate.Run(false); // Prepare the actual gate/delegate/JIT paths before contention.
            var lease = world.Acquire();
            world.GenerationSeen = 0;
            int result = -1; Exception failure = null;
            var worker = new Thread(() => { try { result = world.Gate.Run(true); } catch (Exception e) { failure = e; } });
            try
            {
                lock (world.Engine)
                {
                    worker.Start();
                    // Before the monitor, the worker only performs nonblocking generation/
                    // epoch reads. Its wait state plus the generation marker identifies this
                    // fixture's controlled monitor contention; no game thread is inspected.
                    Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref world.GenerationSeen) != 0 &&
                        (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 5000));
                    lease.Dispose(); // Same production generation, count returns to zero.
                }
            }
            finally
            {
                lease.Dispose();
                if (worker.IsAlive) Assert.IsTrue(worker.Join(5000));
            }
            Assert.IsTrue(worker.Join(5000)); Assert.IsNull(failure);
            Assert.AreEqual(0, result); Assert.AreEqual(1, world.Calls);
            Assert.AreEqual(73, world.Gate.Run(true), "A genuinely later run is admitted.");
        }

        [TestMethod]
        public void FiniteAndReentrantAcquisitionFailuresPublishNoHold()
        {
            var world = new World();
            Assert.IsTrue(world.Gate.TryBindNativeRunSuspension(out _));
            bool acquired = true; WorkerProductionRunGate.NativeRunSuspension lease = null;
            lock (world.Engine)
            {
                Assert.IsFalse(world.Gate.TryAcquireNativeRunSuspension(0, out _, out _));
                var waiter = new Thread(() => acquired = world.Gate.TryAcquireNativeRunSuspension(0, out lease, out _));
                waiter.Start(); Assert.IsTrue(waiter.Join(5000));
            }
            Assert.IsFalse(acquired); Assert.IsNull(lease);
            Assert.IsFalse(world.Gate.TryAcquireNativeRunSuspension(-1, out _, out _));
            Assert.AreEqual(41, world.Gate.Run(false));
        }

        [TestMethod]
        public void FaultObservationAndPublicPairingKeepTheirExistingOrdering()
        {
            var world = new World();
            var lease = world.Acquire(); world.Faulted = true;
            Assert.AreEqual(0, world.Gate.Run(false)); Assert.AreEqual(1, world.FaultObservations);
            Assert.AreEqual(0, world.Begins); Assert.AreEqual(0, world.Ends);
            lease.Dispose(); Assert.IsTrue(world.Faulted);
            world.Faulted = false;
            world.ReturnZero = true;
            Assert.AreEqual(0, world.Gate.Run(false));
            Assert.AreEqual(1, world.Begins); Assert.AreEqual(1, world.Ends);
            world.OriginalFailure = new InvalidOperationException("original");
            Assert.AreSame(world.OriginalFailure, Assert.ThrowsExactly<InvalidOperationException>(() => world.Gate.Run(false)));
            Assert.AreEqual(2, world.Begins); Assert.AreEqual(2, world.Ends);
            Assert.IsFalse(Monitor.IsEntered(world.Engine));
        }

        [TestMethod]
        public void BindingPreservesOnlyOptedInPublicationAndRejectsMonitorMismatch()
        {
            var world = new World();
            Assert.IsFalse(world.Gate.TryAcquireNativeRunSuspension(0, out _, out _));
            Assert.IsFalse(world.Gate.HasNativeRunSuspensionBinding);
            Assert.IsNull(WorkerProductionRunGate.RetainNativeRunSuspensionBinding(world.Gate, new object()),
                "Unbound initialization retains its previous replacement choice.");
            var lease = world.Acquire();
            Assert.AreSame(world.Gate, WorkerProductionRunGate.RetainNativeRunSuspensionBinding(world.Gate, world.Engine));
            lease.Dispose();
            Assert.AreSame(world.Gate, WorkerProductionRunGate.RetainNativeRunSuspensionBinding(world.Gate, world.Engine),
                "Process-lived binding, rather than transient count, owns publication.");
            Assert.Throws<InvalidOperationException>(() =>
                WorkerProductionRunGate.RetainNativeRunSuspensionBinding(world.Gate, new object()));
            world.CurrentEngine = new object();
            Assert.IsFalse(world.Gate.TryAcquireNativeRunSuspension(0, out _, out _));
        }

        [TestMethod]
        public void CapturedEarlierGateCannotBypassAReplacementSuspensionBinding()
        {
            var world = new World();
            var capturedOld = world.Gate; // The existing OnRun's already-read reference.
            var replacement = world.NewGate();
            world.PublishedGate = replacement;
            Assert.AreEqual(41, capturedOld.Run(false), "Unbound replacement keeps the previous default.");
            Assert.IsFalse(capturedOld.TryBindNativeRunSuspension(out _));
            Assert.IsTrue(replacement.TryBindNativeRunSuspension(out _));
            Assert.IsTrue(replacement.TryAcquireNativeRunSuspension(1000, out var lease, out string reason), reason);
            try
            {
                Assert.AreEqual(0, capturedOld.Run(false), "A stale count-zero gate cannot bypass the current owner's hold.");
                Assert.AreEqual(1, world.Calls);
            }
            finally { lease.Dispose(); }
            Assert.AreEqual(0, capturedOld.Run(false), "A bound publication also retires old captures after release.");
            Assert.AreEqual(41, replacement.Run(false));
        }

        [TestMethod]
        public void CompiledRunEntryHasNoAllocationOrReflectionInstructions()
        {
            var method = typeof(WorkerProductionRunGate).GetMethod(nameof(WorkerProductionRunGate.Run),
                BindingFlags.NonPublic | BindingFlags.Instance);
            using (var definition = new DynamicMethodDefinition(method))
            {
                var code = definition.Definition.Body.Instructions;
                Assert.IsFalse(code.Any(i => i.OpCode == OpCodes.Newobj || i.OpCode == OpCodes.Newarr ||
                    i.OpCode == OpCodes.Box || i.OpCode == OpCodes.Ldftn || i.OpCode == OpCodes.Ldvirtftn));
                Assert.IsFalse(code.Any(i => i.Operand is Mono.Cecil.MethodReference called &&
                    called.DeclaringType.FullName.StartsWith("System.Reflection.", StringComparison.Ordinal)));
            }
        }

        [TestMethod]
        public void WarmNormalDormantAndSuspendedRunsAllocateNothing()
        {
            var world = new World();
            for (int mode = 0; mode != 3; mode++)
            {
                world.Enabled = mode != 1;
                var lease = mode == 2 ? world.Acquire() : null;
                try
                {
                    for (int i = 0; i < 2000; i++) world.Gate.Run(false);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20000; i++) world.Gate.Run(false);
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    long started = Stopwatch.GetTimestamp();
                    for (int i = 0; i < 20000; i++) world.Gate.Run(false);
                    long ticks = Stopwatch.GetTimestamp() - started;
                    Console.WriteLine("nativeRunSuspension mode={0} allocated={1} timingTicks={2} frequency={3}",
                        mode, allocated, ticks, Stopwatch.Frequency);
                    Assert.AreEqual(0L, allocated);
                }
                finally { lease?.Dispose(); }
            }
        }
    }
}
