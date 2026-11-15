using System;
using System.Linq;
using APIShared.Economy;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace APISharedTests
{
    [TestClass]
    public unsafe sealed class NativeStorageCargoEngineTests
    {
        private sealed class Backend : IGoodsWalletBackend
        {
            internal readonly object Gate = new object();
            internal object CurrentWorld = new object(), ObservedCargo;
            internal NativeStorageState ObservedState;
            internal bool Ready = true, Allowed = true;
            internal int WorldReads, AuthorityReads, GoldLookups, AfterPublicationCallbacks;
            internal Action<int> BeforeWorldRead, BeforeAuthorityRead;
            private void Touch()
            { if (ObservedState != null && !ReferenceEquals(ObservedCargo, ObservedState.CargoPublicationIdentity)) AfterPublicationCallbacks++; }
            public bool IsReady { get { Touch(); return Ready; } }
            public object EngineMonitor { get { Touch(); return Gate; } }
            public object World { get { Touch(); BeforeWorldRead?.Invoke(++WorldReads); return CurrentWorld; } }
            public bool HasAuthorityContext { get { Touch(); BeforeAuthorityRead?.Invoke(++AuthorityReads); return Allowed; } }
            public bool TryGetGoldCell(int player, out int* cell)
            { cell = null; GoldLookups++; throw new InvalidOperationException("An output-only cargo operation must never resolve native gold."); }
            internal void Observe(NativeStorageState state)
            { ObservedState = state; ObservedCargo = state.CargoPublicationIdentity; AfterPublicationCallbacks = 0; }
        }
        private static NativeStorageState Setup(out CustomGoodId good)
        {
            var state = new NativeStorageState(); good = new CustomGoodId("fish.owner", "fish");
            Assert.IsTrue(state.TryRegister(good, StoragePool.Granary, out string reason), reason); return state;
        }
        private static NativeStorageProbe Store() => new NativeStorageProbe(true, 1, 10, 1, StoragePool.Granary, 0, 250);
        private static void Stock(NativeStorageState state, CustomGoodId good, int quantity)
        { Assert.IsTrue(state.TryGetBalance(good, 1, out GoodsBalance balance)); Assert.AreEqual(quantity, balance.Quantity); Assert.AreEqual(quantity, state.GetQuantity(good, Store())); }
        private static NativeStorageState.CargoLot Issued(NativeStorageState state, CustomGoodId good)
        {
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareCreateCargo(good, 1, 1, 8, out var plan, out var lot, out string reason), reason);
            using (plan) plan.Publish(); return lot;
        }

        [TestMethod]
        public void ProductionCargoAndGranaryAllocationCommitTogetherWithoutAnyWalletLookupOrPostStoreCallback()
        {
            var state = Setup(out var good); var backend = new Backend(); var engine = new NativeStorageCargoEngine(backend);
            object world = backend.CurrentWorld;
            lock (backend.Gate)
            {
                backend.Observe(state);
                Assert.IsTrue(engine.TryCreate(state, good.OwnerGuid, good, world, true, 1, 1, 8, out var lot, out var status, out string reason), reason);
                Assert.AreEqual(GoodsTransactionStatus.Applied, status); Assert.AreEqual(8, lot.Remaining); Stock(state, good, 0);
                Assert.AreEqual(0, backend.AfterPublicationCallbacks);
                backend.Observe(state);
                Assert.IsTrue(engine.TryDeposit(state, good.OwnerGuid, lot, world, true, Store(), Store(), 3, out var next, out status, out reason), reason);
                Assert.AreEqual(GoodsTransactionStatus.Applied, status); Assert.AreEqual(5, next.Remaining); Stock(state, good, 3);
                Assert.AreEqual(0, backend.AfterPublicationCallbacks); Assert.AreEqual(0, backend.GoldLookups);
                backend.Observe(state);
                Assert.IsTrue(engine.TryDeposit(state, good.OwnerGuid, lot, world, true, Store(), default, 3, out next, out status, out reason), reason);
                Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, status); Assert.AreEqual(5, next.Remaining); Stock(state, good, 3);
                Assert.AreEqual(0, backend.GoldLookups);
            }
        }

        [TestMethod]
        [DataRow("multiplayer")]
        [DataRow("monitor")]
        [DataRow("ready")]
        [DataRow("authority")]
        [DataRow("world")]
        [DataRow("owner")]
        public void EngineAdmissionRejectsUnqualifiedContextsBeforeCreatingCargo(string invalid)
        {
            var state = Setup(out var good); var backend = new Backend(); var engine = new NativeStorageCargoEngine(backend);
            object world = backend.CurrentWorld; object before = state.CargoPublicationIdentity;
            backend.Ready = invalid != "ready"; backend.Allowed = invalid != "authority";
            bool Attempt() => engine.TryCreate(state, invalid == "owner" ? "other" : good.OwnerGuid, good,
                invalid == "world" ? new object() : world, invalid != "multiplayer", 1, 1, 8, out _, out _, out _);
            if (invalid == "monitor") Assert.IsFalse(Attempt()); else lock (backend.Gate) Assert.IsFalse(Attempt());
            Assert.AreSame(before, state.CargoPublicationIdentity); Assert.IsFalse(state.HasCargoHistory);
            Assert.AreEqual(0L, state.GetLastCargoSequence(good.OwnerGuid)); Assert.AreEqual(0, backend.GoldLookups);
        }

        [TestMethod]
        [DataRow("world")]
        [DataRow("authority")]
        [DataRow("exception")]
        public void FinalContextFailureDisposesARealPreparedLedgerAndCargoPlanWithoutPublication(string failure)
        {
            var state = Setup(out var good); var lot = Issued(state, good); var backend = new Backend(); var engine = new NativeStorageCargoEngine(backend);
            object before = state.CargoPublicationIdentity, world = backend.CurrentWorld;
            backend.BeforeWorldRead = count =>
            {
                if (count != 2) return;
                if (failure == "world") backend.CurrentWorld = new object();
                if (failure == "exception") throw new InvalidOperationException("Synthetic final context failure.");
            };
            backend.BeforeAuthorityRead = count => { if (count == 2 && failure == "authority") backend.Allowed = false; };
            lock (backend.Gate)
            {
                Assert.IsFalse(engine.TryDeposit(state, good.OwnerGuid, lot, world, true, Store(), Store(), 3, out var result, out var status, out string reason));
                Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status); Assert.IsFalse(string.IsNullOrEmpty(reason)); Assert.AreEqual(0, result.Remaining);
                Assert.AreSame(before, state.CargoPublicationIdentity); Stock(state, good, 0); Assert.AreEqual(0L, state.GetLastSequence(good.OwnerGuid));
                Assert.IsTrue(state.TryGetCargo(good.OwnerGuid, 1, out var current)); Assert.AreEqual(8, current.Remaining);
                backend.BeforeWorldRead = null; backend.BeforeAuthorityRead = null; backend.Allowed = true; backend.CurrentWorld = world;
                Assert.IsTrue(engine.TryDeposit(state, good.OwnerGuid, lot, world, true, Store(), Store(), 3, out result, out status, out reason), reason);
                Assert.AreEqual(5, result.Remaining); Stock(state, good, 3);
            }
        }

        [TestMethod]
        public void ReentrantBackendReadCannotPublishAnotherCargoCommandWhileAPlanIsPrepared()
        {
            var state = Setup(out var good); var lot = Issued(state, good); var backend = new Backend(); var engine = new NativeStorageCargoEngine(backend);
            object world = backend.CurrentWorld; bool attempted = false;
            backend.BeforeWorldRead = count =>
            {
                if (count != 2) return; attempted = true;
                Assert.IsFalse(engine.TryCreate(state, good.OwnerGuid, good, world, true, 2, 1, 1, out _, out var status, out _));
                Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status);
            };
            lock (backend.Gate)
            {
                Assert.IsTrue(engine.TryDeposit(state, good.OwnerGuid, lot, world, true, Store(), Store(), 3, out var result, out _, out string reason), reason);
                Assert.IsTrue(attempted); Assert.AreEqual(5, result.Remaining); Stock(state, good, 3);
                Assert.AreEqual(1L, state.GetLastCargoSequence(good.OwnerGuid));
            }
        }

        [TestMethod]
        public void OldWorldSnapshotsNeverBecomeValidMerelyBecauseTheSameCreationSequenceExistsAgain()
        {
            var state = Setup(out var good); var lot = Issued(state, good); var backend = new Backend(); var engine = new NativeStorageCargoEngine(backend);
            var old = new NativeCargoSnapshot(backend.CurrentWorld, lot); backend.CurrentWorld = new object();
            lock (backend.Gate)
            {
                Assert.IsFalse(engine.TryDiscard(state, good.OwnerGuid, old.Value, old.World, true, out _, out var status, out _));
                Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status);
                Assert.IsTrue(state.TryGetCargo(good.OwnerGuid, 1, out var current)); Assert.AreEqual(8, current.Remaining);
            }
        }

        [TestMethod]
        public void FrozenStorageCheckpointDetectsMetadataOnlyCargoChanges()
        {
            var state = Setup(out var good); object world = new object();
            var frozen = new NativeStorageProductionSave(state, world);
            Assert.IsTrue(frozen.Matches(state, world));
            object physical = state.PublicationIdentity; Issued(state, good);
            Assert.AreSame(physical, state.PublicationIdentity, "Issuing undelivered cargo must not invent physical storage rows.");
            Assert.IsFalse(frozen.Matches(state, world), "Frozen save identity must include cargo publication, even without any stored row change.");
        }

        [TestMethod]
        public void PendingOrPassiveCargoStateRequiresCriticalSaveWithoutAnyNativeProductionDemand()
        {
            var empty = Setup(out var good); var history = Setup(out _); var lot = Issued(history, good);
            Assert.IsFalse(NativeStorageRuntime.CargoSaveCheckpointRequired(true, false, empty, null));
            Assert.IsTrue(NativeStorageRuntime.CargoSaveCheckpointRequired(true, false, null, history),
                "A NativeLoaded observer may save before staged cargo was activated by a query/tick.");
            Assert.IsTrue(NativeStorageRuntime.CargoSaveCheckpointRequired(true, true, null, null),
                "Unknown/TooNew passive cargo must not take an unguarded successful raw-save path.");
            Assert.AreEqual(GoodsTransactionStatus.Applied, history.PrepareDiscardCargo(lot, out var discard, out _, out _));
            using (discard) discard.Publish();
            Assert.IsTrue(NativeStorageRuntime.CargoSaveCheckpointRequired(true, false, history, empty),
                "Terminal-only history still owns replay high-watermarks and qualified saving.");
            Assert.IsFalse(NativeStorageRuntime.CargoSaveCheckpointRequired(false, true, history, history));
        }

        [TestMethod]
        [DataRow(false, 0, true, true)]
        [DataRow(true, 0, true, false)]
        [DataRow(false, 1, true, false)]
        [DataRow(false, -1, true, false)]
        [DataRow(false, 0, false, false)]
        public void ActualRuntimeEntryPolicyRefusesFrozenSaveAddChainAndUnavailableSaveOwner(bool frozen, int depth, bool saveReady, bool expected)
        {
            Assert.AreEqual(expected, NativeStorageRuntime.CargoEntryAllowed(frozen, depth, saveReady, out string reason));
            Assert.AreEqual(expected, reason.Length == 0);
        }

        [TestMethod]
        [DataRow(true, false, true, false, true)]
        [DataRow(false, false, true, false, false)]
        [DataRow(true, true, true, false, false)]
        [DataRow(true, false, false, false, false)]
        [DataRow(true, false, true, true, false)]
        public void ActualRuntimeWorldPolicyRefusesUnknownMultiplayerReplacedAndPassiveWorlds(bool known, bool multiplayer, bool exact, bool passive, bool expected)
        {
            Assert.AreEqual(expected, NativeStorageRuntime.CargoWorldAllowed(known, multiplayer, exact, passive));
        }

        [TestMethod]
        public void ScratchWarmupIsIsolatedAndCompiledPublicationHasNoProducerOrBackendCallback()
        {
            var state = Setup(out _); object before = state.CargoPublicationIdentity;
            NativeStorageCargoEngine.WarmCommitPath(); NativeStorageCargoEngine.WarmCommitPath();
            Assert.AreSame(before, state.CargoPublicationIdentity); Assert.IsFalse(state.HasCargoHistory);
            using (var assembly = AssemblyDefinition.ReadAssembly(typeof(NativeStorageClient).Assembly.Location))
            {
                var code = assembly.MainModule.GetType("APIShared.Economy.NativeStorageCargoEngine").Methods.Single(method => method.Name == "Publish").Body.Instructions;
                var calls = code.Where(instruction => instruction.Operand is MethodReference).Select(instruction => ((MethodReference)instruction.Operand).FullName).ToArray();
                Assert.AreEqual(1, calls.Length); StringAssert.Contains(calls[0], "PreparedCargoChange::Publish()");
                Assert.IsFalse(code.Any(instruction => instruction.OpCode.Code == Code.Newobj || instruction.OpCode.Code == Code.Newarr));
            }
        }
    }
}
