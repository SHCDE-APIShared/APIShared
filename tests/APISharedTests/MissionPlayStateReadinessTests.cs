using System;
using System.Collections.Generic;
using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    [TestCategory("MissionLifecycle")]
    public sealed class MissionPlayStateReadinessTests
    {
        private sealed class Fixture
        {
            internal bool Unity = true;
            internal long CurrentSession = 1;
            internal int Requests, Errors;
            internal readonly object Old = new object(), Fresh = new object();
            internal readonly List<MissionPlayStateReadyNotification> Received = new List<MissionPlayStateReadyNotification>();
            internal readonly MissionPlayStateReadinessState State;
            internal Fixture()
            {
                State = new MissionPlayStateReadinessState(() => Requests++, _ => Errors++,
                    () => Unity, session => CurrentSession == session);
                Assert.IsTrue(State.Register("consumer", "ready", Received.Add));
            }
            internal void Notify(MissionLifecycleKind kind, object snapshot, long session = 1,
                MissionInitializationPhase phase = MissionInitializationPhase.BeforeLoad)
            {
                var context = new MissionContext(session, MissionStartKind.NewGame, default, "map", "map", false);
                State.Handle(new MissionLifecycleNotification(context, kind, phase,
                    kind == MissionLifecycleKind.End ? MissionEndReason.Unloaded : MissionEndReason.None,
                    kind == MissionLifecycleKind.Start), snapshot);
            }
            internal void Begin()
            {
                Notify(MissionLifecycleKind.Initialization, Old);
                Notify(MissionLifecycleKind.Start, Old);
            }
        }

        [DataTestMethod]
        [DataRow(16)]
        [DataRow(35)]
        [DataRow(90)]
        public void SlowNetworkOrPausedPresentationNeverExpiresPendingReadiness(int seconds)
        {
            var f = new Fixture(); f.Begin();
            // 60 presentation opportunities per simulated second; elapsed wall time is deliberately
            // not a production input. A later state must still work after every former deadline.
            for (int frame = 0; frame < seconds * 60; frame++) f.State.Pump(f.Old);
            Assert.IsTrue(f.State.Pending); Assert.IsNull(f.State.Current); Assert.AreEqual(0, f.Received.Count);
            f.State.Pump(f.Fresh); f.State.Pump(new object());
            Assert.AreEqual(1, f.Received.Count); Assert.IsFalse(f.Received[0].IsReplay);
            Assert.AreEqual(1L, f.State.Current.SessionId);
        }

        [TestMethod]
        public void FirstStateBeforeManagedStartAndLaterPhasesPreserveEarliestReference()
        {
            var f = new Fixture();
            f.Notify(MissionLifecycleKind.Initialization, f.Old);
            f.Notify(MissionLifecycleKind.Initialization, f.Fresh, phase: MissionInitializationPhase.BeforeNativeStart);
            f.State.Pump(f.Fresh); Assert.AreEqual(0, f.Received.Count);
            f.Notify(MissionLifecycleKind.Start, f.Fresh);
            f.State.Pump(f.Fresh);
            Assert.AreEqual(1, f.Received.Count);
        }

        [TestMethod]
        public void NullAndPreviousMissionStatesCannotAuthorizeReadiness()
        {
            var f = new Fixture(); f.Begin();
            f.State.Pump(null); f.State.Pump(f.Old);
            Assert.IsNull(f.State.Current); Assert.AreEqual(0, f.Received.Count);
        }

        [TestMethod]
        public void EndAndReplacementInvalidateImmediatelyAndRejectLateOldNotifications()
        {
            var f = new Fixture(); f.Begin(); f.State.Pump(f.Fresh);
            f.CurrentSession = 2; // Lifecycle identity changes before its observer FIFO reaches us.
            Assert.IsNull(f.State.Current);
            f.Notify(MissionLifecycleKind.End, f.Fresh);
            f.Notify(MissionLifecycleKind.Start, f.Fresh); f.State.Pump(new object());
            Assert.IsNull(f.State.Current); Assert.AreEqual(1, f.Received.Count);
            f.Notify(MissionLifecycleKind.Initialization, f.Fresh, 2);
            f.Notify(MissionLifecycleKind.Start, f.Fresh, 2); f.State.Pump(f.Fresh);
            Assert.AreEqual(1, f.Received.Count);
            f.State.Pump(new object()); Assert.AreEqual(2, f.Received.Count);
            f.Notify(MissionLifecycleKind.End, null, 1);
            Assert.AreEqual(2L, f.State.Current.SessionId);
        }

        [TestMethod]
        public void LifecyclePublisherCannotDeliverAndLateRegistrationReplaysOnUnity()
        {
            var f = new Fixture(); f.Unity = false; f.Begin();
            f.State.Pump(f.Fresh);
            Assert.AreEqual(0, f.Received.Count); Assert.IsTrue(f.Requests > 0);
            Assert.IsFalse(f.State.Register("worker", "invalid", _ => Assert.Fail()));
            f.Unity = true; f.State.Pump(f.Fresh);
            var replay = new List<MissionPlayStateReadyNotification>();
            Assert.IsTrue(f.State.Register("late", "ready", replay.Add)); f.State.Pump(f.Fresh);
            Assert.AreEqual(1, replay.Count); Assert.IsTrue(replay[0].IsReplay);
            Assert.IsFalse(f.State.Register("late", "ready", replay.Add));
        }

        [TestMethod]
        public void ReentrantRegistrationQueuesReplayAndObserverFailuresAreIsolated()
        {
            var f = new Fixture(); var order = new List<string>();
            f.State.Register("a", "first", _ => {
                order.Add("first");
                f.State.Register("b", "late", notification => { Assert.IsTrue(notification.IsReplay); order.Add("late"); });
                f.State.Pump(f.Fresh); order.Add("first-end");
                throw new InvalidOperationException("isolated");
            });
            f.Begin(); f.State.Pump(f.Fresh);
            CollectionAssert.AreEqual(new[] { "first", "first-end", "late" }, order);
            Assert.AreEqual(1, f.Errors); Assert.AreEqual(1, f.Received.Count);
        }

        [TestMethod]
        public void ObserverReplacingSessionStopsOldPublicationBeforeRemainingCallbacks()
        {
            var f = new Fixture();
            f.State.Register("a", "replace", _ => f.CurrentSession = 2);
            f.Begin(); f.State.Pump(f.Fresh);
            Assert.AreEqual(0, f.Received.Count); Assert.IsNull(f.State.Current);
        }
    }
}
