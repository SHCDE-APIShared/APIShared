using APIShared;
using APIShared.GameModes;
using APIShared.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Reflection;

namespace APISharedTests
{
    // The production reader and lifecycle state execute unchanged. Synthetic owner publications
    // cover races without game loads; acquisition additionally binds the installed engine monitor.
    [TestClass]
    public sealed class NativeWorldReadTests
    {
        [TestMethod]
        public void ReadsRequireTheExactHeldMonitorAndCoherentManagedReadiness()
        {
            object gate = new object();
            var state = NewState();
            var client = Client(gate, state);
            int notifications = 0;
            state.Register("read.fixture", "observe", _ => notifications++);
            long id = state.Begin(Seed(Mode()));
            state.BeginNativeLoad(id);
            state.CompleteNativeLoad(id, true);
            state.Checkpoint(id, state.Context, MissionInitializationPhase.NativeLoaded);
            Assert.IsNotNull(state.NativeClock, "Native completion precedes managed readiness.");
            lock (gate) Assert.IsFalse(client.TryReadCurrent(out _, out _));
            state.Ready(id, state.Context);
            int published = notifications;
            var clock = state.NativeClock;
            Assert.IsFalse(client.TryReadCurrent(out var denied, out string reason));
            Assert.AreEqual(0L, denied.WorldId); Assert.IsNull(denied.Mission);
            Assert.IsFalse(string.IsNullOrEmpty(reason));
            lock (new object()) Assert.IsFalse(client.TryReadCurrent(out _, out _));
            lock (gate)
            {
                Assert.IsTrue(client.TryReadCurrent(out var read, out reason));
                Assert.AreEqual(string.Empty, reason);
                Assert.AreSame(state.Current, read.Mission);
                Assert.AreEqual(state.NativeClock.World.Sequence, read.WorldId);
                Assert.IsTrue(client.TryReadCurrent(read, out var fresh, out _));
                Assert.AreSame(read.Mission, fresh.Mission);
                Assert.IsFalse(client.TryReadCurrent(default, out _, out _));
            }
            Assert.AreEqual(published, notifications, "A read must not publish lifecycle notifications.");
            Assert.AreSame(clock, state.NativeClock, "A read must not refresh or replace owner state.");
        }

        [TestMethod]
        public void RetainedWorldReturnsFreshModeAndCannotBeReplacedByEqualNumericIds()
        {
            object gate = new object();
            var world = new MissionLifecycleState.NativeWorldState(7, 3);
            var ready = Seed(Mode(), 3);
            var clock = new MissionLifecycleState.NativeClockState(ready, world);
            var client = new NativeWorldReadClient(new NativeWorldReadReader(gate, () => clock, () => ready));
            lock (gate)
            {
                Assert.IsTrue(client.TryReadCurrent(out var old, out _));
                ready = Seed(Mode(multiplayer: true), 3);
                clock = new MissionLifecycleState.NativeClockState(ready, world);
                Assert.IsTrue(client.TryReadCurrent(old, out var fresh, out _));
                Assert.IsFalse(old.Mode.IsRealMultiplayer, "Retained receipts stay immutable.");
                Assert.IsTrue(fresh.Mode.IsRealMultiplayer, "The caller must use the freshly returned mode.");
                Assert.AreSame(ready, fresh.Mission);
                clock = new MissionLifecycleState.NativeClockState(ready,
                    new MissionLifecycleState.NativeWorldState(world.Sequence, world.Mission));
                Assert.IsFalse(client.TryReadCurrent(old, out _, out _),
                    "Process-local numbers cannot substitute for exact world identity.");
            }
        }

        [TestMethod]
        public void KnownModeIsObservedWithoutGrantingAMultiplayerOrNetworkingMutationPolicy()
        {
            object gate = new object();
            var state = NewState();
            var client = Client(gate, state);
            foreach (var mode in new[] { default(GameModeSnapshot), Mode(conflict: true) })
            {
                MakeReady(state, mode);
                lock (gate) Assert.IsFalse(client.TryReadCurrent(out _, out _));
            }
            MakeReady(state, Mode(skirmish: true));
            lock (gate)
            {
                Assert.IsTrue(client.TryReadCurrent(out var solo, out _));
                Assert.IsTrue(solo.Mode.IsSingleplayerSkirmish);
                Assert.IsTrue(solo.Mode.LowLevelNetworked);
                Assert.IsFalse(solo.Mode.IsRealMultiplayer);
            }
            MakeReady(state, Mode(multiplayer: true));
            lock (gate)
            {
                Assert.IsTrue(client.TryReadCurrent(out var multiplayer, out _));
                Assert.IsTrue(multiplayer.Mode.IsRealMultiplayer);
            }
            MakeReady(state, Mode(editor: true));
            lock (gate)
            {
                Assert.IsTrue(client.TryReadCurrent(out var editor, out _));
                Assert.IsTrue(editor.Mode.IsMapEditor);
            }
        }

        [TestMethod]
        public void EqualSessionMetadataAndPartialPublicationsDoNotSatisfyReadyContextCoherence()
        {
            object gate = new object();
            var ready = Seed(Mode(), 4);
            var world = new MissionLifecycleState.NativeWorldState(1, 4);
            var clock = new MissionLifecycleState.NativeClockState(ready, world);
            var client = new NativeWorldReadClient(new NativeWorldReadReader(gate, () => clock, () => ready));
            lock (gate)
            {
                Assert.IsTrue(client.TryReadCurrent(out _, out _));
                clock = new MissionLifecycleState.NativeClockState(Seed(Mode(), 4), world);
                Assert.IsFalse(client.TryReadCurrent(out _, out _),
                    "Equal SessionId and mode do not establish the owner's exact ready publication.");
                clock = new MissionLifecycleState.NativeClockState(ready,
                    new MissionLifecycleState.NativeWorldState(1, 5));
                Assert.IsFalse(client.TryReadCurrent(out _, out _));
                clock = new MissionLifecycleState.NativeClockState(ready,
                    new MissionLifecycleState.NativeWorldState(0, 4));
                Assert.IsFalse(client.TryReadCurrent(out _, out _));
                clock = null;
                Assert.IsFalse(client.TryReadCurrent(out _, out _));
            }
        }

        [TestMethod]
        public void ReloadNestedFailureAndRetirementRevokeRetainedReads()
        {
            object gate = new object();
            var state = NewState();
            var client = Client(gate, state);
            long id = MakeReady(state, Mode());
            NativeWorldReadSnapshot original;
            lock (gate) Assert.IsTrue(client.TryReadCurrent(out original, out _));
            state.BeginNativeLoad(id);
            lock (gate) Assert.IsFalse(client.TryReadCurrent(original, out _, out _));
            state.CompleteNativeLoad(id, true);
            lock (gate)
            {
                Assert.IsFalse(client.TryReadCurrent(original, out _, out _));
                Assert.IsTrue(client.TryReadCurrent(out var reload, out _));
                Assert.AreEqual(original.Mission.SessionId, reload.Mission.SessionId);
                Assert.IsTrue(reload.WorldId > original.WorldId);
            }
            state.BeginNativeLoad(id); state.BeginNativeLoad(id);
            state.CompleteNativeLoad(id, false); state.CompleteNativeLoad(id, true);
            lock (gate) Assert.IsFalse(client.TryReadCurrent(out _, out _));
            state.BeginNativeLoad(id); state.CompleteNativeLoad(id, true);
            lock (gate) Assert.IsTrue(client.TryReadCurrent(out _, out _));
            state.End(MissionEndReason.Unloaded);
            lock (gate) Assert.IsFalse(client.TryReadCurrent(out _, out _));
            long failed = state.Begin(Seed(Mode()));
            state.BeginNativeLoad(failed); state.CompleteNativeLoad(failed, true);
            state.Finish(failed, state.Context, true, true, false, false);
            lock (gate) Assert.IsFalse(client.TryReadCurrent(out _, out _));
        }

        [TestMethod]
        public void QueuedOldStartEndAndReplayCannotRevalidateAnOldWorld()
        {
            object gate = new object();
            var state = NewState();
            var client = Client(gate, state);
            NativeWorldReadSnapshot old = default;
            int lateStarts = 0, oldEnds = 0;
            bool replaced = false, initialRead = false, lateRejected = true, replacementRead = true;
            state.Register("queued.fixture", "first", e => {
                if (e.Kind != MissionLifecycleKind.Start || replaced) return;
                lock (gate) initialRead = client.TryReadCurrent(out old, out _);
                replaced = true;
                MakeReady(state, Mode(editor: true));
            });
            state.Register("queued.fixture", "late", e => {
                if (e.Context.SessionId != old.Mission?.SessionId) return;
                if (e.Kind == MissionLifecycleKind.Start) lateStarts++;
                else if (e.Kind == MissionLifecycleKind.End) oldEnds++;
                else return;
                lock (gate)
                {
                    lateRejected &= !client.TryReadCurrent(old, out _, out _);
                    replacementRead &= client.TryReadCurrent(out var replacement, out _) && replacement.Mode.IsMapEditor;
                }
            });
            MakeReady(state, Mode());
            Assert.AreEqual(1, lateStarts); Assert.AreEqual(1, oldEnds);
            Assert.IsTrue(initialRead && lateRejected && replacementRead);
            bool replayed = false, replayRejectedOld = false, replayReadCurrent = false;
            state.Register("queued.fixture", "replay", e => {
                replayed = e.IsReplay;
                lock (gate)
                {
                    replayRejectedOld = !client.TryReadCurrent(old, out _, out _);
                    replayReadCurrent = client.TryReadCurrent(out var current, out _) && ReferenceEquals(e.Context, current.Mission);
                }
            });
            Assert.IsTrue(replayed && replayRejectedOld && replayReadCurrent);
        }

        [TestMethod]
        public void InvalidationAndReadyOrClockReplacementDuringReadsReturnNoReceipt()
        {
            object gate = new object();
            var ready = Seed(Mode(), 1);
            var world = new MissionLifecycleState.NativeWorldState(1, 1);
            var first = new MissionLifecycleState.NativeClockState(ready, world);
            for (int race = 0; race < 3; race++)
            {
                int clockReads = 0, readyReads = 0;
                var clock = first;
                var current = ready;
                var reader = new NativeWorldReadReader(gate, () => {
                    if (++clockReads == 2 && race != 2)
                        clock = race == 0 ? null : new MissionLifecycleState.NativeClockState(ready, world);
                    return clock;
                }, () => {
                    if (++readyReads == 2 && race == 2) current = Seed(Mode(multiplayer: true), 1);
                    return current;
                });
                lock (gate)
                {
                    Assert.IsFalse(reader.TryRead(default, false, out var absent, out string reason));
                    Assert.IsNull(absent.Mission); Assert.AreEqual(0L, absent.WorldId);
                    Assert.IsFalse(string.IsNullOrEmpty(reason));
                }
            }
        }

        [TestMethod]
        public void OwnerClientAcquisitionBindsAndCachesTheInstalledActualEngineMonitor()
        {
            var runtime = new ApiSharedRuntime();
            var mod = new ModApiClient("Independent.Author.ReadFixture", runtime);
            Assert.IsFalse(mod.TryGetNativeWorldReads(out _, out var pending));
            Assert.AreEqual(NativeCapabilityState.Pending, pending.State);
            SetRuntime(runtime, "state", NativeApiState.Ready);
            Assert.IsFalse(mod.TryGetNativeWorldReads(out _, out _));
            var service = (MissionLifecycleService)Activator.CreateInstance(typeof(MissionLifecycleService),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { null }, null);
            SetRuntime(runtime, "missionLifecycle", service);
            SetRuntime(runtime, "missionLifecycleDiagnostic", new NativeCapabilityDiagnostic(
                NativeCapabilityIds.MissionLifecycle, NativeCapabilityState.Available, "", "fixture"));
            var lifecycle = (MissionLifecycleState)typeof(MissionLifecycleService).GetField("state",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
            MakeReady(lifecycle, Mode());
            var field = EngineMonitor.Field;
            Assert.IsNotNull(field);
            Assert.IsTrue(field.IsPrivate && field.IsStatic && field.FieldType == typeof(object));
            object expectedMonitor = field.GetValue(null);
            Assert.IsNotNull(expectedMonitor);
            Assert.IsTrue(mod.TryGetNativeWorldReads(out var client, out var diagnostic), diagnostic?.Reason);
            Assert.AreEqual(NativeCapabilityState.Available, diagnostic.State);
            Assert.AreSame(expectedMonitor, client.SyncRoot);
            Assert.IsTrue(mod.TryGetNativeWorldReads(out var another, out _));
            Assert.AreSame(client.SyncRoot, another.SyncRoot);
            Assert.IsFalse(client.TryReadCurrent(out _, out _));
            lock (expectedMonitor)
            {
                Assert.IsTrue(client.TryReadCurrent(out var read, out _));
                Assert.AreSame(lifecycle.Current, read.Mission);
            }
            var failed = new ApiSharedRuntime();
            SetRuntime(failed, "state", NativeApiState.Unavailable);
            Assert.IsFalse(new ModApiClient("Other.Author", failed).TryGetNativeWorldReads(out _, out var unavailable));
            Assert.AreEqual(NativeCapabilityState.Faulted, unavailable.State);
        }

        [TestMethod]
        public void InstalledEngineMonitorHasOnlyItsProcessInitializationAssignment()
        {
            // Caching the actual monitor is safe only while game code does not replace it.
            // This installed-assembly guard checks that binding assumption, not game behavior
            // or writes by uncooperative reflection/native code outside the shared contract.
            var field = EngineMonitor.Field;
            Assert.IsNotNull(field);
            int assignments = 0;
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(field.DeclaringType.Assembly.Location))
            {
                foreach (var type in assembly.MainModule.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (!(instruction.Operand is Mono.Cecil.FieldReference target) ||
                            target.DeclaringType.FullName != field.DeclaringType.FullName || target.Name != field.Name) continue;
                        Assert.AreNotEqual(Mono.Cecil.Cil.OpCodes.Ldsflda, instruction.OpCode,
                            "A writable monitor address needs a new lifetime audit.");
                        if (instruction.OpCode != Mono.Cecil.Cil.OpCodes.Stsfld) continue;
                        assignments++;
                        Assert.AreEqual(".cctor", method.Name);
                        Assert.AreEqual(field.DeclaringType.FullName, method.DeclaringType.FullName);
                    }
                }
            }
            Assert.AreEqual(1, assignments, "The retained monitor must be assigned only during process initialization.");
        }

        [TestMethod]
        public void WarmSuccessfulAndRejectedReadsAllocateNoManagedMemory()
        {
            object gate = new object();
            var state = NewState();
            var client = Client(gate, state);
            MakeReady(state, Mode());
            lock (gate)
            {
                Assert.IsTrue(client.TryReadCurrent(out var expected, out _));
                for (int i = 0; i < 64; i++) client.TryReadCurrent(expected, out _, out _);
                int accepted = 0;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++)
                    if (client.TryReadCurrent(expected, out _, out _)) accepted++;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.AreEqual(10000, accepted); Assert.AreEqual(0L, allocated);
                state.End(MissionEndReason.Unloaded);
                for (int i = 0; i < 64; i++) client.TryReadCurrent(expected, out _, out _);
                before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++)
                    if (client.TryReadCurrent(expected, out _, out _)) accepted++;
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.AreEqual(10000, accepted); Assert.AreEqual(0L, allocated);
            }
        }

        private static NativeWorldReadClient Client(object gate, MissionLifecycleState state) =>
            new NativeWorldReadClient(new NativeWorldReadReader(gate, () => state.NativeClock, () => state.Current));
        private static MissionLifecycleState NewState() => new MissionLifecycleState(error => Assert.Fail(error.ToString()));
        private static long MakeReady(MissionLifecycleState state, GameModeSnapshot mode)
        {
            long id = state.Begin(Seed(mode));
            state.BeginNativeLoad(id); state.CompleteNativeLoad(id, true);
            state.Ready(id, state.Context);
            return id;
        }
        private static MissionContext Seed(GameModeSnapshot mode, long id = 0) =>
            new MissionContext(id, MissionStartKind.NewGame, mode, null, "protected read fixture", false);
        private static void SetRuntime(ApiSharedRuntime runtime, string field, object value) =>
            typeof(ApiSharedRuntime).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, value);
        private static GameModeSnapshot Mode(bool multiplayer = false, bool skirmish = false,
            bool editor = false, bool conflict = false) => new GameModeSnapshot(
                isRealMultiplayer: multiplayer, isSingleplayerSkirmishMode: skirmish,
                isSingleplayerSkirmish: skirmish, isSingleplayerTrail: false,
                isMapEditor: editor, multiplayerSave: false, directorAvailable: true,
                directorMultiplayer: multiplayer || skirmish, directorSkirmish: skirmish,
                lowLevelNetworked: multiplayer || skirmish, platformMultiplayer: multiplayer,
                lobbyMembers: 1, realLobbyMembers: multiplayer ? 2 : 1, skirmishLobbyMembers: skirmish ? 1 : 0,
                gameMembers: multiplayer ? 2 : 1, realNetworkGameMembers: multiplayer ? 2 : 0,
                gameType: 0, skirmishGameType: 0, skirmishTrailType: 0, coopTrailId: 0,
                kind: editor ? GameModeKind.MapEditor : GameModeKind.CustomGame,
                launchVariant: GameModeLaunchVariant.Standard, campaignMapId: 0,
                eventTrailType: 0, customizedTrailId: 0, customizedMissionId: 0,
                customizedOriginKind: 0, hasConflictingCustomizedOrigin: conflict);
    }
}
