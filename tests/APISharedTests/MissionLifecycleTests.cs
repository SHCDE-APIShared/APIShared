using APIShared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    [TestCategory("MissionLifecycle")]
    public sealed class MissionLifecycleTests
    {
        [TestMethod]
        public void MultiplayerRestartOnlyIgnoresRotationsRewrittenByVanilla()
        {
            var setup = new EngineInterface.MultiplayerSetupData();
            var overwrittenSlots = new[] { 2 };
            string source = MissionLifecycleService.MultiplayerRestartSetupIdentity(setup, overwrittenSlots);
            string coop = MissionLifecycleService.MultiplayerRestartSetupIdentity(setup, Array.Empty<int>());

            setup.preferredAIVs[1] = -3;
            Assert.AreEqual(source, MissionLifecycleService.MultiplayerRestartSetupIdentity(setup, overwrittenSlots));
            Assert.AreNotEqual(coop, MissionLifecycleService.MultiplayerRestartSetupIdentity(setup, Array.Empty<int>()));
            setup.preferredAIVs[0] = -2;
            Assert.AreNotEqual(source, MissionLifecycleService.MultiplayerRestartSetupIdentity(setup, overwrittenSlots));
            setup.preferredAIVs[0] = 0;
            setup.start_keep_location_order[1] = 3;
            Assert.AreNotEqual(source, MissionLifecycleService.MultiplayerRestartSetupIdentity(setup, overwrittenSlots));
        }

        [TestMethod]
        public void NativeAdmissionAbortUnwindsTheWholeStartAndDoesNotHideUnrelatedExceptions()
        {
            int originalCalls = 0, postLoadCalls = 0;
            int result = MissionLifecycleService.InvokeNativeAdmission<int>(() => {
                originalCalls++;
                Action nativeLoad = () => throw new MissionLifecycleService.NativeAdmissionAbort("changed file");
                nativeLoad();
                postLoadCalls++; return 1;
            }, out bool normal);
            Assert.IsFalse(normal); Assert.AreEqual(0, result);
            Assert.AreEqual(1, originalCalls); Assert.AreEqual(0, postLoadCalls);
            Assert.AreEqual(7, MissionLifecycleService.InvokeNativeAdmission(() => 7, out normal));
            Assert.IsTrue(normal);
            Assert.ThrowsExactly<InvalidOperationException>(() => MissionLifecycleService.InvokeNativeAdmission<int>(
                () => throw new InvalidOperationException("original failed"), out _));
        }
        private static MissionContext Seed(MissionStartKind kind) => new MissionContext(0, kind, default,
            kind == MissionStartKind.EditorCreated ? null : "same.map", "same map", false, 160, 1, 1);

        [TestMethod]
        public void ReplacementPreservesSessionIdentityAndTransitionOrder()
        {
            foreach (MissionStartKind first in Enum.GetValues(typeof(MissionStartKind)))
            foreach (MissionStartKind next in Enum.GetValues(typeof(MissionStartKind)))
            {
                var events = new List<MissionLifecycleNotification>();
                var state = new MissionLifecycleState(_ => { });
                state.Register("mod", "observer", events.Add);
                long a = state.Begin(Seed(first));
                state.Checkpoint(a, Seed(first), MissionInitializationPhase.BeforeLoad);
                state.Checkpoint(a, Seed(first), MissionInitializationPhase.BeforeLoad);
                state.Finish(a, Seed(first), true, true, true, false);
                state.Ready(a, Seed(first));
                long b = state.Begin(Seed(next));
                Assert.IsTrue(state.Current == null, "replacement retained an old ready session");
                state.Finish(a, Seed(first), true, true, true, false);
                state.Finish(b, Seed(next), true, true, true, false);
                state.End(MissionEndReason.SceneChanged);
                state.End(MissionEndReason.Unloaded);
                Assert.IsTrue(events.Select(e => e.Kind).SequenceEqual(new[] { MissionLifecycleKind.Initialization,
                    MissionLifecycleKind.Start, MissionLifecycleKind.End, MissionLifecycleKind.Start, MissionLifecycleKind.End }),
                    "duplicate/reordered transition for " + first + " -> " + next);
                Assert.IsTrue(events[2].Context.SessionId == a && events[2].Context.StartKind == first &&
                    events[3].Context.SessionId == b && b > a && events[4].Context.StartKind == next,
                    "End lost its original context or same-size/player replacement reused an identity");
            }
        }

        [TestMethod]
        public void InitializationRequiresCompletionAndWaitsOnlyForDelayedClients()
        {
            foreach (bool normal in new[] { true, false })
            foreach (bool native in new[] { true, false })
            foreach (bool managed in new[] { true, false })
            foreach (bool client in new[] { true, false })
            {
                var events = new List<MissionLifecycleNotification>();
                var state = new MissionLifecycleState(_ => { });
                state.Register("matrix", "observer", events.Add);
                long id = state.Begin(Seed(MissionStartKind.LoadedSave));
                bool waiting = state.Finish(id, Seed(MissionStartKind.LoadedSave), normal, native, managed, client);
                Assert.IsTrue(waiting == (normal && native && !managed && client), "incorrect delayed-client decision");
                Assert.IsTrue((state.Current != null) == (normal && native && managed), "Ready published for failed/incomplete initialization");
                if (!waiting && state.Current == null)
                    Assert.IsTrue(events.Count == 1 && events[0].Kind == MissionLifecycleKind.End && !events[0].HasStarted &&
                        events[0].EndReason == (normal ? MissionEndReason.Failed : MissionEndReason.Exception),
                        "failed attempt did not end exactly once with its failure reason");
                if (waiting)
                {
                    state.Finish(id, Seed(MissionStartKind.LoadedSave), true, true, true, true);
                    state.Finish(id, Seed(MissionStartKind.LoadedSave), true, true, true, true);
                    Assert.IsTrue(events.Count == 1 && events[0].Kind == MissionLifecycleKind.Start, "client completion was missing or duplicated");
                }
            }
        }

        [TestMethod]
        public void LateObserversReplayOnlyTheActiveSessionAndRejectDuplicateIds()
        {
            var replayState = new MissionLifecycleState(_ => { });
            long readyId = replayState.Begin(Seed(MissionStartKind.EditorLoaded));
            replayState.Ready(readyId, Seed(MissionStartKind.EditorLoaded));
            var replay = new List<MissionLifecycleNotification>();
            Assert.IsTrue(replayState.Register("owner", "stable", replay.Add), "valid registration rejected");
            Assert.IsTrue(!replayState.Register("owner", "stable", replay.Add), "duplicate owner-local ID accepted");
            Assert.IsTrue(replay.Count == 1 && replay[0].IsReplay && replay[0].Context.SessionId == readyId,
                "late observer did not receive exactly one ready replay");
            replayState.End(MissionEndReason.Unloaded);
            replayState.Register("late", "after-end", replay.Add);
            Assert.IsTrue(replay.Count == 2 && replay[1].Kind == MissionLifecycleKind.End, "ended session was replayed");
        }

        [TestMethod]
        public void ObserverAndLoggerFailuresDoNotStarveOtherObservers()
        {
            int failures = 0, survivors = 0;
            var isolated = new MissionLifecycleState(_ => { failures++; throw new Exception("logger"); });
            isolated.Register("bad", "one", _ => throw new Exception("observer"));
            isolated.Register("good", "one", _ => survivors++);
            long isolationId = isolated.Begin(Seed(MissionStartKind.NewGame));
            isolated.Ready(isolationId, Seed(MissionStartKind.NewGame));
            isolated.End(MissionEndReason.Unloaded);
            Assert.IsTrue(failures == 2 && survivors == 2, "observer/logger failure escaped or starved another observer");
        }

        [TestMethod]
        public void ReentrantEndIsDeliveredAfterEveryStartObserver()
        {
            var order = new List<string>();
            var reentrant = new MissionLifecycleState(_ => { });
            reentrant.Register("first", "one", e =>
            {
                order.Add("first:" + e.Kind);
                if (e.Kind == MissionLifecycleKind.Start) reentrant.End(MissionEndReason.Replaced);
            });
            reentrant.Register("second", "one", e => order.Add("second:" + e.Kind));
            long reentrantId = reentrant.Begin(Seed(MissionStartKind.NewGame));
            reentrant.Ready(reentrantId, Seed(MissionStartKind.NewGame));
            Assert.IsTrue(order.SequenceEqual(new[] { "first:Start", "second:Start", "first:End", "second:End" }),
                "reentrant End overtook Start for another observer");
        }

        [TestMethod]
        public void ExecutingSessionRequiresOwnedScopePendingAttemptAndCallingThread()
        {
            var failures = new List<Exception>();
            var state = new MissionLifecycleState(failures.Add);
            var execution = new MissionExecutionSession();
            long id = state.Begin(Seed(MissionStartKind.LoadedSave));
            Assert.AreEqual(0L, execution.Read(state, true, id), "pending alone is not execution");
            var previous = execution.Enter(state);
            try
            {
                Assert.AreEqual(id, execution.Read(state, true, id));
                Assert.AreEqual(0L, execution.Read(state, true, 0), "unassigned operation borrowed an earlier pending attempt");
                Assert.AreEqual(0L, execution.Read(state, false, id), "scope alone is not execution");
                Assert.AreEqual(0L, execution.Read(state, true, id + 1), "unrelated operation borrowed pending identity");
                long foreign = -1, foreignEntry = -1;
                var reader = new Thread(() =>
                {
                    foreign = execution.Read(state, true, id);
                    var foreignScope = execution.Enter(state);
                    try { foreignEntry = execution.Read(state, true, id); }
                    finally { execution.Restore(foreignScope); }
                });
                reader.Start(); reader.Join();
                Assert.AreEqual(0L, foreign, "foreign archive caller borrowed another thread's attempt");
                Assert.AreEqual(0L, foreignEntry, "foreign entry ignored the service creator thread");
                var nested = execution.Enter(state);
                try { throw new InvalidOperationException("nested callback failed"); }
                catch (InvalidOperationException) { }
                finally { execution.Restore(nested); }
                Assert.AreEqual(id, execution.Read(state, true, id), "nested failure lost outer execution");
                state.Ready(id, Seed(MissionStartKind.LoadedSave));
                Assert.AreEqual(0L, execution.Read(state, true, id), "ready context is not executing pending identity");
            }
            finally { execution.Restore(previous); }
            Assert.AreEqual(0L, execution.Read(state, true, id), "completed scope leaked execution");
            Assert.AreEqual(0, failures.Count, "lifecycle observer assertion failed");
        }

        [TestMethod]
        public void ExecutingSessionReadsPendingReplacementBeforeBeginReturnsItsId()
        {
            var failures = new List<Exception>();
            var state = new MissionLifecycleState(failures.Add);
            var execution = new MissionExecutionSession();
            long a = state.Begin(Seed(MissionStartKind.NewGame));
            state.Ready(a, Seed(MissionStartKind.NewGame));
            long operationId = 0, observed = 0;
            state.Register("owner", "replacement", e =>
            {
                if (e.Kind == MissionLifecycleKind.End)
                {
                    Assert.AreEqual(0L, operationId, "fixture must observe Begin before its return assignment");
                    observed = execution.Read(state, true, operationId);
                    Assert.AreEqual(e.ReplacementContext.SessionId, observed);
                    Assert.IsNull(state.Current);
                }
            });
            var previous = execution.Enter(state);
            try
            {
                operationId = state.Begin(Seed(MissionStartKind.LoadedSave));
                Assert.AreEqual(operationId, observed);
                Assert.AreEqual(operationId, execution.Read(state, true, operationId));
            }
            finally { execution.Restore(previous); }
            Assert.AreEqual(0, failures.Count, "lifecycle observer assertion failed");
        }

        [TestMethod]
        public void ExecutingSessionAssociatesReentrantLoadBeforeQueuedNotificationsAndDeferredResume()
        {
            foreach (int outcome in new[] { 0, 1, 2 }) // ready, failed, deferred client
            {
                var failures = new List<Exception>();
                var state = new MissionLifecycleState(failures.Add);
                var execution = new MissionExecutionSession();
                long a = state.Begin(Seed(MissionStartKind.NewGame));
                long b = 0, preparedSession = 0;
                bool beforeBDelivered = false;
                var ends = new List<long>();
                state.Register("first", "starts-load", e =>
                {
                    if (e.Kind != MissionLifecycleKind.Start || e.Context.SessionId != a) return;
                    var previous = execution.Enter(state);
                    try
                    {
                        b = state.Begin(Seed(MissionStartKind.LoadedSave));
                        state.Checkpoint(b, Seed(MissionStartKind.LoadedSave), MissionInitializationPhase.BeforeLoad);
                        Assert.IsFalse(beforeBDelivered, "reentrant lifecycle fixture stopped queuing");
                        preparedSession = execution.Read(state, true, b);
                        state.Finish(b, Seed(MissionStartKind.LoadedSave), true, outcome != 1,
                            outcome == 0, outcome == 2);
                    }
                    finally { execution.Restore(previous); }
                });
                state.Register("second", "consumer", e =>
                {
                    if (e.Kind == MissionLifecycleKind.Initialization && e.Context.SessionId == b)
                        beforeBDelivered = true;
                    if (e.Kind == MissionLifecycleKind.End) ends.Add(e.Context.SessionId);
                });
                state.Ready(a, Seed(MissionStartKind.NewGame));
                Assert.AreEqual(b, preparedSession, "early archive callback inherited the retiring session");
                Assert.IsTrue(beforeBDelivered);
                Assert.IsTrue(ends.Contains(a));
                Assert.AreEqual(outcome == 1, ends.Contains(preparedSession), "exact failure identity was lost");
                Assert.AreEqual(0L, execution.Read(state, true, b), "returned operation remained executing");
                if (outcome == 2)
                {
                    Assert.IsNull(state.Current);
                    var resume = execution.Enter(state);
                    try
                    {
                        Assert.AreEqual(b, execution.Read(state, true, b), "deferred message lost its waiting attempt");
                        state.Ready(b, Seed(MissionStartKind.LoadedSave));
                    }
                    finally { execution.Restore(resume); }
                }
                Assert.AreEqual(outcome == 1 ? 0L : b, state.Current?.SessionId ?? 0);
                Assert.AreEqual(0, failures.Count, "lifecycle observer assertion failed");
            }
        }

        [TestMethod]
        public void ExecutingSessionSeparatesPendingReplacementFromDelayedTerminalEnd()
        {
            var failures = new List<Exception>();
            var state = new MissionLifecycleState(failures.Add);
            var execution = new MissionExecutionSession();
            long a = state.Begin(Seed(MissionStartKind.NewGame));
            state.Ready(a, Seed(MissionStartKind.NewGame));
            long b = 0, preparedSession = 0, delayedEnd = 0;
            bool beforeBDelivered = false;
            state.Register("first", "end-starts-load", e =>
            {
                if (e.Kind != MissionLifecycleKind.End || e.Context.SessionId != a) return;
                var previous = execution.Enter(state);
                try
                {
                    b = state.Begin(Seed(MissionStartKind.LoadedSave));
                    state.Checkpoint(b, Seed(MissionStartKind.LoadedSave), MissionInitializationPhase.BeforeLoad);
                    preparedSession = execution.Read(state, true, b);
                    Assert.IsFalse(beforeBDelivered);
                    Assert.IsTrue(state.Finish(b, Seed(MissionStartKind.LoadedSave), true, true, false, true));
                }
                finally { execution.Restore(previous); }
            });
            state.Register("second", "consumer", e =>
            {
                if (e.Kind == MissionLifecycleKind.Initialization && e.Context.SessionId == b) beforeBDelivered = true;
                if (e.Kind == MissionLifecycleKind.End) delayedEnd = e.Context.SessionId;
            });
            state.End(MissionEndReason.SceneChanged, a);
            Assert.IsNull(state.Current, "deferred B must remain indistinguishable through Current alone");
            Assert.IsTrue(state.IsPending(b));
            Assert.AreEqual(b, preparedSession);
            Assert.AreEqual(a, delayedEnd);
            Assert.AreNotEqual(preparedSession, delayedEnd, "late terminal End(A) was associated with B's archive");
            state.End(MissionEndReason.Failed, b);
            Assert.AreEqual(preparedSession, delayedEnd, "terminal End(B) could not identify its own prepared archive");
            Assert.AreEqual(0, failures.Count, "lifecycle observer assertion failed");
        }

        [TestMethod]
        public void RestartOutsideReadyGameplayKeepsVanillaInsteadOfThrowing()
        {
            var state = new MissionLifecycleState(_ => { });
            var gameplay = Seed(MissionStartKind.LoadedSave);
            long session = state.Begin(gameplay);
            state.Ready(session, gameplay);
            MissionContext current = state.Current;
            Assert.IsTrue(MissionLifecycleService.IsLifecycleRestart(false, current, true));
            Assert.IsFalse(MissionLifecycleService.IsLifecycleRestart(true, current, true), "running attempt");
            Assert.IsFalse(MissionLifecycleService.IsLifecycleRestart(false, current, false), "foreign thread");
            Assert.IsFalse(MissionLifecycleService.IsLifecycleRestart(false, null, true), "no session");
            var editorState = new MissionLifecycleState(_ => { });
            var editor = Seed(MissionStartKind.EditorLoaded);
            long editorSession = editorState.Begin(editor);
            editorState.Ready(editorSession, editor);
            Assert.IsFalse(MissionLifecycleService.IsLifecycleRestart(false, editorState.Current, true), "editor");

            Assert.AreEqual(MissionLifecycleService.ConfirmedRestart.Lifecycle,
                MissionLifecycleService.DecideConfirmedRestart(current.SessionId, current, true));
            Assert.AreEqual(MissionLifecycleService.ConfirmedRestart.Stale,
                MissionLifecycleService.DecideConfirmedRestart(current.SessionId + 1, current, true));
            // "Yes" without a tracked gameplay mission must still restart through Vanilla.
            Assert.AreEqual(MissionLifecycleService.ConfirmedRestart.Vanilla,
                MissionLifecycleService.DecideConfirmedRestart(null, current, true));
            Assert.AreEqual(MissionLifecycleService.ConfirmedRestart.Vanilla,
                MissionLifecycleService.DecideConfirmedRestart(current.SessionId, null, true));
            Assert.AreEqual(MissionLifecycleService.ConfirmedRestart.Vanilla,
                MissionLifecycleService.DecideConfirmedRestart(editorState.Current.SessionId, editorState.Current, true));
            Assert.AreEqual(MissionLifecycleService.ConfirmedRestart.Vanilla,
                MissionLifecycleService.DecideConfirmedRestart(current.SessionId, current, false));
        }

        [TestMethod]
        public void ReplacementCarriesImmutableRestartOwnershipBeforeCleanup()
        {
            var events = new List<MissionLifecycleNotification>();
            var state = new MissionLifecycleState(_ => { });
            state.Register("settings", "ownership", events.Add);
            var original = Seed(MissionStartKind.LoadedSave);
            long source = state.Begin(original);
            state.Ready(source, original);
            var restart = new MissionContext(0, MissionStartKind.NewGame, default,
                "same.map", "same map", true).AsRestartOf(source);
            long destination = state.Begin(restart);
            var end = events.Last();
            Assert.AreEqual(MissionEndReason.Replaced, end.EndReason);
            Assert.AreEqual(source, end.ReplacementContext.RestartedFromSessionId);
            Assert.AreEqual(destination, end.ReplacementContext.SessionId);
            Assert.IsFalse(end.ReplacementContext.IsSave);
            state.Ready(destination, restart);
            Assert.AreEqual(source, state.Current.RestartedFromSessionId);
            state.End(MissionEndReason.SceneChanged);
            Assert.IsNull(events.Last().ReplacementContext);
            Assert.AreEqual(destination, end.ReplacementContext.SessionId, "retained notification changed after completion");
        }

        [TestMethod]
        public void StaleClientCompletionAndFailureCannotReplaceTheCurrentSession()
        {
            var replaced = new MissionLifecycleState(_ => { });
            long stale = replaced.Begin(Seed(MissionStartKind.LoadedSave));
            Assert.IsTrue(replaced.Finish(stale, Seed(MissionStartKind.LoadedSave), true, true, false, true), "client did not wait");
            long current = replaced.Begin(Seed(MissionStartKind.NewGame));
            replaced.Ready(stale, Seed(MissionStartKind.LoadedSave));
            Assert.IsTrue(replaced.Current == null && replaced.IsPending(current), "obsolete client completion activated a replaced mission");
            replaced.End(MissionEndReason.Exception, stale);
            Assert.IsTrue(replaced.IsPending(current), "obsolete failure ended a newer attempt");
        }
    }
}
