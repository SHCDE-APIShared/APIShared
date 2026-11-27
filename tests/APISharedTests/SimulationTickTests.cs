using System;
using System.Collections.Generic;
using System.Linq;
using APIShared;
using APIShared.GameModes;
using APIShared.Simulation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API.Components.Timer;

namespace APISharedTests
{
    // Production reader/registry and the installed SE frame provider execute unchanged.
    // Only native memory, mission identity and source availability are external doubles.
    [TestClass, DoNotParallelize]
    public sealed class SimulationTickTests
    {
        private static MissionContext Mission(long id) => new MissionContext(id,
            MissionStartKind.NewGame, default(GameModeSnapshot), null, "tick fixture", false);

        [TestMethod]
        public void NativeClockTracksNestedLoadsBeforeCallbacksAndRejectsStaleOrRacingReads()
        {
            var lifecycle = new MissionLifecycleState(_ => { });
            long id = lifecycle.Begin(Mission(0));
            MissionContext expected = lifecycle.Context;
            int reads = 0;
            var clock = new MissionSimulationClockReader(() => lifecycle.NativeClock, () => { reads++; return 0; }, () => true);
            Assert.IsFalse(clock.TryRead(expected, out _));
            lifecycle.BeginNativeLoad(id);
            lifecycle.BeginNativeLoad(id);
            lifecycle.CompleteNativeLoad(id, true);
            Assert.IsFalse(clock.TryRead(expected, out _));
            lifecycle.CompleteNativeLoad(id, true);
            bool observed = false;
            lifecycle.Register("test", "clock", e => {
                if (e.Phase == MissionInitializationPhase.NativeLoaded)
                    observed = clock.TryRead(e.Context, out int tick) && tick == 0;
            });
            lifecycle.Checkpoint(id, expected, MissionInitializationPhase.NativeLoaded);
            Assert.IsTrue(observed);
            Assert.IsTrue(clock.TryRead(expected, out int zero)); Assert.AreEqual(0, zero);
            Assert.IsFalse(clock.TryRead(Mission(id + 1), out _));
            var racing = new MissionSimulationClockReader(() => lifecycle.NativeClock, () => {
                lifecycle.BeginNativeLoad(id); lifecycle.CompleteNativeLoad(id, true); return 400;
            }, () => true);
            Assert.IsFalse(racing.TryRead(expected, out _));
            lifecycle.BeginNativeLoad(id);
            lifecycle.BeginNativeLoad(id);
            lifecycle.CompleteNativeLoad(id, false);
            lifecycle.CompleteNativeLoad(id, true);
            Assert.IsFalse(clock.TryRead(expected, out _));
            lifecycle.BeginNativeLoad(id); lifecycle.CompleteNativeLoad(id, true);
            lifecycle.Ready(id, expected);
            Assert.IsTrue(clock.TryRead(expected, out _));
            lifecycle.End(MissionEndReason.Unloaded);
            Assert.IsFalse(clock.TryRead(expected, out _));
            lifecycle.Begin(Mission(0));
            Assert.IsFalse(clock.TryRead(expected, out _));
            Assert.IsTrue(reads > 0);
        }

        [TestMethod]
        public void NativeWorldIdentitySurvivesMetadataButRetiresBeforeReentrantReplacementObservers()
        {
            var lifecycle = new MissionLifecycleState(error => Assert.Fail(error.ToString()));
            long id = lifecycle.Begin(Mission(0));
            lifecycle.BeginNativeLoad(id); lifecycle.CompleteNativeLoad(id, true);
            var first = lifecycle.NativeClock.World;
            Assert.AreEqual(id, first.Mission);
            var beforeCheckpoint = lifecycle.NativeClock;
            lifecycle.Checkpoint(id, lifecycle.Context, MissionInitializationPhase.NativeLoaded);
            lifecycle.Ready(id, lifecycle.Context);
            Assert.AreNotSame(beforeCheckpoint, lifecycle.NativeClock, "Clock snapshots still track context changes.");
            Assert.AreSame(first, lifecycle.NativeClock.World, "Context refresh must not revoke the live native world.");

            // A repeated native load invalidates before its managed completion;
            // the same mission ID cannot make old native identities current again.
            lifecycle.BeginNativeLoad(id); Assert.IsNull(lifecycle.NativeClock);
            lifecycle.CompleteNativeLoad(id, true);
            var reloaded = lifecycle.NativeClock.World;
            Assert.AreNotSame(first, reloaded); Assert.IsTrue(reloaded.Sequence > first.Sequence);
            long replacement = 0;
            bool sawRetired = false;
            lifecycle.Register("world.fixture", "replace", notification => {
                if (notification.Kind != MissionLifecycleKind.End) return;
                sawRetired = lifecycle.NativeClock == null;
                replacement = lifecycle.Context.SessionId;
                lifecycle.BeginNativeLoad(replacement); lifecycle.CompleteNativeLoad(replacement, true);
            });
            bool sawNewWorld = false;
            lifecycle.Register("world.fixture", "late-end", notification => {
                if (notification.Kind != MissionLifecycleKind.End) return;
                var current = lifecycle.NativeClock?.World;
                sawNewWorld = current != null && current.Mission == replacement && !ReferenceEquals(current, reloaded);
            });
            lifecycle.Begin(Mission(0));
            Assert.IsTrue(sawRetired); Assert.IsTrue(sawNewWorld,
                "An old End observer can run after another observer has loaded the new native world.");
        }

        [TestMethod]
        public void ElapsedClockAllowsZeroAndSignedWrapButRejectsBackwards()
        {
            Assert.IsTrue(SimulationClock.TryGetElapsedTicks(0, 0, out int elapsed)); Assert.AreEqual(0, elapsed);
            Assert.IsTrue(SimulationClock.TryGetElapsedTicks(int.MaxValue - 3, int.MinValue + 6, out elapsed)); Assert.AreEqual(10, elapsed);
            Assert.IsTrue(SimulationClock.TryGetElapsedTicks(-10, 10, out elapsed)); Assert.AreEqual(20, elapsed);
            Assert.IsFalse(SimulationClock.TryGetElapsedTicks(401, 400, out elapsed)); Assert.AreEqual(0, elapsed);
        }

        [TestMethod]
        public void ReadyReaderRejectsSameMissionReloadDuringRead()
        {
            var context = Mission(1); object generation = new object();
            var reader = new SimulationTickReader(() => context, () => { generation = new object(); return 400; },
                () => true, () => generation);
            Assert.IsFalse(reader.TryRead(out _, out _));
        }

        private sealed class World
        {
            internal MissionContext Current = Mission(1);
            internal int NativeTick, Reads, Activations;
            internal bool Available = true, FailActivation;
            internal readonly StrongholdFrameProvider Provider = new StrongholdFrameProvider();
            internal readonly SimulationTickRegistry Registry;
            internal World()
            {
                var reader = new SimulationTickReader(() => Current, () => { Reads++; return NativeTick; }, () => Available);
                Registry = new SimulationTickRegistry(reader.TryRead, callback => {
                    Activations++;
                    if (FailActivation) throw new InvalidOperationException("source absent");
                    Provider.OnGameTick += callback;
                });
            }
            internal void Emit(int native, int local, bool paused = false)
            {
                NativeTick = native;
                var state = new FrameState(local, (long)native * DeterministicClock.UNITS_PER_TICK, paused);
                Provider.Tick(in state);
            }
        }

        [TestMethod]
        public void NativeTickReplacesOnlyTheNewPayloadAndIsReadOnceForEveryPublication()
        {
            var world = new World();
            var native = new List<int>(); var local = new List<int>();
            world.Provider.OnGameTick += local.Add;
            Assert.IsTrue(world.Registry.TryRegister("a", "one", native.Add, 0, out _));
            Assert.IsTrue(world.Registry.TryRegister("b", "two", native.Add, 0, out _));
            var runner = new Runner(); world.Provider.Register(runner);
            world.Emit(400, 903);
            CollectionAssert.AreEqual(new[] { 400, 400 }, native);
            CollectionAssert.AreEqual(new[] { 903 }, local);
            Assert.AreEqual(903, runner.LastTick);
            Assert.AreEqual(903L, world.Provider.GetFrameCount());
            Assert.AreEqual(903, world.Provider.CurrentGameTick);
            Assert.AreEqual(903, world.Provider.CurrentFrameState.GameTick);
            Assert.AreEqual(1, world.Reads);
            Assert.AreEqual(1, world.Activations);
        }

        private sealed class Runner : R3.IFrameRunnerWorkItem
        {
            internal long LastTick;
            public bool MoveNext(long frameCount) { LastTick = frameCount; return true; }
        }

        [TestMethod]
        public void DifferentLocalCountersProduceTheSameMapTickSequence()
        {
            var host = new World(); var client = new World();
            var h = new List<int>(); var c = new List<int>();
            host.Registry.TryRegister("mod", "tick", h.Add, 0, out _);
            client.Registry.TryRegister("mod", "tick", c.Add, 0, out _);
            foreach (int tick in new[] { 399, 400, 400, 799, 800, 1200 })
            { host.Emit(tick, tick + 3); client.Emit(tick, tick * 2 + 19); }
            CollectionAssert.AreEqual(new[] { 399, 400, 799, 800, 1200 }, h);
            CollectionAssert.AreEqual(h, c);
        }

        [TestMethod]
        public void RegistrationsAreOrderedSnapshotsAndFailuresDoNotBlockLaterObservers()
        {
            var world = new World(); var calls = new List<string>(); bool added = false;
            world.Registry.TryRegister("b", "a", _ => calls.Add("b/a"), 0, out _);
            world.Registry.TryRegister("a", "z", _ => calls.Add("a/z"), 0, out _);
            world.Registry.TryRegister("a", "a", tick => {
                calls.Add("a/a");
                if (!added) { added = true; world.Registry.TryRegister("a", "b", x => calls.Add("new"), 0, out _); }
                world.Registry.OnSourceTick(999); // Reentrancy must not publish the same/next tick recursively.
                throw new InvalidOperationException("consumer failure");
            }, 0, out _);
            world.Registry.TryRegister("z", "first", _ => calls.Add("first"), -1, out _);
            world.Emit(1, 20);
            CollectionAssert.AreEqual(new[] { "first", "a/a", "a/z", "b/a" }, calls);
            calls.Clear(); world.Emit(2, 21);
            CollectionAssert.AreEqual(new[] { "first", "a/a", "new", "a/z", "b/a" }, calls);
            Assert.AreEqual(2L, world.Registry.CallbackFailures);
        }

        [TestMethod]
        public void MissingPendingAndReplacedMissionsCannotReadOrPublishInventedTicks()
        {
            var world = new World(); var ticks = new List<int>();
            world.Registry.TryRegister("mod", "tick", ticks.Add, 0, out _);
            world.Current = null; world.Emit(400, 40);
            Assert.IsFalse(world.Registry.TryGetCurrentTick(out int missing)); Assert.AreEqual(0, missing);
            Assert.AreEqual(0, world.Reads);
            world.Current = Mission(2); world.Available = false; world.Emit(400, 41);
            Assert.AreEqual(0, world.Reads);
            world.Available = true; world.Emit(400, 42); world.Emit(399, 43); world.Emit(400, 44);
            CollectionAssert.AreEqual(new[] { 400 }, ticks);
            world.Emit(401, 45, paused: true);
            CollectionAssert.AreEqual(new[] { 400 }, ticks);
            world.Current = Mission(3); world.Emit(400, 46);
            CollectionAssert.AreEqual(new[] { 400, 400 }, ticks);
            world.NativeTick = 0;
            Assert.IsTrue(world.Registry.TryGetCurrentTick(out int zero)); Assert.AreEqual(0, zero);
            MissionContext current = Mission(4);
            var replacing = new SimulationTickReader(() => current, () => { current = Mission(5); return 500; }, () => true);
            Assert.IsFalse(replacing.TryRead(out _, out _));
        }

        [TestMethod]
        public void RegistrationValidationRetryLateJoinAndCounterWrapHaveExplicitBehavior()
        {
            var world = new World { FailActivation = true };
            Assert.IsFalse(world.Registry.TryRegister(null, "id", _ => { }, 0, out _));
            Assert.IsFalse(world.Registry.TryRegister("owner", "id", null, 0, out _));
            Assert.AreEqual(0, world.Activations);
            Assert.IsFalse(world.Registry.TryRegister("owner", "id", _ => { }, 0, out string reason));
            StringAssert.Contains(reason, "source absent");
            world.FailActivation = false; var ticks = new List<int>();
            Assert.IsTrue(world.Registry.TryRegister("owner", "id", ticks.Add, 0, out _));
            Assert.IsFalse(world.Registry.TryRegister("owner", "id", ticks.Add, 1, out _));
            world.Emit(int.MaxValue, 1);
            var late = new List<int>();
            world.Registry.TryRegister("owner", "late", late.Add, 0, out _);
            Assert.AreEqual(0, late.Count);
            world.Emit(int.MaxValue, 2); Assert.AreEqual(0, late.Count);
            world.Emit(int.MinValue, 3);
            CollectionAssert.AreEqual(new[] { int.MaxValue, int.MinValue }, ticks);
            CollectionAssert.AreEqual(new[] { int.MinValue }, late);
        }

        [TestMethod]
        public void ReadFailureDoesNotTurnIntoTickZeroAndCallbackPathHasNoSteadyStateAllocations()
        {
            var failing = new SimulationTickRegistry((out long session, out int tick) => {
                session = 0; tick = 0; throw new InvalidOperationException("read failed");
            }, _ => { }, (_, __) => { throw new InvalidOperationException("log failed"); });
            Assert.IsFalse(failing.TryGetCurrentTick(out int value)); Assert.AreEqual(0, value);
            int calls = 0; failing.TryRegister("mod", "tick", _ => calls++, 0, out _); failing.OnSourceTick(400);
            Assert.AreEqual(0, calls);
            var registry = new SimulationTickRegistry((out long session, out int tick) => {
                session = 1; tick = ++counter; return true;
            }, _ => { });
            registry.TryRegister("mod", "tick", Ignore, 0, out _);
            var allocated = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>),
                typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread"));
            for (int i = 0; i < 1000; i++) registry.OnSourceTick(i);
            long before = allocated();
            for (int i = 0; i < 10000; i++) registry.OnSourceTick(i);
            Assert.AreEqual(0L, allocated() - before);
        }
        private static int counter;
        private static void Ignore(int tick) { }
    }
}
