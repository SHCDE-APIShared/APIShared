using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using APIShared.Units.AiMilitary;
using APIShared.Units.AiMilitary.Native;
using APIShared.Units.AiMilitary.Quota;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class AiMilitaryCoreTests
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly Type Core = typeof(AiMilitaryService);
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void EditorCompletionSpawnsStayOutsideTheOriginalCaptureAndKeepBothResults(bool callbackThrows)
        {
            using (var fixture = new SpawnFixture())
            lock (fixture.Engine)
            {
                var callbacks = new List<int>();
                var callbackReturns = new List<long>();
                var scope = fixture.Begin(result =>
                {
                    callbacks.Add(result.Unit.UnitId);
                    fixture.Created(3); // Ordinary same-owner/type placement from a synchronous callback.
                    callbackReturns.Add(fixture.FilterReturn(3));
                    if (callbackThrows) throw new InvalidOperationException("isolated callback failure");
                });
                try
                {
                    fixture.Created(1); fixture.Created(2);
                    var results = scope.Complete();
                    CollectionAssert.AreEqual(new[] { 1, 2 }, results.Select(result => result.Unit.UnitId).ToArray());
                    CollectionAssert.AreEqual(new[] { 1, 2 }, callbacks.ToArray());
                    CollectionAssert.AreEqual(new[] { 3L, 3L }, callbackReturns.ToArray());
                    Assert.IsTrue(results.All(result => result.Status == AiMilitaryAssignmentStatus.Declared));
                    Assert.IsFalse(fixture.IsTracked(3), "Callback placements must not inherit the completed declaration.");
                }
                finally { scope.Dispose(); }
                fixture.AssertNoCapture();
            }
        }

        [TestMethod]
        public void WorldEndFromEditorCompletionRejectsTheRemainingOldWorldCapture()
        {
            using (var fixture = new SpawnFixture())
            lock (fixture.Engine)
            {
                var callbacks = new List<AiMilitaryAssignmentResult>();
                var scope = fixture.Begin(result => {
                    callbacks.Add(result);
                    if (callbacks.Count == 1) fixture.EndWorld();
                    fixture.Created(3);
                });
                try
                {
                    fixture.Created(1); fixture.Created(2);
                    var results = scope.Complete();
                    Assert.AreEqual(2, results.Count); Assert.AreEqual(2, callbacks.Count);
                    Assert.AreEqual(AiMilitaryAssignmentStatus.Declared, results[0].Status);
                    Assert.AreEqual(AiMilitaryAssignmentStatus.Rejected, results[1].Status);
                    Assert.IsFalse(fixture.IsTracked(1)); Assert.IsFalse(fixture.IsTracked(2)); Assert.IsFalse(fixture.IsTracked(3));
                }
                finally { scope.Dispose(); }
                fixture.AssertNoCapture();
            }
        }

        [TestMethod]
        public void CompletingANestedScopeDoesNotCaptureCallbackPlacementsInItsParent()
        {
            using (var fixture = new SpawnFixture())
            lock (fixture.Engine)
            {
                var outer = fixture.Begin(_ => { });
                try
                {
                    fixture.Created(1);
                    var inner = fixture.Begin(_ => fixture.Created(3));
                    try
                    {
                        fixture.Created(2);
                        var results = inner.Complete();
                        CollectionAssert.AreEqual(new[] { 2 }, results.Select(result => result.Unit.UnitId).ToArray());
                    }
                    finally { inner.Dispose(); }
                    fixture.Created(4);
                    var outerResults = outer.Complete();
                    CollectionAssert.AreEqual(new[] { 1, 4 }, outerResults.Select(result => result.Unit.UnitId).ToArray());
                    Assert.IsFalse(fixture.IsTracked(3));
                }
                finally { outer.Dispose(); }
                fixture.AssertNoCapture();
            }
        }

        // Serial CLI fixture: only the external unit lookup and mode boundaries are substituted.
        // The real Begin/Complete/Request/Notify/OnCreated/return-filter paths run against owned memory.
        // No game thread runs here; restore the temporary boundary patches before freeing that memory.
        private sealed unsafe class SpawnFixture : IDisposable
        {
            private static GameUnit* units;
            private readonly Harmony harmony = new Harmony("APISharedTests.AiSpawnCapture");
            private readonly object service;
            private readonly object oldCapture;
            private readonly byte[] quota;
            internal readonly object Engine = new object();
            private static readonly FieldInfo Capture = Core.GetField("capture", Static);
            internal SpawnFixture()
            {
                Assert.IsTrue(units == null);
                oldCapture = Capture.GetValue(null);
                Assert.IsNull(oldCapture, "The fixture must not replace an active capture.");
                Assert.IsFalse(AiMilitaryQuotaRuntime.IsReady, "The CLI fixture must not replace an active game publication.");
                quota = AiMilitaryQuotaRuntime.Capture();
                units = (GameUnit*)Marshal.AllocHGlobal(sizeof(GameUnit) * 4);
                for (int i = 0; i < 4; i++) units[i] = new GameUnit {
                    r_GlobalId = (uint)(101 + i), r_ControllableForPlayerId = 1,
                    r_UnitChimp = eChimps.CHIMP_TYPE_ARCHER, r_AliveState = AliveState.IsAlive
                };
                service = Activator.CreateInstance(Core, true);
                Core.GetField("engine", Instance).SetValue(service, Engine);
                Core.GetField("ready", Instance).SetValue(service, true);
                try
                {
                    Patch(typeof(APIShared.UnitAccess).GetMethods().Single(method => method.Name == "TryGetById" &&
                        method.GetParameters()[0].ParameterType == typeof(int)), nameof(Lookup));
                    Patch(typeof(APIShared.GameModes.GameModeHelper).GetMethod("IsMapEditor"), nameof(Editor));
                    Patch(typeof(APIShared.GameModes.GameModeHelper).GetMethod("IsRealMultiplayer"), nameof(Local));
                }
                catch { Dispose(); throw; }
            }
            private void Patch(MethodInfo target, string prefix) => harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(SpawnFixture).GetMethod(prefix, Static)));
            private static bool Lookup(int unitId, ref GameUnit* unit, ref APIShared.UnitLookupFailure failure, ref bool __result)
            {
                __result = unitId >= 1 && unitId <= 4;
                unit = __result ? units + unitId - 1 : null;
                failure = __result ? APIShared.UnitLookupFailure.None : APIShared.UnitLookupFailure.OutOfRange;
                return false;
            }
            private static bool Editor(ref bool __result) { __result = true; return false; }
            private static bool Local(ref bool __result) { __result = false; return false; }
            internal IAiMilitarySpawnScope Begin(Action<AiMilitaryAssignmentResult> completed)
            {
                object[] args = { "fixture-owner", 1, eChimps.CHIMP_TYPE_ARCHER,
                    new AiMilitaryAssignment(AiMilitaryRole.None, AiRecruitmentMode.Regular), true, false, completed, null, null };
                Assert.IsTrue((bool)Core.GetMethod("Begin", Instance).Invoke(service, args), args[8]?.ToString());
                return (IAiMilitarySpawnScope)args[7];
            }
            internal void Created(int id) => Core.GetMethod("OnCreated", Instance).Invoke(service, new object[] { id });
            internal long FilterReturn(long id) => (long)Core.GetMethod("FilterCapturedCreateReturn", Instance).Invoke(service, new object[] { id, id });
            internal bool IsTracked(int id) => ((IDictionary)Core.GetField("tracked", Instance).GetValue(service)).Contains(id);
            internal void EndWorld() => Core.GetMethod("ClearWorld", Instance).Invoke(service, null);
            internal void AssertNoCapture() => Assert.AreSame(oldCapture, Capture.GetValue(null));
            public void Dispose()
            {
                harmony.UnpatchSelf();
                Capture.SetValue(null, oldCapture);
                AiMilitaryQuotaRuntime.Restore(quota);
                Marshal.FreeHGlobal((IntPtr)units); units = null;
            }
        }
        [TestMethod]
        public void RecycledCreationCompletionsRunAtTheSafePointOrWorldEndExactlyOnce()
        {
            object service = Activator.CreateInstance(Core, true), engine = new object();
            Core.GetField("engine", Instance).SetValue(service, engine); Core.GetField("ready", Instance).SetValue(service, true);
            var retired = (IList)Core.GetField("retired", Instance).GetValue(service);
            Type tracked = Core.GetNestedType("Tracked", BindingFlags.NonPublic);
            int calls = 0;
            var observed = new System.Collections.Generic.List<AiMilitaryReason>();
            Action<AiMilitaryAssignmentResult> callback = result => { calls++; observed.Add(result.Reason); };
            object NewEntry(int id)
            {
                var assignment = new AiMilitaryAssignment(AiMilitaryRole.None, AiRecruitmentMode.Regular);
                object entry = Activator.CreateInstance(tracked, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { "owner", new AiUnitRef(id, (uint)(100 + id)), 1, eChimps.CHIMP_TYPE_ARCHER,
                        assignment, false, false, callback }, null);
                tracked.GetField("Last", Instance).SetValue(entry, new AiMilitaryAssignmentResult(new AiUnitRef(id, (uint)(100 + id)),
                    assignment, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.StaleIdentity, "slot reused"));
                return entry;
            }
            retired.Add(NewEntry(1));
            Assert.AreEqual(0, calls, "Native creation only retires the callback; it must not invoke user code there.");
            lock (engine)
            {
                Core.GetMethod("Process", Instance).Invoke(service, null);
                Assert.AreEqual(1, calls); Assert.AreEqual(0, retired.Count);
                Core.GetMethod("Process", Instance).Invoke(service, null);
                Assert.AreEqual(1, calls);
                retired.Add(NewEntry(2));
                Core.GetMethod("ClearWorld", Instance).Invoke(service, null);
                Assert.AreEqual(2, calls, "World end must also complete an already-retired creation.");
                Core.GetMethod("ClearWorld", Instance).Invoke(service, null);
                Assert.AreEqual(2, calls);
            }
            CollectionAssert.AreEqual(new[] { AiMilitaryReason.StaleIdentity, AiMilitaryReason.WorldEnded }, observed);
        }
        [TestMethod]
        public void ClearWorldRetiresFlagsBeforeIsolatedExactlyOnceReentrantCallbacks()
        {
            Assert.IsFalse(AiMilitaryQuotaRuntime.IsReady, "Private fixture must not replace an active game publication.");
            object service = Activator.CreateInstance(Core, true), engine = new object();
            Core.GetField("engine", Instance).SetValue(service, engine); Core.GetField("ready", Instance).SetValue(service, true);
            var registry = (IDictionary)Core.GetField("tracked", Instance).GetValue(service);
            var flags = (uint[])Core.GetField("additional", Instance).GetValue(service); flags[1] = 101;
            int calls = 0; bool cleanBeforeCallbacks = true, rejectedReentry = true;
            var failures = new System.Collections.Generic.List<Exception>();
            var assignment = new AiMilitaryAssignment(AiMilitaryRole.None, AiRecruitmentMode.Regular);
            MethodInfo requestMethod = Core.GetMethods(Instance).Single(m => m.Name == "Request" && m.GetParameters().Length == 7);
            Action<AiMilitaryAssignmentResult> callback = result => {
                calls++; cleanBeforeCallbacks &= registry.Count == 0 && flags[1] == 0 && result.Reason == AiMilitaryReason.WorldEnded;
                // The production publisher isolates callback exceptions. Record every
                // observation here and assert outside it, so it cannot hide test failures.
                try
                {
                    object[] request = { "owner", new AiUnitRef(50, 150), assignment, null, false, false, null };
                    rejectedReentry &= !(bool)requestMethod.Invoke(service, request);
                }
                catch (Exception error) { failures.Add(error); }
                if (calls == 1) throw new InvalidOperationException("one consumer failed");
            };
            Type tracked = Core.GetNestedType("Tracked", BindingFlags.NonPublic);
            for (int id = 1; id <= 2; id++)
            {
                object entry = Activator.CreateInstance(tracked, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { "owner", new AiUnitRef(id, (uint)(100 + id)), 1, eChimps.CHIMP_TYPE_ARCHER, assignment, false, false, callback }, null);
                tracked.GetField("Pending", Instance).SetValue(entry, true); registry.Add(id, entry);
            }
            byte[] quota = AiMilitaryQuotaRuntime.Capture();
            try
            {
                lock (engine)
                {
                    Core.GetMethod("ClearWorld", Instance).Invoke(service, null);
                    Assert.AreEqual(2, calls, "One callback failure cannot suppress another pending completion.");
                    Core.GetMethod("ClearWorld", Instance).Invoke(service, null);
                    Assert.AreEqual(2, calls, "No completion replay on a second clear.");
                }
                Assert.IsTrue(cleanBeforeCallbacks, "Registry and flags must already be cleared before every completion.");
                Assert.IsTrue(rejectedReentry, "Every reentrant old-world request must be refused.");
                Assert.AreEqual(0, failures.Count, "Reentry must be rejected before any native lookup/error.");
                Assert.AreEqual(0, registry.Count); Assert.AreEqual(0U, flags[1]);
            }
            finally { AiMilitaryQuotaRuntime.Restore(quota); }
        }
        [TestMethod]
        public void SaveCodecRetainsRejectedStatusAndInitializationFlagsAndRejectsUnknownStatus()
        {
            Type saved = Core.GetNestedType("SaveUnit", BindingFlags.NonPublic);
            IList list = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(saved));
            object value = Activator.CreateInstance(saved, true);
            void Set(string name, object data) => saved.GetField(name, Instance).SetValue(value, data);
            Set("Owner", "owner"); Set("Identity", new AiUnitRef(1, 101)); Set("Player", 1); Set("Type", eChimps.CHIMP_TYPE_ENGINEER);
            Set("Assignment", new AiMilitaryAssignment(AiMilitaryRole.HarassmentEngineers, AiRecruitmentMode.Additional));
            Set("Declaration", true); Set("Pending", true); Set("Protected", true); Set("Private", default(AiNativeGroup));
            Set("Status", AiMilitaryAssignmentStatus.Rejected); Set("Reason", AiMilitaryReason.NoAvailableNativeJob); list.Add(value);
            byte[] bytes = (byte[])Core.GetMethod("EncodeUnits", Static).Invoke(null, new object[] { list });
            IList decoded = (IList)Core.GetMethod("DecodeUnits", Static).Invoke(null, new object[] { bytes });
            object after = decoded[0];
            foreach (string field in new[] { "Owner", "Identity", "Player", "Type", "Assignment", "Declaration", "Pending", "Protected", "Status", "Reason" })
                Assert.AreEqual(saved.GetField(field, Instance).GetValue(value), saved.GetField(field, Instance).GetValue(after), field);
            using (var stream = new MemoryStream(bytes)) using (var reader = new BinaryReader(stream))
            {
                reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt32(); reader.ReadUInt32(); reader.ReadUInt16(); reader.ReadByte(); reader.ReadString();
                reader.ReadByte(); reader.ReadByte(); reader.ReadBoolean(); reader.ReadBoolean(); reader.ReadBoolean();
                byte[] bad = (byte[])bytes.Clone(); bad[stream.Position] = 255;
                var error = Assert.ThrowsExactly<TargetInvocationException>(() => Core.GetMethod("DecodeUnits", Static).Invoke(null, new object[] { bad }));
                Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
            }
        }
        [TestMethod]
        public void PendingNativeInitializationIsSavedAndDeathsAreNotResurrected()
        {
            var unit = new GameUnit { r_AliveState = AliveState.NeedsInit };
            Assert.IsTrue(AiMilitarySaveLifetime.CanCapture(in unit));
            unit.r_AliveState = AliveState.IsAlive; Assert.IsTrue(AiMilitarySaveLifetime.CanCapture(in unit));
            unit.r_IsKilledByProjectile = 1; Assert.IsFalse(AiMilitarySaveLifetime.CanCapture(in unit));
            unit.r_AliveState = AliveState.MarkedForDeletion; Assert.IsFalse(AiMilitarySaveLifetime.CanCapture(in unit));
            unit.r_AliveState = AliveState.None; Assert.IsFalse(AiMilitarySaveLifetime.CanCapture(in unit));
        }
        [TestMethod]
        public void FailedPreparedRecordRemainsAStickyNativeLoadVetoAndKeepsOriginalBytes()
        {
            foreach (var status in new[] { VersionedSaveLoadStatus.Unrecognized, VersionedSaveLoadStatus.Restored })
            {
                object service = Activator.CreateInstance(Core, true), engine = new object();
                Core.GetField("engine", Instance).SetValue(service, engine); Core.GetField("ready", Instance).SetValue(service, true);
                var load = new VersionedSaveLoad(status, VersionedSaveLoadSource.MapToPlay, "map.map", 1, 1,
                    new System.Collections.Generic.Dictionary<string, byte[]>(), 0, "fixture");
                var error = Assert.ThrowsExactly<TargetInvocationException>(() => Core.GetMethod("OnPrepared", Instance).Invoke(service, new object[] { load }));
                Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
                Assert.IsTrue((bool)Core.GetField("rawRestoreFailed", Instance).GetValue(service));
                lock (engine)
                {
                    for (int retry = 0; retry < 2; retry++)
                        Assert.IsFalse((bool)Core.GetMethod("OnRawRestored", Instance).Invoke(service, null), "A duplicate Original cannot turn a failed preparation into a successful load.");
                    Core.GetMethod("CompleteNativeLoad", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service, null);
                }
                Assert.IsTrue((bool)Core.GetField("rawRestoreFailed", Instance).GetValue(service));
                var restored = new VersionedSaveLoad(VersionedSaveLoadStatus.Restored, VersionedSaveLoadSource.MapToPlay,
                    "map.map", 1, 1, null, 0, string.Empty);
                Assert.ThrowsExactly<TargetInvocationException>(() => Core.GetMethod("OnLoaded", Instance).Invoke(service, new object[] { restored }),
                    "The versioned registry must observe a callback failure and preserve the original record.");
            }
            object emptyService = Activator.CreateInstance(Core, true), emptyEngine = new object();
            Core.GetField("engine", Instance).SetValue(emptyService, emptyEngine); Core.GetField("ready", Instance).SetValue(emptyService, true);
            var absent = new VersionedSaveLoad(VersionedSaveLoadStatus.NoRecord, VersionedSaveLoadSource.MapToPlay, "vanilla.map", 0, 0, null, 0, string.Empty);
            Core.GetMethod("OnPrepared", Instance).Invoke(emptyService, new object[] { absent });
            lock (emptyEngine) Assert.IsTrue((bool)Core.GetMethod("OnRawRestored", Instance).Invoke(emptyService, null), "Absent optional data retains Vanilla's original successful load.");
        }
    }
}
