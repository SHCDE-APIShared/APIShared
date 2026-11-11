using APIShared;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    [TestCategory("PlayerDefeat")]
    [DoNotParallelize]
    public sealed class PlayerDefeatTests
    {
        [TestMethod]
        public void LordDeathFollowsLivingIdentityAndPublishesOnce()
        {
            var state = new PlayerDefeatState();
            var lordDeaths = new List<PlayerLordDeathNotification>();
            state.SynchronizeSession(11);
            state.Observe(1, Slots(Slot(WinLossState.None)), lordDeaths.Add, null);
            state.Observe(2, Slots(Living(4, 40)), lordDeaths.Add, null);
            state.Observe(3, Slots(Living(5, 50)), lordDeaths.Add, null);
            Check(lordDeaths.Count == 0, "a living lord replacement does not report a death");
            state.Observe(4, Slots(Slot(WinLossState.None)), lordDeaths.Add, null);
            state.Observe(5, Slots(Slot(WinLossState.None)), lordDeaths.Add, null);
            state.Observe(6, Slots(Living(6, 60)), lordDeaths.Add, null);
            state.Observe(7, Slots(Slot(WinLossState.None)), lordDeaths.Add, null);
            Check(lordDeaths.Count == 1 &&
                  lordDeaths[0].SessionId == 11 &&
                  lordDeaths[0].PlayerId == 1 &&
                  lordDeaths[0].LordUnitId == 5 &&
                  lordDeaths[0].LordGlobalId == 50 &&
                  lordDeaths[0].SimulationTick == 4,
                "lord death is armed by life, follows replacement identity and publishes once");

            var delayedSpawn = new PlayerDefeatState();
            lordDeaths.Clear();
            delayedSpawn.SynchronizeSession(12);
            delayedSpawn.Observe(1, Slots(Slot(WinLossState.None)), lordDeaths.Add, null);
            delayedSpawn.Observe(2, Slots(Living(7, 70)), lordDeaths.Add, null);
            delayedSpawn.Observe(3, Slots(Slot(WinLossState.None)), lordDeaths.Add, null);
            Check(lordDeaths.Count == 1 && lordDeaths[0].LordUnitId == 7,
                "a lord spawned after the baseline arms the detector");
        }

        [TestMethod]
        public void OfficialLossPublishesOnceIndependentlyOfLordIdentity()
        {
            var state = new PlayerDefeatState();
            var defeats = new List<PlayerDefeatNotification>();
            state.SynchronizeSession(21);
            state.Observe(1, Slots(Slot(WinLossState.None), Slot(WinLossState.Win)), null, defeats.Add);
            state.Observe(2, Slots(Slot(WinLossState.Loss), Slot(WinLossState.Loss)), null, defeats.Add);
            state.Observe(3, Slots(Slot(WinLossState.Loss), Slot(WinLossState.Loss)), null, defeats.Add);
            state.Observe(4, Slots(Slot(WinLossState.None), Slot(WinLossState.Win)), null, defeats.Add);
            state.Observe(5, Slots(Slot(WinLossState.Loss), Slot(WinLossState.Loss)), null, defeats.Add);
            Check(defeats.Count == 2 &&
                  defeats[0].PlayerId == 1 &&
                  defeats[1].PlayerId == 2 &&
                  defeats[0].SimulationTick == 2 &&
                  defeats[1].SimulationTick == 2,
                "None-to-Loss and Win-to-Loss publish once per player and session");

            var lordless = new PlayerDefeatState();
            defeats.Clear();
            lordless.SynchronizeSession(22);
            lordless.Observe(1, Slots(Slot(WinLossState.None)), null, defeats.Add);
            lordless.Observe(2, Slots(Slot(WinLossState.Loss)), null, defeats.Add);
            Check(defeats.Count == 1,
                "official defeat does not depend on a lord identity");
        }

        [TestMethod]
        public void LoadedAndReplacedSessionsEstablishFreshBaselinesWithoutReplay()
        {
            var state = new PlayerDefeatState();
            var lordDeaths = new List<PlayerLordDeathNotification>();
            var defeats = new List<PlayerDefeatNotification>();
            state.SynchronizeSession(31);
            state.Observe(8, Slots(Slot(WinLossState.Loss)), lordDeaths.Add, defeats.Add);
            state.Observe(9, Slots(Slot(WinLossState.Loss)), lordDeaths.Add, defeats.Add);
            Check(lordDeaths.Count == 0 && defeats.Count == 0,
                "a loaded save with an existing death or loss only establishes a baseline");

            state.Observe(10, Slots(Living(8, 80)), lordDeaths.Add, defeats.Add);
            state.SynchronizeSession(null);
            state.SynchronizeSession(32);
            state.Observe(1, Slots(Slot(WinLossState.Loss)), lordDeaths.Add, defeats.Add);
            Check(lordDeaths.Count == 0 && defeats.Count == 0,
                "mission end and session change clear armed and official state without replay");
        }

        [TestMethod]
        public void OwnersReceiveOrderedIsolatedAndReentrantNotifications()
        {
            Check(!ApiShared.Current.TryGetPlayerDefeat(
                    "",
                    out _,
                    out NativeCapabilityDiagnostic invalidOwner) &&
                  invalidOwner.State == NativeCapabilityState.ValidationFailed,
                "capability acquisition rejects an empty owner GUID");
            PlayerDefeatService service = PlayerDefeatService.TestCreate();
            IPlayerDefeatCapability beta = service.Bind("beta");
            IPlayerDefeatCapability alpha = service.Bind("alpha");
            var calls = new List<string>();

            Check(alpha.TryRegisterObserver("two", _ => calls.Add("alpha/two/lord"),
                    _ => calls.Add("alpha/two/defeat"), out NativeCapabilityDiagnostic available) &&
                  available.State == NativeCapabilityState.Available,
                "valid observer registration is available");
            Check(beta.TryRegisterObserver("one", _ => calls.Add("beta/one/lord"),
                    _ => calls.Add("beta/one/defeat"), out _),
                "second owner registration succeeds");
            Check(!alpha.TryRegisterObserver("two", _ => { }, null, out NativeCapabilityDiagnostic duplicate) &&
                  duplicate.State == NativeCapabilityState.Conflict,
                "duplicate owner-local registration IDs are rejected");
            Check(!alpha.TryRegisterObserver("", _ => { }, null, out _) &&
                  !alpha.TryRegisterObserver("empty", null, null, out _),
                "invalid registration IDs and empty callback sets are rejected");

            IPlayerDefeatCapability first = service.Bind("00-throwing");
            Check(first.TryRegisterObserver(
                    "observer",
                    _ =>
                    {
                        calls.Add("00/lord");
                        service.TestPublish(new PlayerDefeatNotification(41, 1, 9));
                        throw new InvalidOperationException("expected test exception");
                    },
                    _ => calls.Add("00/defeat"),
                    out _),
                "throwing observer registers");

            service.TestPublish(new PlayerLordDeathNotification(41, 1, 2, 20, 8));
            string joined = string.Join(",", calls);
            Check(joined ==
                  "00/lord,alpha/two/lord,beta/one/lord,00/defeat,alpha/two/defeat,beta/one/defeat",
                "observers are deterministic, reentrant publication is queued and exceptions are isolated");
            Check(NativeCapabilityIds.PlayerDefeat == "player-defeat",
                "player-defeat capability ID is stable");
        }

        [TestMethod]
        public void LoadedLossIsQueryableStateWithoutReplayingTransitions()
        {
            long? session = 71;
            var observations = Slots(Slot(WinLossState.Loss), Slot(WinLossState.None));
            var service = PlayerDefeatService.TestCreate(() => session, () => observations);
            var capability = (IPlayerDefeatStateCapability)service.Bind("consumer");
            int transitions = 0;
            var snapshots = new List<PlayerDefeatSnapshot>();
            Assert.IsTrue(capability.TryRegisterObserver("transitions", _ => transitions++, _ => transitions++, out _));
            Assert.IsTrue(capability.TryRegisterStateObserver("states", snapshots.Add, out _));
            Assert.IsFalse(capability.TryGetObservedState(71, 1, out _));
            service.TestTick(3);
            Assert.IsTrue(capability.TryGetObservedState(71, 1, out var loss));
            Assert.IsTrue(loss.Readable && loss.OfficialLoss && loss.IsEliminated);
            Assert.IsFalse(loss.HasLivingLord || loss.LordDeathConfirmed);
            Assert.AreEqual(3, loss.SimulationTick);
            Assert.AreEqual(0, transitions);
            Assert.AreEqual(8, snapshots.Count);
            Assert.IsTrue(capability.TryGetObservedState(71, 2, out var lordless));
            Assert.IsFalse(lordless.IsEliminated, "absence in a baseline grants no elimination rights");
            Assert.IsFalse(capability.TryGetObservedState(72, 1, out _));
            Assert.IsFalse(capability.TryGetObservedState(71, 0, out _));
            Assert.IsFalse(capability.TryGetObservedState(71, 9, out _));

            var replay = new List<PlayerDefeatSnapshot>();
            Assert.IsTrue(capability.TryRegisterStateObserver("late", replay.Add, out _));
            Assert.AreEqual(8, replay.Count);
            for (int i = 0; i < replay.Count; i++) Assert.AreSame(snapshots[i], replay[i]);
            Assert.IsFalse(capability.TryRegisterObserver("late", _ => { }, null, out _),
                "state and transition registrations share the owner-local ID namespace");
            Assert.AreEqual(0, transitions);
        }

        [TestMethod]
        public void StableObservationsReuseSnapshotsAndConfirmedDeathPrecedesTransitions()
        {
            var observations = Slots(Living(10, 100));
            var service = PlayerDefeatService.TestCreate(() => 81, () => observations);
            var capability = (IPlayerDefeatStateCapability)service.Bind("consumer");
            int changes = 0, deaths = 0;
            bool stateWasCommitted = false;
            Assert.IsTrue(capability.TryRegisterStateObserver("state", _ => changes++, out _));
            Assert.IsTrue(capability.TryRegisterObserver("death", _ => {
                deaths++;
                stateWasCommitted = capability.TryGetObservedState(81, 1, out var committed) && committed.IsEliminated;
            }, null, out _));
            service.TestTick(1);
            Assert.IsTrue(capability.TryGetObservedState(81, 1, out var first));
            service.TestTick(2);
            Assert.IsTrue(capability.TryGetObservedState(81, 1, out var unchanged));
            Assert.AreSame(first, unchanged, "unchanged ticks allocate no new state snapshots");
            Assert.AreEqual(1, unchanged.SimulationTick);
            Assert.AreEqual(8, changes);
            observations = Slots(Slot(WinLossState.None));
            service.TestTick(3);
            Assert.IsTrue(capability.TryGetObservedState(81, 1, out var dead));
            Assert.IsTrue(dead.LordDeathConfirmed && dead.IsEliminated && !dead.OfficialLoss && !dead.HasLivingLord);
            Assert.AreEqual(1, deaths);
            Assert.IsTrue(stateWasCommitted, "transition observers can query the already committed elimination state");
            Assert.AreEqual(9, changes);
        }

        [TestMethod]
        public void UnreadableCaptureInvalidatesRightsAndRetainsConfirmedHistoryForRecovery()
        {
            bool fail = false;
            var observations = Slots(Living(11, 110));
            var service = PlayerDefeatService.TestCreate(() => 91, () => {
                if (fail) throw new InvalidOperationException("unavailable capture");
                return observations;
            });
            var capability = (IPlayerDefeatStateCapability)service.Bind("consumer");
            Assert.IsTrue(capability.TryRegisterStateObserver("state", _ => { }, out _));
            service.TestTick(1);
            observations = Slots(Slot(WinLossState.None));
            service.TestTick(2);
            Assert.IsTrue(capability.TryGetObservedState(91, 1, out var dead) && dead.IsEliminated);
            fail = true;
            service.TestTick(3);
            Assert.IsTrue(capability.TryGetObservedState(91, 1, out var unknown));
            Assert.IsFalse(unknown.Readable || unknown.IsEliminated || unknown.LordDeathConfirmed);
            fail = false;
            service.TestTick(4);
            Assert.IsTrue(capability.TryGetObservedState(91, 1, out var restored) && restored.LordDeathConfirmed && restored.IsEliminated);
            observations = null;
            service.TestTick(5);
            Assert.IsTrue(capability.TryGetObservedState(91, 1, out unknown) && !unknown.Readable && !unknown.IsEliminated);
        }

        [TestMethod]
        public void EndedSessionCannotGrantRightsBeforeOrAfterTheNextTick()
        {
            long? session = 101;
            var observations = Slots(Slot(WinLossState.Loss));
            var service = PlayerDefeatService.TestCreate(() => session, () => observations);
            var capability = (IPlayerDefeatStateCapability)service.Bind("consumer");
            var changes = new List<PlayerDefeatSnapshot>();
            Assert.IsTrue(capability.TryRegisterStateObserver("state", changes.Add, out _));
            service.TestTick(1);
            Assert.IsTrue(capability.TryGetObservedState(101, 1, out var ended) && ended.IsEliminated);
            session = null;
            Assert.IsFalse(capability.TryGetObservedState(101, 1, out _), "lookup sees lifecycle invalidation immediately");
            service.TestTick(2);
            Assert.IsFalse(changes[changes.Count - 1].Readable);
            Assert.AreEqual(101L, changes[changes.Count - 1].SessionId);
            session = 102;
            observations = Slots(Slot(WinLossState.None));
            Assert.IsFalse(capability.TryGetObservedState(102, 1, out _));
            service.TestTick(3);
            Assert.IsTrue(capability.TryGetObservedState(102, 1, out var fresh) && !fresh.IsEliminated);
            Assert.IsFalse(capability.TryGetObservedState(101, 1, out _));
            Assert.IsTrue(ended.IsEliminated, "retained snapshots are immutable historical values, not live permission tokens");
        }

        [TestMethod]
        public void ReentrantStateRegistrationReplaysCommittedStateOnceInPlayerOrder()
        {
            var service = PlayerDefeatService.TestCreate(() => 111, () => Slots(Slot(WinLossState.Loss)));
            var alpha = (IPlayerDefeatStateCapability)service.Bind("alpha");
            var beta = (IPlayerDefeatStateCapability)service.Bind("beta");
            var calls = new List<string>();
            var replay = new List<PlayerDefeatSnapshot>();
            Assert.IsTrue(alpha.TryRegisterStateObserver("first", snapshot => {
                calls.Add("a" + snapshot.PlayerId);
                if (snapshot.PlayerId == 1)
                {
                    Assert.IsTrue(beta.TryRegisterStateObserver("late", value => {
                        replay.Add(value); calls.Add("b" + value.PlayerId);
                    }, out _));
                    throw new InvalidOperationException("isolated callback");
                }
            }, out _));
            service.TestTick(1);
            Assert.AreEqual(8, replay.Count, "the new observer gets targeted replay, never earlier queued publications twice");
            Assert.AreEqual("a1,a2,a3,a4,a5,a6,a7,a8,b1,b2,b3,b4,b5,b6,b7,b8", string.Join(",", calls));
            Assert.IsFalse(alpha.TryRegisterStateObserver("", _ => { }, out _));
            Assert.IsFalse(alpha.TryRegisterStateObserver("null", null, out _));
        }

        [TestMethod]
        public void ConcurrentSnapshotQueriesSeeWholeImmutableValues()
        {
            var service = PlayerDefeatService.TestCreate(() => 121, () => Slots(Slot(WinLossState.Loss)));
            var capability = (IPlayerDefeatStateCapability)service.Bind("consumer");
            Assert.IsTrue(capability.TryRegisterStateObserver("state", _ => { }, out _));
            service.TestTick(1);
            var reader = System.Threading.Tasks.Task.Run(() => {
                for (int i = 0; i < 1000; i++)
                {
                    Assert.IsTrue(capability.TryGetObservedState(121, 1, out var snapshot));
                    Assert.IsTrue(snapshot.SessionId == 121 && snapshot.PlayerId == 1 && snapshot.OfficialLoss && snapshot.IsEliminated);
                }
            });
            for (int i = 2; i < 100; i++) service.TestTick(i);
            Assert.IsTrue(reader.Wait(TimeSpan.FromSeconds(5)));
        }

        [TestMethod]
        public void ConcurrentStateReplayCannotMoveTransitionsOffTheirTickPublisher()
        {
            var observations = Slots(Living(12, 120));
            var service = PlayerDefeatService.TestCreate(() => 131, () => observations);
            var capability = (IPlayerDefeatStateCapability)service.Bind("consumer");
            int transitionThread = 0, transitions = 0;
            Assert.IsTrue(capability.TryRegisterObserver("transition", _ => {
                transitionThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                transitions++;
            }, null, out _));
            service.TestTick(1);
            using (var enteredReplay = new System.Threading.ManualResetEventSlim())
            using (var releaseReplay = new System.Threading.ManualResetEventSlim())
            {
                var replay = System.Threading.Tasks.Task.Run(() => capability.TryRegisterStateObserver("replay", snapshot => {
                    if (snapshot.PlayerId != 1) return;
                    enteredReplay.Set();
                    releaseReplay.Wait(TimeSpan.FromSeconds(5));
                }, out _));
                try
                {
                    Assert.IsTrue(enteredReplay.Wait(TimeSpan.FromSeconds(5)), "another thread owns the state-replay drain");
                    int tickThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    observations = Slots(Slot(WinLossState.None));
                    service.TestTick(2);
                    Assert.AreEqual(1, transitions, "transitions remain synchronous even while replay delivery is blocked");
                    Assert.AreEqual(tickThread, transitionThread, "state replay cannot drain simulation callbacks on its own thread");
                    Assert.IsTrue(capability.TryGetObservedState(131, 1, out var current) && current.IsEliminated);
                }
                finally
                {
                    releaseReplay.Set();
                    Assert.IsTrue(replay.Wait(TimeSpan.FromSeconds(5)));
                }
            }
        }

        private static PlayerDefeatObservation Living(int unitId, int globalId) =>
            new PlayerDefeatObservation(true, WinLossState.None, true, unitId, globalId);

        private static PlayerDefeatObservation Slot(WinLossState state) =>
            new PlayerDefeatObservation(true, state, false, 0, 0);

        private static PlayerDefeatObservation[] Slots(params PlayerDefeatObservation[] values)
        {
            var result = new PlayerDefeatObservation[8];
            for (int index = 0; index < result.Length; index++)
                result[index] = index < values.Length
                    ? values[index]
                    : new PlayerDefeatObservation(false, WinLossState.None, false, 0, 0);
            return result;
        }

        private static void Check(bool condition, string message)
        {
            Assert.IsTrue(condition, message);
        }
    }
}
