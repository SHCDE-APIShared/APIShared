using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using APIShared;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class MultiplayerSavePreparationTests
    {
        private static bool ChoiceStatePrefix(ref SavegameLoadChoiceState __result)
        { __result = SavegameLoadChoiceState.Legacy; return false; }
        private static bool CaptureSettingsPrefix(ref Dictionary<string, Dictionary<string, byte[]>> __result)
        { __result = new Dictionary<string, Dictionary<string, byte[]>>(); return false; }
        private const string Operation = "01234567890123456789012345678901";
        private static MultiplayerSavePreparationSnapshot Snapshot(string operation = Operation, ulong[] peers = null)
            => new MultiplayerSavePreparationSnapshot(operation, 0, 10, 1, true, peers ?? new ulong[] { 1, 2 },
                "MySave", 123, 456, @"C:\save.sav", "local-only-hash", MultiplayerSavePreparationPhase.Preparing);

        [TestMethod]
        public void ImmutableProvenanceTransitionsEndOnceAndRetainLocalFileIdentity()
        {
            var errors = new List<Exception>(); var state = new MultiplayerSavePreparationState(errors.Add);
            var events = new List<MultiplayerSavePreparationSnapshot>();
            Assert.IsTrue(state.TryRegister("Author", "Observe", events.Add, out _));
            ulong[] peers = { 1, 2 }; var source = Snapshot(peers: peers); peers[0] = 99;
            Assert.IsTrue(state.Begin(source)); var first = state.Current;
            CollectionAssert.AreEqual(new ulong[] { 1, 2 }, first.HumanPeers.ToArray());
            Assert.ThrowsExactly<NotSupportedException>(() => ((IList<ulong>)first.HumanPeers)[0] = 99);
            Assert.IsTrue(state.Advance(Operation, MultiplayerSavePreparationPhase.Released));
            Assert.IsTrue(state.Advance(Operation, MultiplayerSavePreparationPhase.Released));
            Assert.IsTrue(state.Advance(Operation, MultiplayerSavePreparationPhase.NativeLoading));
            Assert.AreEqual(MultiplayerSavePreparationPhase.Preparing, first.Phase);
            state.End(Operation, MultiplayerSavePreparationEndReason.ScopeFinished, null);
            state.End(Operation, MultiplayerSavePreparationEndReason.Rejected, "late");
            Assert.IsNull(state.Current); Assert.AreEqual(4, events.Count);
            Assert.AreEqual(MultiplayerSavePreparationEndReason.ScopeFinished, events.Last().EndReason);
            Assert.IsTrue(events.Zip(events.Skip(1), (a, b) => a.Revision < b.Revision).All(value => value));
            Assert.IsTrue(events.All(value => value.LocalPath == source.LocalPath && value.LocalHash == source.LocalHash &&
                value.SaveName == source.SaveName && value.Checksum == 123 && value.Time == 456 && value.LobbyId == 10 && value.HostId == 1));
            Assert.AreEqual(0, errors.Count);
        }

        [TestMethod]
        public void ObserversAreOrderedIsolatedAndOwnerBoundWithOptionalReplay()
        {
            int errors = 0; var state = new MultiplayerSavePreparationState(_ => errors++); var calls = new List<string>();
            Assert.IsFalse(state.TryRegister("", "id", _ => { }, out _));
            Assert.IsTrue(state.TryRegister("Z", "id", _ => calls.Add("last"), out _, order: 3));
            Assert.IsTrue(state.TryRegister("A", "id", _ => { calls.Add("first"); throw new Exception(); }, out _));
            Assert.IsTrue(state.TryRegister("B", "id", _ => calls.Add("middle"), out _));
            Assert.IsFalse(state.TryRegister("A", "id", _ => { }, out _));
            state.Begin(Snapshot()); CollectionAssert.AreEqual(new[] { "first", "middle", "last" }, calls);
            Assert.AreEqual(1, errors);
            int replay = 0; Assert.IsTrue(state.TryRegister("Replay", "id", _ => replay++, out _)); Assert.AreEqual(1, replay);
            Assert.IsTrue(state.TryRegister("Silent", "id", _ => replay++, out _, replay: false)); Assert.AreEqual(1, replay);
        }

        [TestMethod]
        public void ReentrantContinuationsRunOutsideNotificationsAndLaterPhaseObserversStaySynchronous()
        {
            var errors = new List<Exception>(); var state = new MultiplayerSavePreparationState(errors.Add);
            var events = new List<string>();
            const string next = "11234567890123456789012345678901";
            state.TryRegister("A", "Observe", snapshot => {
                events.Add(snapshot.OperationId == Operation ? "A:" + snapshot.Phase : "B:" + snapshot.Phase);
                if (snapshot.OperationId == Operation && snapshot.Phase == MultiplayerSavePreparationPhase.Preparing)
                {
                    state.End(Operation, MultiplayerSavePreparationEndReason.Cancelled, null);
                    state.Begin(Snapshot(next), () => {
                        events.Add("B:continue safe=" + state.CanDeliverSynchronously);
                        state.Advance(next, MultiplayerSavePreparationPhase.Released); events.Add("B:after release");
                        state.Advance(next, MultiplayerSavePreparationPhase.NativeLoading); events.Add("B:after native admission");
                    });
                }
            }, out _);
            state.Begin(Snapshot(), () => events.Add("stale A continuation"));
            CollectionAssert.AreEqual(new[] { "A:Preparing", "A:Ended", "B:Preparing", "B:continue safe=True",
                "B:Released", "B:after release", "B:NativeLoading", "B:after native admission" }, events);
            Assert.AreEqual(0, errors.Count);
        }

        [TestMethod]
        public void CancelledQueuedContinuationIsSkippedAndContinuationExceptionsDoNotEscapeOlderCall()
        {
            var errors = new List<Exception>(); var state = new MultiplayerSavePreparationState(errors.Add);
            int continued = 0;
            state.TryRegister("A", "Cancel", snapshot => {
                if (snapshot.Phase == MultiplayerSavePreparationPhase.Preparing)
                    state.End(snapshot.OperationId, MultiplayerSavePreparationEndReason.Cancelled, null);
            }, out _);
            state.Begin(Snapshot(), () => continued++);
            Assert.AreEqual(0, continued); Assert.IsNull(state.Current);
            var isolated = new MultiplayerSavePreparationState(errors.Add);
            isolated.Begin(Snapshot(), () => { throw new InvalidOperationException("continuation failure"); });
            Assert.AreEqual(1, errors.Count); Assert.AreEqual("continuation failure", errors[0].Message);
        }

        [TestMethod]
        public void ReentrantTerminalClearsCurrentAndCannotRetireReplacement()
        {
            var state = new MultiplayerSavePreparationState(_ => { }); var events = new List<string>();
            bool clearedBeforeTerminalObserver = false;
            string currentDuringQueuedEnd = null;
            string next = "11234567890123456789012345678901";
            state.TryRegister("Author", "first", snapshot => {
                events.Add("first:" + snapshot.Phase);
                if (snapshot.Phase == MultiplayerSavePreparationPhase.Ended)
                    currentDuringQueuedEnd = state.Current?.OperationId;
                if (snapshot.Phase == MultiplayerSavePreparationPhase.Preparing && snapshot.OperationId == Operation)
                {
                    state.End(Operation, MultiplayerSavePreparationEndReason.Cancelled, "cancel");
                    clearedBeforeTerminalObserver = state.Current == null;
                    state.Begin(Snapshot(next));
                }
            }, out _);
            state.TryRegister("Author", "second", snapshot => events.Add("second:" + snapshot.Phase), out _);
            Assert.IsFalse(state.Begin(Snapshot())); Assert.AreEqual(next, state.Current.OperationId);
            Assert.IsTrue(clearedBeforeTerminalObserver);
            Assert.AreEqual(next, currentDuringQueuedEnd, "Queued Ended must not be confused with the newer Current operation.");
            CollectionAssert.AreEqual(new[] { "first:Preparing", "second:Preparing", "first:Ended", "second:Ended", "first:Preparing", "second:Preparing" }, events);
            state.End(Operation, MultiplayerSavePreparationEndReason.Timeout, "late");
            Assert.AreEqual(next, state.Current.OperationId);
            Assert.IsFalse(state.Advance(Operation, MultiplayerSavePreparationPhase.Released));
            Assert.ThrowsExactly<InvalidOperationException>(() => state.Begin(Snapshot()));
            state.Advance(next, MultiplayerSavePreparationPhase.NativeLoading);
            Assert.ThrowsExactly<InvalidOperationException>(() => state.Advance(next, MultiplayerSavePreparationPhase.Preparing));
        }

        [TestMethod]
        public void CoordinatorPublishesActualValidatedFieldsAndRechecksAfterObserverReentry()
        {
            // Execute the coordinator's actual publication boundary without opening Unity,
            // reading Steam, or replacing its implementation with a fixture model.
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            string[] fields = { "pending", "preparingDelivered", "hostId", "attemptHost", "localSavePath", "localSaveHash" };
            var previous = fields.ToDictionary(name => name, name => typeof(SavegameLoadCoordinator).GetField(name, flags).GetValue(null));
            bool enabled = true, replace = false, replaceNative = false;
            var state = MultiplayerSavePreparation.State;
            Assert.IsNull(state.Current, "Fixture requires no active live preparation.");
            string observer = Guid.NewGuid().ToString("N");
            state.TryRegister("Fixture.SavePreparation", observer, snapshot => {
                if (enabled && replace && snapshot.Phase == MultiplayerSavePreparationPhase.Preparing)
                {
                    typeof(SavegameLoadCoordinator).GetField("pending", flags).SetValue(null, null);
                    state.End(snapshot.OperationId, MultiplayerSavePreparationEndReason.Cancelled, "observer cancellation");
                }
                if (enabled && replaceNative && snapshot.Phase == MultiplayerSavePreparationPhase.NativeLoading)
                {
                    state.End(snapshot.OperationId, MultiplayerSavePreparationEndReason.ScopeFinished, null);
                    string next = "11234567890123456789012345678901";
                    typeof(SavegameLoadCoordinator).GetField("pending", flags).SetValue(null, new SavegameLoadPacket { Token = next });
                    state.Begin(Snapshot(next));
                }
            }, out _, replay: false);
            Action<string, object> set = (name, value) => typeof(SavegameLoadCoordinator).GetField(name, flags).SetValue(null, value);
            var publish = typeof(SavegameLoadCoordinator).GetMethod("PublishPreparingCore", flags);
            var advance = typeof(SavegameLoadCoordinator).GetMethod("PublishPhase", flags);
            try
            {
                set("pending", new SavegameLoadPacket { Token = Operation, Lobby = 10, SaveName = "MySave", Checksum = 123, Time = 456 });
                set("hostId", 1UL); set("attemptHost", true); set("localSavePath", @"C:\save.sav"); set("localSaveHash", "local-only-hash");
                Assert.AreEqual(true, publish.Invoke(null, new object[] { new ulong[] { 1, 2 }, null }));
                Assert.AreEqual(Operation, state.Current.OperationId); Assert.AreEqual("local-only-hash", state.Current.LocalHash);
                Assert.AreEqual(true, advance.Invoke(null, new object[] { MultiplayerSavePreparationPhase.Released }));
                Assert.AreEqual(MultiplayerSavePreparationPhase.Released, state.Current.Phase);
                replaceNative = true;
                Assert.AreEqual(false, advance.Invoke(null, new object[] { MultiplayerSavePreparationPhase.NativeLoading }));
                Assert.AreEqual("11234567890123456789012345678901", state.Current.OperationId,
                    "Old native admission retired the replacement preparation.");
                Assert.AreEqual(MultiplayerSavePreparationPhase.Preparing, state.Current.Phase);
                replaceNative = false; state.End(state.Current.OperationId, MultiplayerSavePreparationEndReason.Cancelled, "fixture");
                set("pending", new SavegameLoadPacket { Token = Operation, Lobby = 10, SaveName = "MySave", Checksum = 123, Time = 456 });
                replace = true;
                Assert.AreEqual(false, publish.Invoke(null, new object[] { new ulong[] { 1 }, null }), "Coordinator continued after its proposal was replaced by an observer.");
                Assert.IsNull(state.Current);
            }
            finally
            {
                enabled = false;
                if (state.Current != null) state.End(state.Current.OperationId, MultiplayerSavePreparationEndReason.ScopeFinished, null);
                foreach (var pair in previous) set(pair.Key, pair.Value);
            }
        }

        [TestMethod]
        public void CoordinatorCompletesOldAbortReportingBeforeReentrantReplacementAndEndsEvenIfReportingThrows()
        {
            var state = MultiplayerSavePreparation.State;
            Assert.IsNull(state.Current);
            var events = new List<string>(); bool enabled = true;
            string next = "11234567890123456789012345678901";
            state.TryRegister("Fixture.AbortPublication", Guid.NewGuid().ToString("N"), snapshot => {
                if (!enabled || snapshot.Phase != MultiplayerSavePreparationPhase.Ended || snapshot.OperationId != Operation) return;
                events.Add("ended"); state.Begin(Snapshot(next)); events.Add("replacement");
            }, out _, replay: false);
            var complete = typeof(SavegameLoadCoordinator).GetMethod("CompleteAbortPublication", BindingFlags.Static | BindingFlags.NonPublic);
            try
            {
                state.Begin(Snapshot());
                complete.Invoke(null, new object[] { Operation, MultiplayerSavePreparationEndReason.Rejected, "old failure", (Action)(() => events.Add("old UI/log")) });
                CollectionAssert.AreEqual(new[] { "old UI/log", "ended", "replacement" }, events);
                Assert.AreEqual(next, state.Current.OperationId);
                state.End(next, MultiplayerSavePreparationEndReason.ScopeFinished, null);
                enabled = false; state.Begin(Snapshot());
                Assert.ThrowsExactly<TargetInvocationException>(() => complete.Invoke(null,
                    new object[] { Operation, MultiplayerSavePreparationEndReason.Rejected, "old failure", (Action)(() => { throw new InvalidOperationException("report failed"); }) }));
                Assert.IsNull(state.Current, "Reporting failure retained the old preparation.");
            }
            finally
            {
                enabled = false;
                if (state.Current != null) state.End(state.Current.OperationId, MultiplayerSavePreparationEndReason.ScopeFinished, null);
            }
        }

        [TestMethod]
        public void SameProposalDoesNotAdmitLobbyDestroyedByReleaseObserver()
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            string[] fields = { "pending", "preparingDelivered", "owner", "hostId", "attemptHost", "localSavePath", "localSaveHash" };
            var previous = fields.ToDictionary(name => name, name => typeof(SavegameLoadCoordinator).GetField(name, flags).GetValue(null));
            var state = MultiplayerSavePreparation.State; Assert.IsNull(state.Current);
            bool enabled = true;
            var owner = (Platform_Multiplayer)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Platform_Multiplayer));
            var lobby = (Platform_Multiplayer.MPLobby)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Platform_Multiplayer.MPLobby));
            lobby.id = new Steamworks.CSteamID(10); owner.activeLobby = lobby;
            Action<string, object> set = (name, value) => typeof(SavegameLoadCoordinator).GetField(name, flags).SetValue(null, value);
            state.TryRegister("Fixture.ReleaseScope", Guid.NewGuid().ToString("N"), snapshot => {
                if (enabled && snapshot.Phase == MultiplayerSavePreparationPhase.Released) owner.activeLobby = null;
            }, out _, replay: false);
            try
            {
                var proposal = new SavegameLoadPacket { Token = Operation, Lobby = 10, SaveName = "MySave" };
                set("pending", proposal); set("owner", owner); set("hostId", 1UL); set("attemptHost", true);
                set("localSavePath", @"C:\save.sav"); set("localSaveHash", "local-only-hash");
                Assert.AreEqual(true, typeof(SavegameLoadCoordinator).GetMethod("PublishPreparingCore", flags).Invoke(null, new object[] { new ulong[] { 1 }, null }));
                Assert.AreEqual(true, typeof(SavegameLoadCoordinator).GetMethod("PublishPhase", flags).Invoke(null,
                    new object[] { MultiplayerSavePreparationPhase.Released }));
                Assert.AreSame(proposal, typeof(SavegameLoadCoordinator).GetField("pending", flags).GetValue(null));
                var validate = typeof(SavegameLoadCoordinator).GetMethod("ValidatePreparedAdmission", flags);
                var error = Assert.ThrowsExactly<TargetInvocationException>(() => validate.Invoke(null, new object[] { proposal, @"C:\save.sav" }));
                Assert.IsInstanceOfType(error.InnerException, typeof(InvalidOperationException));
                Assert.IsNull(owner.activeLobby);
            }
            finally
            {
                enabled = false;
                if (state.Current != null) state.End(state.Current.OperationId, MultiplayerSavePreparationEndReason.ScopeFinished, null);
                foreach (var pair in previous) set(pair.Key, pair.Value);
            }
        }

        [TestMethod]
        public void NestedCleanupBoundariesClearCurrentImmediatelyAndFlushBeforeReturning()
        {
            var state = new MultiplayerSavePreparationState(_ => { });
            int ended = 0; state.TryRegister("Author", "Observe", snapshot => {
                if (snapshot.Phase == MultiplayerSavePreparationPhase.Ended) ended++;
            }, out _);
            state.Begin(Snapshot());
            using (state.DeferNotifications())
            {
                using (state.DeferNotifications())
                {
                    state.End(Operation, MultiplayerSavePreparationEndReason.Cancelled, null);
                    Assert.IsNull(state.Current); Assert.AreEqual(0, ended);
                }
                Assert.AreEqual(0, ended);
            }
            Assert.AreEqual(1, ended);
        }

        [TestMethod]
        public void ActualChoiceCleanupWrappersCannotEraseChoiceCreatedByEndedObserver()
        {
            // Only archive classification and external settings capture are isolated.
            // Both cleanup wrappers, the coordinator and PrepareLoad execute normally.
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var types = new[] { typeof(SavegameModSettings), typeof(SavegameLoadCoordinator) };
            var previous = types.SelectMany(type => type.GetFields(flags).Where(field => !field.IsInitOnly && !field.IsLiteral))
                .ToDictionary(field => field, field => field.GetValue(null));
            foreach (string name in new[] { "preparedEndpoints", "preparedSnapshots" })
                Assert.AreEqual(0, ((System.Collections.IDictionary)typeof(SavegameModSettings).GetField(name, flags).GetValue(null)).Count,
                    "Fixture requires no pre-existing prepared endpoints.");
            var state = MultiplayerSavePreparation.State; Assert.IsNull(state.Current);
            var harmony = new HarmonyLib.Harmony("APISharedTests.SavePreparationCleanup");
            var choice = typeof(SavegameModSettings).GetMethod("GetLoadChoiceState");
            var capture = typeof(SavegameModSettings).GetMethod("CaptureCompatibleHostSettings", flags, null, Type.EmptyTypes, null);
            harmony.Patch(choice, prefix: new HarmonyLib.HarmonyMethod(typeof(MultiplayerSavePreparationTests), nameof(ChoiceStatePrefix)));
            harmony.Patch(capture, prefix: new HarmonyLib.HarmonyMethod(typeof(MultiplayerSavePreparationTests), nameof(CaptureSettingsPrefix)));
            bool enabled = true; int replacements = 0;
            string replacement = "11234567890123456789012345678901";
            string newPath = System.IO.Path.GetFullPath(@"C:\new-save.sav");
            state.TryRegister("Fixture.CleanupWrapper", Guid.NewGuid().ToString("N"), snapshot => {
                if (!enabled || snapshot.Phase != MultiplayerSavePreparationPhase.Ended || snapshot.OperationId != Operation) return;
                SavegameModSettings.PrepareLoad(newPath, true);
                state.Begin(Snapshot(replacement)); replacements++;
            }, out _, replay: false);
            var platform = Platform_Multiplayer.Instance; var oldLobby = platform.activeLobby;
            var logger = new BepInEx.Logging.ManualLogSource("SavePreparationCleanupFixture");
            Action<Type, string, object> set = (type, name, value) => type.GetField(name, flags).SetValue(null, value);
            try
            {
                platform.activeLobby = null;
                set(typeof(SavegameModSettings), "log", logger); set(typeof(SavegameLoadCoordinator), "log", logger);
                foreach (bool finish in new[] { false, true })
                {
                    set(typeof(SavegameModSettings), "preparedTransferred", false);
                    set(typeof(SavegameModSettings), "preparedApplicationContext", null);
                    set(typeof(SavegameModSettings), "chosenPath", @"C:\old-save.sav");
                    set(typeof(SavegameLoadCoordinator), "pending", new SavegameLoadPacket { Token = Operation, SaveName = "old-save" });
                    set(typeof(SavegameLoadCoordinator), "owner", null); set(typeof(SavegameLoadCoordinator), "frameWork", null);
                    set(typeof(SavegameLoadCoordinator), "attempt", Operation);
                    set(typeof(SavegameLoadCoordinator), "released", false); set(typeof(SavegameLoadCoordinator), "loading", false);
                    state.Begin(Snapshot());
                    if (finish) SavegameModSettings.FinishLoad(); else SavegameModSettings.CancelLoadChoice();
                    Assert.AreEqual(newPath, typeof(SavegameModSettings).GetField("chosenPath", flags).GetValue(null),
                        "Old cleanup erased the observer's new choice: finish=" + finish);
                    Assert.IsNotNull(typeof(SavegameModSettings).GetField("chosenCurrent", flags).GetValue(null));
                    Assert.AreEqual(replacement, state.Current?.OperationId);
                    state.End(replacement, MultiplayerSavePreparationEndReason.ScopeFinished, null);
                }
                Assert.AreEqual(2, replacements);
            }
            finally
            {
                enabled = false;
                if (state.Current != null) state.End(state.Current.OperationId, MultiplayerSavePreparationEndReason.ScopeFinished, null);
                platform.activeLobby = oldLobby;
                foreach (var pair in previous) pair.Key.SetValue(null, pair.Value);
                harmony.Unpatch(choice, HarmonyLib.HarmonyPatchType.Prefix, harmony.Id);
                harmony.Unpatch(capture, HarmonyLib.HarmonyPatchType.Prefix, harmony.Id);
                logger.Dispose();
            }
        }
    }
}
