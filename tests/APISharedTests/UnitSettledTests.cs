using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using APIShared.Units;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Units;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    // Installed Script Extender types: event-argument filtering, the GameUnit field mapping and the
    // compiled fast paths. Activation itself needs the live game (SE time API) and is checked in game.
    [TestClass]
    [TestCategory("UnitSettled")]
    [DoNotParallelize]
    public sealed class UnitSettledTests
    {
        public TestContext TestContext { get; set; }

        private sealed class Slots : IUnitSettleReader
        {
            internal readonly Dictionary<int, UnitSettleProbe> Units = new Dictionary<int, UnitSettleProbe>();
            public UnitSettleProbe Read(int unitId) => Units.TryGetValue(unitId, out UnitSettleProbe unit) ? unit : default(UnitSettleProbe);
            internal void Alive(int id, eChimps type) =>
                Units[id] = new UnitSettleProbe { Found = true, Alive = true, GlobalId = 1000 + id, PlayerId = 1, UnitType = type };
        }

        [TestMethod]
        public void FacadeRejectsInvalidRegistrationsWithoutSubscribingToScriptExtender()
        {
            Assert.IsFalse(UnitSettledEvents.TryRegister(null, "id", _ => { }, out string reason));
            Assert.IsFalse(string.IsNullOrEmpty(reason));
            Assert.IsFalse(UnitSettledEvents.TryRegister("owner", "id", null, out _));
            Assert.IsFalse(UnitSettledScriptExtenderSource.IsActive, "No SE subscription exists before a valid registration.");
            Assert.IsFalse(UnitSettledEvents.Tracker.IsActivated);
        }

        [TestMethod]
        public void ScriptExtenderArgumentsQueueOnlyPostCreationsAndPreTransitions()
        {
            var slots = new Slots();
            var tracker = new UnitSettledTracker(slots, null);
            var received = new List<UnitSettledNotification>();
            Assert.IsTrue(tracker.TryRegister("owner", "observer", received.Add, 0, out _));
            for (int id = 1; id <= 8; id++) slots.Alive(id, eChimps.CHIMP_TYPE_PEASANT);

            var createPre = new UnitCreateEventArgs(EventHookPhase.Pre, 1, 1, 10, 10, 0, eChimps.CHIMP_TYPE_PEASANT) { ReturnValue = 1 };
            var createFailed = new UnitCreateEventArgs(EventHookPhase.Post, 1, 1, 10, 10, 0, eChimps.CHIMP_TYPE_PEASANT);
            var createPost = new UnitCreateEventArgs(EventHookPhase.Post, 1, 1, 10, 10, 0, eChimps.CHIMP_TYPE_PEASANT) { ReturnValue = 1 };
            UnitSettledScriptExtenderSource.Forward(createPre, tracker);
            UnitSettledScriptExtenderSource.Forward(createFailed, tracker);
            UnitSettledScriptExtenderSource.Forward((UnitCreateEventArgs)null, tracker);
            Assert.AreEqual(0, tracker.PendingUnits);
            UnitSettledScriptExtenderSource.Forward(createPost, tracker);
            Assert.AreEqual(1, tracker.PendingUnits);

            var sources = new[] {
                UnitTransitionSource.MercenaryOutpost, UnitTransitionSource.EuropeanBarracks,
                UnitTransitionSource.Worker, UnitTransitionSource.Disband, (UnitTransitionSource)8,
            };
            for (int index = 0; index < sources.Length; index++)
            {
                UnitSettledScriptExtenderSource.Forward(
                    new UnitTransitionEventArgs(EventHookPhase.Post, 2 + index, 1, eChimps.CHIMP_TYPE_ARCHER, sources[index]), tracker);
                UnitSettledScriptExtenderSource.Forward(
                    new UnitTransitionEventArgs(EventHookPhase.Pre, 2 + index, 1, eChimps.CHIMP_TYPE_ARCHER, sources[index]), tracker);
            }
            Assert.AreEqual(6, tracker.PendingUnits, "Transition Post (not raised by SE 2.14.1) does not queue twice.");

            for (int id = 2; id <= 6; id++) slots.Alive(id, eChimps.CHIMP_TYPE_ARCHER);
            tracker.Tick(7);
            CollectionAssert.AreEqual(new[] {
                UnitSettledCause.Created, UnitSettledCause.MercenaryOutpost, UnitSettledCause.EuropeanBarracks,
                UnitSettledCause.Worker, UnitSettledCause.Disband, UnitSettledCause.OtherTransition,
            }, received.ConvertAll(unit => unit.Cause));
            Assert.AreEqual(eChimps.CHIMP_TYPE_PEASANT, received[1].PreviousUnitType);
        }

        [TestMethod]
        public void ProbeReadsTheInstalledGameUnitLayout()
        {
            var unit = new GameUnit
            {
                r_AliveState = AliveState.IsAlive,
                r_GlobalId = 1513845,
                r_ControllableForPlayerId = 3,
                r_UnitChimp = eChimps.CHIMP_TYPE_MACEMAN,
            };
            UnitSettleProbe probe = UnitSettledScriptExtenderSource.Probe(in unit);
            Assert.IsTrue(probe.Found && probe.Alive && !probe.Dead);
            Assert.AreEqual(1513845, probe.GlobalId);
            Assert.AreEqual(3, probe.PlayerId);
            Assert.AreEqual(eChimps.CHIMP_TYPE_MACEMAN, probe.UnitType);

            unit.r_IsKilledByProjectile = 1;
            probe = UnitSettledScriptExtenderSource.Probe(in unit);
            Assert.IsTrue(!probe.Alive && probe.Dead, "The native death marker ends tracking.");
            unit.r_IsKilledByProjectile = 0;

            foreach (AliveState waiting in new[] { AliveState.NeedsInit, AliveState.Unknown, AliveState.Paused })
            {
                unit.r_AliveState = waiting;
                probe = UnitSettledScriptExtenderSource.Probe(in unit);
                Assert.IsTrue(!probe.Alive && !probe.Dead, waiting + " keeps waiting (recruitment's transient state is 4).");
            }
            foreach (AliveState gone in new[] { AliveState.None, AliveState.MarkedForDeletion })
            {
                unit.r_AliveState = gone;
                Assert.IsTrue(UnitSettledScriptExtenderSource.Probe(in unit).Dead, gone + " ends tracking.");
            }
        }

        [TestMethod]
        public void CompiledScriptExtenderEntryPointsAndTickGuardContainNoHeapAllocation()
        {
            const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
            var methods = new List<MethodBase>
            {
                typeof(UnitSettledTracker).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic),
                typeof(UnitSettledTracker).GetMethod("Enqueue", BindingFlags.Instance | BindingFlags.NonPublic),
                typeof(UnitSettledScriptExtenderSource).GetMethod("OnTick", Static),
                typeof(UnitSettledScriptExtenderSource).GetMethod("OnUnitCreate", Static),
                typeof(UnitSettledScriptExtenderSource).GetMethod("OnUnitTransition", Static),
                typeof(UnitSettledScriptExtenderSource).GetMethod("Discard", Static),
                typeof(UnitSettledScriptExtenderSource).GetMethod("Forward", Static, null,
                    new[] { typeof(UnitCreateEventArgs), typeof(UnitSettledTracker) }, null),
                typeof(UnitSettledScriptExtenderSource).GetMethod("Forward", Static, null,
                    new[] { typeof(UnitTransitionEventArgs), typeof(UnitSettledTracker) }, null),
            };
            foreach (MethodBase method in methods)
            {
                Assert.IsNotNull(method);
                using (var definition = new DynamicMethodDefinition(method))
                    foreach (Instruction instruction in definition.Definition.Body.Instructions)
                    {
                        bool heap = instruction.OpCode == OpCodes.Newarr || instruction.OpCode == OpCodes.Box ||
                            instruction.OpCode == OpCodes.Newobj &&
                            !((Mono.Cecil.MethodReference)instruction.Operand).DeclaringType.Resolve().IsValueType;
                        Assert.IsFalse(heap, method.Name + " allocates: " + instruction);
                    }
            }
        }

        [TestMethod]
        public void WarmTickQueueAndDispatchAllocationAndTiming()
        {
            var slots = new Slots();
            var tracker = new UnitSettledTracker(slots, null);
            long sum = 0;
            for (int index = 0; index < 4; index++)
                Assert.IsTrue(tracker.TryRegister("owner", index.ToString(), unit => sum += unit.GlobalId, 0, out _));
            for (int id = 1; id <= 32; id++) slots.Alive(id, eChimps.CHIMP_TYPE_ARCHER);

            Measure("idle tick", 200000, () => tracker.Tick(1), out long idleBytes, out double idleNs);
            Assert.AreEqual(0L, idleBytes, "An idle tick allocates.");

            // 32 queued creations settling on the next tick, delivered to 4 registrations.
            Measure("32 created units, queue and settle", 2000, () => {
                for (int id = 1; id <= 32; id++) tracker.Enqueue(id, UnitSettledCause.Created);
                tracker.Tick(2);
            }, out long cycleBytes, out double cycleNs);
            Assert.AreEqual(0L, cycleBytes, "Queueing, settlement and dispatch allocate after warm-up.");

            // 200 warm-up plus 5 x 200 measured ticks stay below the 3000-tick settle limit.
            for (int id = 1; id <= 32; id++) tracker.Enqueue(id, UnitSettledCause.EuropeanBarracks);
            Measure("32 waiting recruits", 200, () => tracker.Tick(3), out long waitingBytes, out double waitingNs);
            Assert.AreEqual(0L, waitingBytes, "Waiting units allocate per tick.");
            Assert.AreEqual(32, tracker.PendingUnits);
            Assert.IsTrue(sum > 0);
            TestContext.WriteLine("UNIT_SETTLED_BENCH net481 idleTickNs={0:F1} settleCycleNsPerUnit={1:F1} waitingTickNsPerUnit={2:F1} bytes=0",
                idleNs, cycleNs / 32, waitingNs / 32);
        }

        private static void Measure(string label, int iterations, Action body, out long bytes, out double nanoseconds)
        {
            for (int i = 0; i < iterations; i++) body();
            var times = new long[5];
            bytes = long.MaxValue;
            for (int sample = 0; sample < times.Length; sample++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++) body();
                times[sample] = Stopwatch.GetTimestamp() - start;
                bytes = Math.Min(bytes, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            Array.Sort(times);
            nanoseconds = times[2] * (1e9 / Stopwatch.Frequency) / iterations;
        }
    }
}
