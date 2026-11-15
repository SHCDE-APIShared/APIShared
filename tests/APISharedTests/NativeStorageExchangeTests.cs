using System;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SHCDESE.Interop;

namespace APISharedTests
{
    // Actual storage state/private ledger and production commit helper, with only the checked
    // native backend replaced. Synthetic packed resources are real allocated Int32 memory;
    // these fixtures do not execute game code or establish installed hook compatibility.
    [TestClass]
    public unsafe sealed class NativeStorageExchangeTests
    {
        private sealed class Backend : IGoodsWalletBackend, IDisposable
        {
            private readonly IntPtr buffer = Marshal.AllocHGlobal(22588 * 8);
            internal object Gate = new object(), CurrentWorld = new object();
            internal bool Ready = true, Allowed = true, Missing, ObserveWrites;
            internal int InitialGold, Resolves, AfterStoreCallbacks;
            internal Action BeforeResolve, BeforeWorld;
            internal Backend() { for (int player = 1; player <= 8; player++) *Cell(player) = 0; }
            private int* Cell(int player) => GoodsWalletNative.GoldCell((GamePlayerResources*)((byte*)buffer + (player - 1) * 22588));
            internal int Gold { get => *Cell(1); set => *Cell(1) = value; }
            private void Touch() { if (ObserveWrites && Gold != InitialGold) AfterStoreCallbacks++; }
            public bool IsReady { get { Touch(); return Ready; } }
            public object EngineMonitor { get { Touch(); return Gate; } }
            public object World { get { Touch(); BeforeWorld?.Invoke(); return CurrentWorld; } }
            public bool HasAuthorityContext { get { Touch(); return Allowed; } }
            public bool TryGetGoldCell(int player, out int* cell)
            {
                Touch(); Resolves++; BeforeResolve?.Invoke();
                cell = Missing ? null : Cell(player);
                return cell != null;
            }
            public void Dispose() => Marshal.FreeHGlobal(buffer);
        }
        private static NativeStorageState Setup(out CustomGoodId good)
        {
            var state = new NativeStorageState(); good = new CustomGoodId("storage.test", "ore");
            Assert.IsTrue(state.TryRegister(good, StoragePool.Armory, out string reason), reason);
            return state;
        }
        private static NativeStorageProbe Probe(int slot = 1, uint global = 10, int player = 1,
            StoragePool kind = StoragePool.Armory, int native = 0, int capacity = 50) =>
            new NativeStorageProbe(true, slot, global, player, kind, native, capacity);
        private static NativeStorageSnapshot Observe(Backend backend, NativeStorageProbe probe) =>
            new NativeStorageSnapshot(backend.CurrentWorld, probe, 0);
        private static void AssertStock(NativeStorageState state, CustomGoodId good, NativeStorageProbe probe, int quantity, long sequence)
        {
            Assert.IsTrue(state.TryGetBalance(good, probe.PlayerId, out GoodsBalance balance));
            Assert.AreEqual(quantity, balance.Quantity); Assert.AreEqual(0, balance.Reserved);
            Assert.AreEqual(quantity, state.GetQuantity(good, probe));
            Assert.AreEqual(quantity, state.GetOccupied(probe.Slot, probe.GlobalId, probe.PlayerId, probe.Kind));
            Assert.AreEqual(sequence, state.GetLastSequence(good.OwnerGuid));
        }
        private static (byte[] Goods, byte[] Stores) Capture(NativeStorageState state)
        { state.Capture(out byte[] goods, out byte[] stores); return (goods, stores); }
        private static void AssertUnchanged((byte[] Goods, byte[] Stores) before, NativeStorageState state)
        {
            var after = Capture(state);
            CollectionAssert.AreEqual(before.Goods, after.Goods); CollectionAssert.AreEqual(before.Stores, after.Stores);
        }

        [TestMethod]
        [DataRow(-7, false)]
        [DataRow(-7, true)]
        [DataRow(0, true)]
        public void ZeroGoldDeliveryAndConsumptionNeverResolveOrWriteWalletEvenWithDebt(int debt, bool missing)
        {
            var state = Setup(out var good); var probe = Probe();
            using (var backend = new Backend())
            {
                backend.Gold = debt; backend.Missing = missing;
                backend.BeforeResolve = () => Assert.Fail("A zero-gold storage operation must not ask for a wallet pointer.");
                var engine = new NativeStorageExchangeEngine(backend); var expected = Observe(backend, probe);
                lock (backend.Gate)
                {
                    Assert.IsTrue(engine.TryExchange(state, good, expected, probe, 1, 2, 0, out var status, out string reason), reason);
                    Assert.AreEqual(GoodsTransactionStatus.Applied, status); AssertStock(state, good, probe, 2, 1);
                    Assert.IsTrue(engine.TryExchange(state, good, expected, probe, 2, -1, 0, out status, out reason), reason);
                    Assert.AreEqual(GoodsTransactionStatus.Applied, status); AssertStock(state, good, probe, 1, 2);
                }
                Assert.AreEqual(debt, backend.Gold); Assert.AreEqual(0, backend.Resolves);
            }
        }

        [TestMethod]
        public void GoldPrivateLedgerAndPhysicalRowsApplyOnceAndRetainedRetrySurvivesReconciledDestruction()
        {
            var state = Setup(out var good); var probe = Probe();
            using (var backend = new Backend())
            {
                backend.Gold = 20; var engine = new NativeStorageExchangeEngine(backend); var expected = Observe(backend, probe);
                lock (backend.Gate)
                {
                    Assert.IsTrue(engine.TryExchange(state, good, expected, probe, 1, 2, -5, out var status, out string reason), reason);
                    Assert.AreEqual(GoodsTransactionStatus.Applied, status); Assert.AreEqual(15, backend.Gold); AssertStock(state, good, probe, 2, 1);
                    Assert.IsTrue(state.Reconcile(_ => default, out NativeStorageState reconciled, out reason), reason);
                    state = reconciled; AssertStock(state, good, probe, 0, 2);
                    var beforeRetry = Capture(state); int resolves = backend.Resolves;
                    backend.Gold = -10; backend.Missing = true;
                    Assert.IsTrue(engine.TryExchange(state, good, expected, default, 1, 2, -5, out status, out reason), reason);
                    Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, status);
                    Assert.AreEqual(resolves, backend.Resolves); Assert.AreEqual(-10, backend.Gold); AssertUnchanged(beforeRetry, state);
                    Assert.IsFalse(engine.TryExchange(state, good, expected, default, 3, 1, 0, out status, out reason));
                    Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status); AssertUnchanged(beforeRetry, state);
                }
            }
        }

        [TestMethod]
        [DataRow("funds", GoodsTransactionStatus.InsufficientStock)]
        [DataRow("overflow", GoodsTransactionStatus.CapacityExceeded)]
        [DataRow("missing", GoodsTransactionStatus.InvalidRequest)]
        [DataRow("throw", GoodsTransactionStatus.InvalidRequest)]
        public void FailureAfterPreparationLeavesMoneyLedgerRowsAndSequenceUnchanged(string failure, GoodsTransactionStatus rejected)
        {
            var state = Setup(out var good); var probe = Probe(); var before = Capture(state);
            using (var backend = new Backend())
            {
                backend.Gold = failure == "funds" ? 4 : failure == "overflow" ? int.MaxValue : 20;
                backend.Missing = failure == "missing";
                if (failure == "throw") backend.BeforeResolve = () => throw new InvalidOperationException("Synthetic pointer lookup failure.");
                int initial = backend.Gold, delta = failure == "overflow" ? 1 : -5;
                var engine = new NativeStorageExchangeEngine(backend); var expected = Observe(backend, probe);
                lock (backend.Gate)
                {
                    Assert.IsFalse(engine.TryExchange(state, good, expected, probe, 1, 1, delta, out var status, out string reason));
                    Assert.AreEqual(rejected, status); Assert.IsFalse(string.IsNullOrEmpty(reason));
                    Assert.AreEqual(initial, backend.Gold); AssertUnchanged(before, state); AssertStock(state, good, probe, 0, 0);
                    backend.Missing = false; backend.BeforeResolve = null; backend.Gold = 20;
                    Assert.IsTrue(engine.TryExchange(state, good, expected, probe, 1, 1, delta, out status, out reason), reason);
                    Assert.AreEqual(GoodsTransactionStatus.Applied, status); Assert.AreEqual(20 + delta, backend.Gold); AssertStock(state, good, probe, 1, 1);
                }
            }
        }

        [TestMethod]
        [DataRow("world")]
        [DataRow("slot")]
        [DataRow("global")]
        [DataRow("player")]
        [DataRow("pool")]
        [DataRow("dead")]
        public void FreshApplicationRequiresExactCurrentWorldSlotInstanceOwnerAndPool(string mismatch)
        {
            var state = Setup(out var good); var probe = Probe(); var before = Capture(state);
            using (var backend = new Backend())
            {
                backend.Gold = 20; var engine = new NativeStorageExchangeEngine(backend); var expected = Observe(backend, probe);
                NativeStorageProbe current = mismatch == "slot" ? Probe(slot: 2) : mismatch == "global" ? Probe(global: 11) :
                    mismatch == "player" ? Probe(player: 2) : mismatch == "pool" ? Probe(kind: StoragePool.Granary, capacity: 250) :
                    mismatch == "dead" ? default : probe;
                if (mismatch == "world") backend.CurrentWorld = new object();
                lock (backend.Gate)
                {
                    Assert.IsFalse(engine.TryExchange(state, good, expected, current, 1, 1, -5, out var status, out _));
                    Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status);
                }
                Assert.AreEqual(0, backend.Resolves); Assert.AreEqual(20, backend.Gold); AssertUnchanged(before, state);
            }
        }

        [TestMethod]
        [DataRow("price")]
        [DataRow("quantity")]
        [DataRow("slot")]
        [DataRow("global")]
        [DataRow("player")]
        [DataRow("pool")]
        public void RetainedSequenceRejectsChangedSemanticIntentBeforeAnyWalletLookup(string changed)
        {
            var state = Setup(out var good); var original = Probe();
            using (var backend = new Backend())
            {
                backend.Gold = 20; var engine = new NativeStorageExchangeEngine(backend); var expected = Observe(backend, original);
                lock (backend.Gate)
                {
                    Assert.IsTrue(engine.TryExchange(state, good, expected, original, 1, 1, -5, out _, out string reason), reason);
                    var before = Capture(state); int resolves = backend.Resolves;
                    var retry = changed == "slot" ? Probe(slot: 2) : changed == "global" ? Probe(global: 11) :
                        changed == "player" ? Probe(player: 2) : changed == "pool" ? Probe(kind: StoragePool.Granary, capacity: 250) : original;
                    Assert.IsFalse(engine.TryExchange(state, good, Observe(backend, retry), retry, 1,
                        changed == "quantity" ? 2 : 1, changed == "price" ? -6 : -5, out var status, out _));
                    Assert.AreEqual(GoodsTransactionStatus.Conflict, status); Assert.AreEqual(resolves, backend.Resolves);
                    Assert.AreEqual(15, backend.Gold); AssertUnchanged(before, state);
                }
            }
        }

        [TestMethod]
        public void FreshCapacityComesFromTheMatchingCurrentProbeRatherThanTheOldObservation()
        {
            var state = Setup(out var good); var original = Probe(); var current = Probe(native: 49);
            using (var backend = new Backend())
            {
                backend.Missing = true; var engine = new NativeStorageExchangeEngine(backend); var expected = Observe(backend, original);
                lock (backend.Gate)
                {
                    Assert.IsFalse(engine.TryExchange(state, good, expected, current, 1, 2, 0, out var status, out _));
                    Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, status); AssertStock(state, good, current, 0, 0);
                    Assert.IsTrue(engine.TryExchange(state, good, expected, current, 1, 1, 0, out status, out string reason), reason);
                    AssertStock(state, good, current, 1, 1); Assert.AreEqual(0, backend.Resolves);
                }
            }
        }

        [TestMethod]
        [DataRow("world")]
        [DataRow("authority")]
        [DataRow("ready")]
        [DataRow("gold")]
        public void FinalRevalidationRejectsContextOrWalletChangesWithoutPublishingPreparedRows(string change)
        {
            var state = Setup(out var good); var probe = Probe(); var before = Capture(state);
            using (var backend = new Backend())
            {
                backend.Gold = 20; var engine = new NativeStorageExchangeEngine(backend); var expected = Observe(backend, probe);
                backend.BeforeResolve = () =>
                {
                    if (change == "world") backend.CurrentWorld = new object();
                    if (change == "authority") backend.Allowed = false;
                    if (change == "ready") backend.Ready = false;
                    if (change == "gold") backend.BeforeWorld = () => backend.Gold = 19;
                };
                lock (backend.Gate)
                {
                    Assert.IsFalse(engine.TryExchange(state, good, expected, probe, 1, 1, -5, out var status, out _));
                    Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status); AssertUnchanged(before, state);
                    Assert.AreEqual(change == "gold" ? 19 : 20, backend.Gold);
                }
            }
        }

        [TestMethod]
        public void ReentrantBackendCannotMutateThePreparedOwnerAndFailureReleasesTheLease()
        {
            var state = Setup(out var good); var probe = Probe(); var before = Capture(state);
            using (var backend = new Backend())
            {
                backend.Gold = 20; var engine = new NativeStorageExchangeEngine(backend); var expected = Observe(backend, probe);
                backend.BeforeResolve = () =>
                {
                    Assert.IsFalse(engine.TryExchange(state, good, expected, probe, 2, 1, 0, out var nested, out _));
                    Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, nested);
                    Assert.IsFalse(state.TryRegister(new CustomGoodId("other", "good"), StoragePool.Armory, out _));
                    throw new InvalidOperationException("Synthetic failure after rejected reentry.");
                };
                lock (backend.Gate)
                {
                    Assert.IsFalse(engine.TryExchange(state, good, expected, probe, 1, 1, -5, out var status, out _));
                    Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status); AssertUnchanged(before, state); Assert.AreEqual(20, backend.Gold);
                    backend.BeforeResolve = null;
                    Assert.IsTrue(engine.TryExchange(state, good, expected, probe, 1, 1, -5, out status, out string reason), reason);
                    AssertStock(state, good, probe, 1, 1); Assert.AreEqual(15, backend.Gold);
                }
            }
        }

        [TestMethod]
        [DataRow("monitor")]
        [DataRow("gate")]
        [DataRow("ready")]
        [DataRow("authority")]
        [DataRow("world")]
        public void AdmissionRefusesUnheldMonitorUnavailableBoundaryAndUnknownAuthorityBeforeLookup(string failure)
        {
            var state = Setup(out var good); var probe = Probe(); var before = Capture(state);
            using (var backend = new Backend())
            {
                var engine = new NativeStorageExchangeEngine(backend); var expected = Observe(backend, probe); object gate = backend.Gate;
                Action verify = () =>
                {
                    Assert.IsFalse(engine.TryExchange(state, good, expected, probe, 1, 1, -1, out var status, out _));
                    Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status);
                };
                if (failure == "monitor") verify();
                else lock (gate)
                {
                    if (failure == "gate") backend.Gate = null;
                    if (failure == "ready") backend.Ready = false;
                    if (failure == "authority") backend.Allowed = false;
                    if (failure == "world") backend.CurrentWorld = null;
                    verify();
                }
                Assert.AreEqual(0, backend.Resolves); AssertUnchanged(before, state);
            }
        }

        [TestMethod]
        public void CommitWarmupTouchesOnlyIsolatedScratchAndSuccessfulPaymentCallsNoBackendAfterStore()
        {
            var state = Setup(out var good); var before = Capture(state); var probe = Probe();
            using (var backend = new Backend())
            {
                backend.Gold = backend.InitialGold = 20; backend.ObserveWrites = true;
                NativeStorageExchangeEngine.WarmCommitPath(); NativeStorageExchangeEngine.WarmCommitPath();
                Assert.AreEqual(20, backend.Gold); Assert.AreEqual(0, backend.Resolves); AssertUnchanged(before, state);
                var engine = new NativeStorageExchangeEngine(backend); var expected = Observe(backend, probe);
                lock (backend.Gate)
                {
                    Assert.IsTrue(engine.TryExchange(state, good, expected, probe, 1, 3, -5, out var status, out string reason), reason);
                    Assert.AreEqual(GoodsTransactionStatus.Applied, status); AssertStock(state, good, probe, 3, 1);
                }
                Assert.AreEqual(15, backend.Gold); Assert.AreEqual(1, backend.Resolves);
                Assert.AreEqual(0, backend.AfterStoreCallbacks, "The trusted backend must not be called inside or after the commit aperture.");
            }
        }

        [TestMethod]
        public void CompiledCommitContainsOnlyAnOptionalDirectStoreAndKnownPreparedPublication()
        {
            using (var assembly = AssemblyDefinition.ReadAssembly(typeof(NativeStoredGood).Assembly.Location))
            {
                var code = assembly.MainModule.GetType("APIShared.Economy.NativeStorageExchangeEngine").Methods
                    .Single(method => method.Name == "PublishGoldAndStorage").Body.Instructions;
                Assert.AreEqual(1, code.Count(instruction => instruction.OpCode.Code == Code.Stind_I4));
                var calls = code.Where(instruction => instruction.Operand is MethodReference)
                    .Select(instruction => ((MethodReference)instruction.Operand).FullName).ToArray();
                Assert.AreEqual(1, calls.Length); StringAssert.Contains(calls[0], "PreparedExchange::Publish()");
                Assert.IsFalse(code.Any(instruction => instruction.OpCode.Code == Code.Newobj || instruction.OpCode.Code == Code.Newarr));
            }
        }
    }
}
