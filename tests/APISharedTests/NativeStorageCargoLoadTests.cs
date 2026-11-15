using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using APIShared;
using APIShared.GameModes;
using APIShared.Economy;
using APIShared.Goods;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // The same prepared-load controller used by Runtime.Prepare is bound to the actual
    // versioned registry. The transport is managed; no native world or archive is executed.
    [TestClass, DoNotParallelize]
    public sealed class NativeStorageCargoLoadTests
    {
        private sealed class Binding
        {
            internal readonly NativeStoragePreparedLoad Prepared = new NativeStoragePreparedLoad();
            internal readonly VersionedSaveDataRegistry Registry;
            internal readonly VersionedSaveDataHandle Handle;
            internal Func<VersionedSaveContext, byte[]> Save;
            internal Action<byte[]> Load;
            internal int Captures;
            internal Binding(IReadOnlyList<NativeStorageGoodRegistration> definitions)
            {
                Registry = new VersionedSaveDataRegistry((id, save, load) => { Save = save; Load = load; return true; }, _ => { }, _ => { });
                var registration = new VersionedSaveDataRegistration("cargo.test", "cargo-envelope", 2, 1, new[] { "ledger", "stores" },
                    writer => { Captures++; writer.Write("ledger", new byte[] { 99 }); writer.Write("stores", new byte[] { 99 }); return true; }, _ => { })
                { Scope = VersionedSaveScope.SaveGamesAndMaps, Prepared = load => Prepared.Prepare(definitions, load) };
                Assert.IsTrue(Registry.TryRegister(registration, out Handle, out string reason), reason);
            }
            internal void Read(byte[] record)
            {
                Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "cargo.sav");
                if (record != null) Load(record);
                Registry.PrepareLoad(); Registry.EndLoad();
            }
            internal byte[] SaveCurrent() => Save(new VersionedSaveContext(true, false, "cargo.sav"));
        }
        private static NativeStorageState State(bool cargo, out CustomGoodId good)
        {
            var state = new NativeStorageState(); good = new CustomGoodId("fish.owner", "fish");
            Assert.IsTrue(state.TryRegister(good, StoragePool.Granary, out _));
            if (cargo)
            {
                Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareCreateCargo(good, 1, 1, 8, out var plan, out _, out _));
                using (plan) plan.Publish();
            }
            return state;
        }
        private static SortedDictionary<string, byte[]> Entries(NativeStorageState state)
        {
            state.Capture(out byte[] ledger, out byte[] stores);
            return new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { ["ledger"] = ledger, ["stores"] = stores };
        }
        [TestMethod]
        [DataRow(1, 1)]
        [DataRow(2, 1)]
        public void ActualPreparedBindingRejectsUnderdeclaredCargoEnvelopeWithoutStagingAndPreservesOriginalBytes(int recordVersion, int minimumReader)
        {
            var state = State(true, out _); byte[] original = VersionedSaveRecordCodec.Encode(recordVersion, minimumReader, Entries(state));
            var before = (byte[])original.Clone(); var binding = new Binding(state.GetRegistrations()); binding.Read(original);
            Assert.IsTrue(binding.Prepared.Blocked); Assert.IsTrue(binding.Prepared.MayContainCargo); Assert.IsNull(binding.Prepared.State);
            StringAssert.Contains(binding.Prepared.Reason, "minimum reader 2");
            Assert.IsTrue(binding.Handle.KeepsLoadedRecord);
            CollectionAssert.AreEqual(before, binding.SaveCurrent()); CollectionAssert.AreEqual(before, original);
            Assert.AreEqual(0, binding.Captures, "Rejected cargo must never be rewritten from an empty/new candidate.");
            Assert.IsTrue(NativeStorageRuntime.CargoSaveCheckpointRequired(true,
                binding.Prepared.Blocked && binding.Prepared.MayContainCargo, null, binding.Prepared.State));
        }
        [TestMethod]
        [DataRow(1, 1)]
        [DataRow(2, 1)]
        [DataRow(5, 1)]
        public void LegacyPayloadAndFutureCompatibleEnvelopeRetainTheirExistingLoadBehavior(int recordVersion, int minimumReader)
        {
            var state = State(false, out _); var binding = new Binding(state.GetRegistrations());
            binding.Read(VersionedSaveRecordCodec.Encode(recordVersion, minimumReader, Entries(state)));
            Assert.IsFalse(binding.Prepared.Blocked); Assert.IsFalse(binding.Prepared.MayContainCargo);
            Assert.IsNotNull(binding.Prepared.State); Assert.IsFalse(binding.Prepared.State.HasCargoHistory);
            Assert.IsFalse(binding.Handle.KeepsLoadedRecord);
            Assert.IsFalse(NativeStorageRuntime.CargoSaveCheckpointRequired(true, false, binding.Prepared.State, null));
        }
        [TestMethod]
        [DataRow(2, 2)]
        [DataRow(5, 2)]
        public void CargoPayloadWithQualifiedReaderMetadataStagesOnlyTheValidatedOwner(int recordVersion, int minimumReader)
        {
            var state = State(true, out var good); var binding = new Binding(state.GetRegistrations());
            binding.Read(VersionedSaveRecordCodec.Encode(recordVersion, minimumReader, Entries(state)));
            Assert.IsFalse(binding.Prepared.Blocked); Assert.IsNotNull(binding.Prepared.State); Assert.IsTrue(binding.Prepared.State.HasCargoHistory);
            Assert.IsTrue(binding.Prepared.State.TryGetCargo(good.OwnerGuid, 1, out var lot)); Assert.AreEqual(8, lot.Remaining);
            Assert.IsFalse(binding.Handle.KeepsLoadedRecord);
        }
        [TestMethod]
        public void MalformedProvablyLegacyStoresKeepOriginalSourceWithoutActivatingNewCargoSavePolicy()
        {
            var state = State(false, out _); var entries = Entries(state);
            // Keep the valid stores magic/schema-1 header, but truncate the old physical body.
            // This pair is invalid, yet it provably contains no schema-2 cargo tail.
            byte[] corrupt = new byte[entries["stores"].Length - 1]; Array.Copy(entries["stores"], corrupt, corrupt.Length); entries["stores"] = corrupt;
            byte[] original = VersionedSaveRecordCodec.Encode(1, 1, entries); var binding = new Binding(state.GetRegistrations()); binding.Read(original);
            Assert.IsTrue(binding.Prepared.Blocked); Assert.IsFalse(binding.Prepared.MayContainCargo); Assert.IsNull(binding.Prepared.State);
            Assert.IsTrue(binding.Handle.KeepsLoadedRecord); CollectionAssert.AreEqual(original, binding.SaveCurrent());
            Assert.IsFalse(NativeStorageRuntime.CargoSaveCheckpointRequired(true,
                binding.Prepared.Blocked && binding.Prepared.MayContainCargo, null, null));
        }
        [TestMethod]
        [DataRow("too-new")]
        [DataRow("unrecognized")]
        [DataRow("missing-stores")]
        public void PassiveRecordsThatCannotProveLegacySemanticsRemainConservativelyCargoUnknown(string kind)
        {
            var state = State(true, out _); var entries = Entries(state);
            byte[] original;
            if (kind == "unrecognized") original = new byte[] { 1, 2, 3 };
            else
            {
                if (kind == "missing-stores") entries.Remove("stores");
                original = VersionedSaveRecordCodec.Encode(kind == "too-new" ? 3 : 1, kind == "too-new" ? 3 : 1, entries);
            }
            var binding = new Binding(state.GetRegistrations()); binding.Read(original);
            Assert.IsTrue(binding.Prepared.Blocked); Assert.IsTrue(binding.Prepared.MayContainCargo); Assert.IsNull(binding.Prepared.State);
            Assert.IsTrue(binding.Handle.KeepsLoadedRecord); CollectionAssert.AreEqual(original, binding.SaveCurrent());
            Assert.IsTrue(NativeStorageRuntime.CargoSaveCheckpointRequired(true,
                binding.Prepared.Blocked && binding.Prepared.MayContainCargo, null, null));
        }
        [TestMethod]
        public void ANewNoRecordWorldClearsThePreviousPassiveClassification()
        {
            var state = State(true, out _); var binding = new Binding(state.GetRegistrations());
            binding.Read(new byte[] { 1, 2, 3 }); Assert.IsTrue(binding.Prepared.Blocked); Assert.IsTrue(binding.Prepared.MayContainCargo);
            binding.Read(null); Assert.IsFalse(binding.Prepared.Blocked); Assert.IsFalse(binding.Prepared.MayContainCargo);
            Assert.IsNotNull(binding.Prepared.State); Assert.IsFalse(binding.Prepared.State.HasCargoHistory);
        }

        // This binds the actual Runtime.Prepare/Capture/Loaded methods to the actual registry.
        // Only mission metadata is published: no native cohort, game objects, hooks, gold or
        // building probes are initialized. Cargo-only fixture states have no physical rows.
        private sealed class RuntimeBinding : IDisposable
        {
            private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            private static readonly FieldInfo Published = typeof(MissionLifecycleService).GetField("published", BindingFlags.Static | BindingFlags.NonPublic);
            private readonly object previousPublished;
            private readonly MissionLifecycleService service;
            private readonly MissionLifecycleState lifecycle;
            private readonly MissionExecutionSession execution;
            private readonly FieldInfo executing;
            private MissionExecutionSession.Scope previousExecution;
            private bool inExecution;
            private MissionContext seed;
            internal readonly object Gate = new object();
            internal readonly NativeStorageRuntime Runtime;
            internal readonly VersionedSaveDataRegistry Registry;
            internal readonly VersionedSaveDataHandle Handle;
            internal readonly CustomGoodId Good;
            internal readonly NativeStoredGood StoredGood;
            internal long Session;
            internal Func<VersionedSaveContext, byte[]> Save;
            private Action<byte[]> load;
            internal NativeStorageState CurrentState => (NativeStorageState)typeof(NativeStorageRuntime).GetField("state", Fields).GetValue(Runtime);

            internal RuntimeBinding(bool editorCreated, bool cargo = true)
            {
                // Demand updates would write native fixture memory if a process-wide cohort
                // existed. Preserve it; do not clear/replace it to make this fixture run.
                Assert.IsNull(typeof(NativeStorageNativeRuntime).GetField("candidate", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
                Assert.IsFalse(GoodsWalletNative.Instance.IsReady);
                previousPublished = Published.GetValue(null);
                try
                {
                    service = (MissionLifecycleService)Activator.CreateInstance(typeof(MissionLifecycleService),
                        BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { null }, null);
                    lifecycle = (MissionLifecycleState)typeof(MissionLifecycleService).GetField("state", Fields).GetValue(service);
                    execution = (MissionExecutionSession)typeof(MissionLifecycleService).GetField("executionSession", Fields).GetValue(service);
                    executing = typeof(MissionLifecycleService).GetField("executing", Fields);
                    Published.SetValue(null, service);
                    Runtime = (NativeStorageRuntime)Activator.CreateInstance(typeof(NativeStorageRuntime), true);
                    typeof(NativeStorageRuntime).GetField("engine", Fields).SetValue(Runtime, Gate);
                    typeof(NativeStorageRuntime).GetField("ready", Fields).SetValue(Runtime, true);
                    var stock = State(cargo, out Good);
                    Assert.IsFalse(stock.HasRows, "This fixture must never enter native building-probe reconciliation.");
                    var definitions = (List<NativeStorageGoodRegistration>)typeof(NativeStorageRuntime).GetField("definitions", Fields).GetValue(Runtime);
                    definitions.AddRange(stock.GetRegistrations());
                    StoredGood = new NativeStoredGood(Good, StoragePool.Granary);
                    var capture = (VersionedSaveCapture)Delegate.CreateDelegate(typeof(VersionedSaveCapture), Runtime,
                        typeof(NativeStorageRuntime).GetMethod("Capture", Fields));
                    var prepared = (Action<VersionedSaveLoad>)Delegate.CreateDelegate(typeof(Action<VersionedSaveLoad>), Runtime,
                        typeof(NativeStorageRuntime).GetMethod("Prepare", Fields));
                    var loaded = (Action<VersionedSaveLoad>)Delegate.CreateDelegate(typeof(Action<VersionedSaveLoad>), Runtime,
                        typeof(NativeStorageRuntime).GetMethod("Loaded", Fields));
                    Registry = new VersionedSaveDataRegistry((id, save, read) => { Save = save; load = read; return true; }, _ => { }, _ => { });
                    var registration = new VersionedSaveDataRegistration("causal.storage", "causal-storage", 2, 1,
                        new[] { "ledger", "stores" }, capture, loaded)
                        { Scope = VersionedSaveScope.SaveGamesAndMaps, Prepared = prepared };
                    Assert.IsTrue(Registry.TryRegister(registration, out Handle, out string reason), reason);
                    typeof(NativeStorageRuntime).GetField("saveHandle", Fields).SetValue(Runtime, Handle);
                    Begin(editorCreated ? MissionStartKind.EditorCreated : MissionStartKind.LoadedSave);
                    lock (Gate)
                    {
                        if (editorCreated)
                        {
                            // A real blank editor world has no archive callback. Exercise that
                            // actual fallback before seeding fixture-owned cargo on its state.
                            Assert.IsTrue(Runtime.TryGetStock(Good, 1, out _, out reason), reason);
                            if (cargo)
                            {
                                Assert.AreEqual(GoodsTransactionStatus.Applied,
                                    CurrentState.PrepareCreateCargo(Good, 1, 1, 8, out var plan, out _, out reason), reason);
                                using (plan) plan.Publish();
                            }
                        }
                        else Read(VersionedSaveRecordCodec.Encode(2, 2, Entries(stock)), Session);
                        Assert.IsTrue(Runtime.TryGetStock(Good, 1, out _, out reason), reason);
                    }
                    Ready();
                }
                catch
                {
                    if (inExecution) execution.Restore(previousExecution);
                    Published.SetValue(null, previousPublished);
                    throw;
                }
            }

            internal void Begin(MissionStartKind kind)
            {
                Assert.IsFalse(inExecution);
                bool editor = kind == MissionStartKind.EditorCreated || kind == MissionStartKind.EditorLoaded;
                var mode = new GameModeSnapshot(false, false, false, false, editor, false, true, false, false,
                    false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    editor ? GameModeKind.MapEditor : GameModeKind.CustomGame, GameModeLaunchVariant.Standard,
                    0, 0, 0, 0, 0, false);
                seed = new MissionContext(0, kind, mode, editor ? "editor.map" : "mission.sav", "fixture", false, 160, 1, 1);
                previousExecution = execution.Enter(lifecycle); inExecution = true;
                object operation = Activator.CreateInstance(typeof(MissionLifecycleService).GetNestedType("Operation", BindingFlags.NonPublic), true);
                executing.SetValue(service, operation);
                Session = lifecycle.Begin(seed);
                operation.GetType().GetField("Id", Fields).SetValue(operation, Session);
                lifecycle.CompleteNativeLoad(Session, true);
                Assert.AreEqual(Session, MissionLifecycleService.ExecutingSessionId);
            }

            internal void Ready()
            {
                lifecycle.Ready(Session, seed);
                execution.Restore(previousExecution); inExecution = false;
                executing.SetValue(service, null);
                Assert.AreEqual(0L, MissionLifecycleService.ExecutingSessionId);
            }

            internal void RefreshMetadata()
            {
                object prior = MissionLifecycleService.NativeWorld;
                typeof(MissionLifecycleState).GetMethod("RefreshNativeClock", Fields).Invoke(lifecycle, new object[] { lifecycle.Context });
                Assert.AreSame(prior, MissionLifecycleService.NativeWorld);
            }

            internal void Read(byte[] record, long session, VersionedSaveLoadSource source = VersionedSaveLoadSource.SaveGame)
            {
                Registry.BeginLoad(source, "causal.sav", session);
                if (record != null) load(record);
                Registry.PrepareLoad(); Registry.EndLoad();
            }
            internal byte[] SaveCurrent() => Save(new VersionedSaveContext(true, false, "causal.sav"));
            public void Dispose()
            {
                try { if (inExecution) execution.Restore(previousExecution); }
                finally { Published.SetValue(null, previousPublished); }
            }
        }

        [TestMethod]
        [DataRow(false, false, false)]
        [DataRow(false, false, true)]
        [DataRow(false, true, false)]
        [DataRow(false, true, true)]
        [DataRow(true, false, false)]
        [DataRow(true, false, true)]
        [DataRow(true, true, false)]
        [DataRow(true, true, true)]
        public void UnassociatedArchiveCannotBorrowReadyWorldOrEraseItsStock(bool wrongPositive, bool noRecord, bool editorCreated)
        {
            using (var fixture = new RuntimeBinding(editorCreated))
            lock (fixture.Gate)
            {
                NativeStorageState prior = fixture.CurrentState;
                prior.Capture(out byte[] ledger, out byte[] stores);
                byte[] incoming = noRecord ? null : VersionedSaveRecordCodec.Encode(1, 1, Entries(State(false, out _)));
                fixture.Read(incoming, wrongPositive ? fixture.Session + 1 : 0);
                fixture.RefreshMetadata();
                Assert.IsFalse(fixture.Runtime.TryGetStock(fixture.Good, 1, out _, out _),
                    "An unassociated archive must not borrow ReadyB's global ID, including its blank-editor fallback.");
                Assert.IsFalse(fixture.Runtime.IsCurrentWorldReady);
                Assert.IsFalse(fixture.Runtime.TryGetSequence(fixture.Good.OwnerGuid, out _, out _));
                Assert.IsFalse(fixture.Runtime.TryExchange(fixture.StoredGood,
                    new NativeStorageSnapshot(MissionLifecycleService.NativeWorld, default, 0), 1, 1, 0, out _, out _));
                Assert.ThrowsExactly<InvalidDataException>(() => fixture.Runtime.FreezeProductionSave());
                Assert.IsTrue(fixture.Runtime.HasExternalCargoSaveDemand,
                    "Unknown association, even NoRecord, must retain the protected-save requirement.");
                Assert.AreSame(prior, fixture.CurrentState);
                prior.Capture(out byte[] afterLedger, out byte[] afterStores);
                CollectionAssert.AreEqual(ledger, afterLedger); CollectionAssert.AreEqual(stores, afterStores);
                if (noRecord)
                {
                    // Failed preparation retains passive-save policy even when the original record is absent.
                    Assert.IsTrue(fixture.Handle.KeepsLoadedRecord);
                    Assert.IsNull(fixture.SaveCurrent());
                    Assert.AreEqual(VersionedSaveWriteStatus.NotWritten, fixture.Handle.LastWriteStatus);
                }
                else
                {
                    Assert.IsTrue(fixture.Handle.KeepsLoadedRecord);
                    CollectionAssert.AreEqual(incoming, fixture.SaveCurrent());
                }
            }
        }

        [TestMethod]
        public void UnassociatedNoRecordRetainsProtectedSaveDemandEvenWithoutPriorCargo()
        {
            using (var fixture = new RuntimeBinding(true, false))
            lock (fixture.Gate)
            {
                Assert.IsFalse(fixture.Runtime.HasExternalCargoSaveDemand);
                NativeStorageState prior = fixture.CurrentState;
                fixture.Read(null, 0);
                Assert.IsTrue(fixture.Runtime.HasExternalCargoSaveDemand);
                Assert.IsFalse(fixture.Runtime.TryGetStock(fixture.Good, 1, out _, out _));
                Assert.AreSame(prior, fixture.CurrentState);
                Assert.IsNull(fixture.SaveCurrent());
                Assert.ThrowsExactly<InvalidDataException>(() => fixture.Runtime.FreezeProductionSave());
            }
        }

        [TestMethod]
        [DataRow(false, false, false)]
        [DataRow(false, true, false)]
        [DataRow(true, false, false)]
        [DataRow(true, true, false)]
        [DataRow(false, false, true)]
        [DataRow(false, true, true)]
        public void MatchingExecutingArchiveRetainsNormalSaveAndNewMapNoRecordBehavior(bool newMap, bool noRecord, bool editorMap)
        {
            using (var fixture = new RuntimeBinding(false))
            lock (fixture.Gate)
            {
                fixture.Begin(editorMap ? MissionStartKind.EditorLoaded :
                    newMap ? MissionStartKind.NewGame : MissionStartKind.LoadedSave);
                byte[] incoming = noRecord ? null : VersionedSaveRecordCodec.Encode(2, 2, Entries(State(true, out _)));
                fixture.Read(incoming, fixture.Session, editorMap ? VersionedSaveLoadSource.EditorMap :
                    newMap ? VersionedSaveLoadSource.MapToPlay : VersionedSaveLoadSource.SaveGame);
                Assert.IsTrue(fixture.Runtime.TryGetStock(fixture.Good, 1, out _, out string reason), reason);
                Assert.IsTrue(fixture.Runtime.IsCurrentWorldReady);
                Assert.AreEqual(!noRecord, fixture.CurrentState.HasCargoHistory);
                Assert.IsFalse(fixture.Handle.KeepsLoadedRecord);
                fixture.Ready();
                Assert.IsNotNull(fixture.SaveCurrent());
                Assert.AreEqual(VersionedSaveWriteStatus.Written, fixture.Handle.LastWriteStatus);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ANewExplicitBlankEditorWorldCanRecoverAfterAnUnassociatedArchive(bool noRecord)
        {
            using (var fixture = new RuntimeBinding(true))
            lock (fixture.Gate)
            {
                byte[] incoming = noRecord ? null : VersionedSaveRecordCodec.Encode(1, 1, Entries(State(false, out _)));
                fixture.Read(incoming, 0);
                Assert.IsFalse(fixture.Runtime.TryGetStock(fixture.Good, 1, out _, out _));
                object blocked = MissionLifecycleService.NativeWorld;
                fixture.Registry.Unload(); // Actual unload outside the closed archive drops its passive bytes.
                fixture.Begin(MissionStartKind.EditorCreated);
                Assert.AreNotSame(blocked, MissionLifecycleService.NativeWorld);
                Assert.IsTrue(fixture.Runtime.TryGetStock(fixture.Good, 1, out _, out string reason), reason);
                Assert.IsTrue(fixture.Runtime.IsCurrentWorldReady);
                Assert.IsFalse(fixture.CurrentState.HasCargoHistory);
                fixture.Ready();
                Assert.IsFalse(fixture.Handle.KeepsLoadedRecord);
                Assert.IsNotNull(fixture.SaveCurrent());
            }
        }
    }
}
