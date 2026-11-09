using System;
using System.Threading;
using APIShared.Economy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class WorkerProductionRunGateTests
    {
        private sealed class World : IDisposable
        {
            internal readonly object EngineGate = new object();
            internal readonly ManualResetEventSlim GenerationRead = new ManualResetEventSlim();
            internal long Generation = 1;
            internal int Enabled = 1, Faulted, Admitted = 1, Calls, Reservations, Observations;
            internal bool SignalGeneration, LastFrameSkip, OriginalOwnedMonitor = true;
            internal Action DuringOriginal;
            internal readonly WorkerProductionRunGate Boundary;
            internal World()
            {
                Boundary = new WorkerProductionRunGate(EngineGate, ReadGeneration,
                    ReadEnabled, ReadFaulted, ReadAdmitted, Original, ObserveFault);
            }
            private long ReadGeneration()
            {
                long value = Interlocked.Read(ref Generation);
                if (SignalGeneration) GenerationRead.Set();
                return value;
            }
            private bool ReadEnabled() => Volatile.Read(ref Enabled) != 0;
            private bool ReadFaulted() => Volatile.Read(ref Faulted) != 0;
            private bool ReadAdmitted() => Volatile.Read(ref Admitted) != 0;
            private int Original(bool mpFrameSkip)
            {
                // Reservation represents Vanilla's first buffer-acquisition side effect.
                Calls++; Reservations++; LastFrameSkip = mpFrameSkip;
                OriginalOwnedMonitor &= Monitor.IsEntered(EngineGate);
                DuringOriginal?.Invoke();
                return 73;
            }
            private void ObserveFault() { Observations++; }
            public void Dispose() => GenerationRead.Dispose();
        }

        [TestMethod]
        public void DormantRunWaitingOnEngineMonitorCannotEnterReplacementWorld()
        {
            using (var world = new World { Enabled = 0, SignalGeneration = true })
            {
                int result = -1; Exception error = null; bool captured;
                var worker = new Thread(() => {
                    try { result = world.Boundary.Run(true); }
                    catch (Exception failure) { error = failure; }
                });
                lock (world.EngineGate)
                {
                    worker.Start();
                    captured = world.GenerationRead.Wait(TimeSpan.FromSeconds(5));
                    // The real production transition is serialized by this same monitor.
                    Interlocked.Increment(ref world.Generation);
                    Volatile.Write(ref world.Admitted, 0); Volatile.Write(ref world.Enabled, 1);
                    Assert.AreEqual(0, world.Reservations, "No buffer may be reserved while the old run waits.");
                }
                Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)), "Waiting run must complete after the monitor is released.");
                Assert.IsTrue(captured, "The waiting run captured its dormant generation before admission changed.");
                Assert.IsNull(error); Assert.AreEqual(0, result);
                Assert.AreEqual(0, world.Calls); Assert.AreEqual(0, world.Reservations);
            }
        }

        [TestMethod]
        public void PendingAndFaultedWorldsNeverInvokeOriginalOrReserveBuffers()
        {
            using (var world = new World { Admitted = 0 })
            {
                Assert.AreEqual(0, world.Boundary.Run(false));
                Assert.AreEqual(0, world.Calls); Assert.AreEqual(0, world.Reservations); Assert.AreEqual(0, world.Observations);
                world.Faulted = 1;
                Assert.AreEqual(0, world.Boundary.Run(true));
                Assert.AreEqual(0, world.Calls); Assert.AreEqual(0, world.Reservations); Assert.AreEqual(1, world.Observations);
            }
        }

        [TestMethod]
        public void AdmittedAndDormantRunsCallOriginalExactlyOnceWithUnchangedArguments()
        {
            using (var world = new World())
            {
                Assert.AreEqual(73, world.Boundary.Run(true));
                Assert.AreEqual(1, world.Calls); Assert.IsTrue(world.LastFrameSkip); Assert.IsTrue(world.OriginalOwnedMonitor);
                world.Enabled = 0; world.Admitted = 0;
                Assert.AreEqual(73, world.Boundary.Run(false), "Dormant Vanilla execution does not require production admission.");
                Assert.AreEqual(2, world.Calls); Assert.AreEqual(2, world.Reservations);
                Assert.IsFalse(world.LastFrameSkip); Assert.IsTrue(world.OriginalOwnedMonitor);
            }
        }

        [TestMethod]
        public void NativeFaultAndReentrantEpochChangeSuppressOriginalResult()
        {
            using (var world = new World())
            {
                world.DuringOriginal = () => world.Faulted = 1;
                Assert.AreEqual(0, world.Boundary.Run(false)); Assert.AreEqual(1, world.Calls);
                world.Faulted = 0; world.DuringOriginal = () => Interlocked.Increment(ref world.Generation);
                Assert.AreEqual(0, world.Boundary.Run(true)); Assert.AreEqual(2, world.Calls);
                world.Enabled = 0;
                Assert.AreEqual(0, world.Boundary.Run(false), "An epoch change also invalidates an originally dormant result.");
                Assert.AreEqual(3, world.Calls); Assert.IsTrue(world.OriginalOwnedMonitor);
            }
        }

        [TestMethod]
        public void OriginalExceptionsPropagateAndReleaseTheEngineMonitor()
        {
            using (var world = new World())
            {
                world.DuringOriginal = () => throw new InvalidOperationException("original failure");
                Assert.Throws<InvalidOperationException>(() => world.Boundary.Run(false));
                Assert.IsFalse(Monitor.IsEntered(world.EngineGate)); Assert.AreEqual(1, world.Calls);
                world.DuringOriginal = null;
                Assert.AreEqual(73, world.Boundary.Run(true)); Assert.AreEqual(2, world.Calls);
            }
        }

        [TestMethod]
        public void RepeatedNormalDormantAndPendingRunsAllocateNothing()
        {
            using (var world = new World())
            {
                for (int mode = 0; mode != 3; mode++)
                {
                    world.Enabled = mode == 1 ? 0 : 1; world.Admitted = mode == 2 ? 0 : 1;
                    for (int i = 0; i < 2000; i++) world.Boundary.Run(false);
                    int calls = world.Calls;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20000; i++) world.Boundary.Run(false);
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.AreEqual(0L, allocated, "The actual run boundary must not allocate per run.");
                    Assert.AreEqual(mode == 2 ? calls : calls + 20000, world.Calls);
                }
            }
        }
    }
}
