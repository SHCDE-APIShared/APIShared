using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Production batch state, ledger and direct-store helper with only the trusted native
    // backend/probe reader replaced. Allocated Int32 cells exercise real pointer writes;
    // no game code, native hook or gameplay compatibility is established by this fixture.
    [TestClass]
    public unsafe sealed class NativeStorageBatchExchangeTests
    {
        private sealed class Backend : IGoodsWalletBackend, IDisposable
        {
            private readonly IntPtr buffer = Marshal.AllocHGlobal(8 * sizeof(int));
            internal object Gate = new object(), CurrentWorld = new object();
            internal bool Ready = true, Allowed = true, Missing, WatchWrites;
            internal int Resolves, InitialGold, AfterStoreCalls;
            internal Action BeforeResolve, BeforeWorld;
            internal Backend() { for (int player = 1; player <= 8; player++) *Cell(player) = 0; }
            private int* Cell(int player) => (int*)buffer + player - 1;
            internal int Gold { get => *Cell(1); set => *Cell(1) = value; }
            internal void Touch() { if (WatchWrites && Gold != InitialGold) AfterStoreCalls++; }
            public bool IsReady { get { Touch(); return Ready; } }
            public object EngineMonitor { get { Touch(); return Gate; } }
            public object World { get { Touch(); BeforeWorld?.Invoke(); return CurrentWorld; } }
            public bool HasAuthorityContext { get { Touch(); return Allowed; } }
            public bool TryGetGoldCell(int player, out int* cell)
            { Touch(); Resolves++; BeforeResolve?.Invoke(); cell = Missing ? null : Cell(player); return cell != null; }
            public void Dispose() => Marshal.FreeHGlobal(buffer);
        }

        private sealed class CallerList : IReadOnlyList<NativeStorageChange>
        {
            internal NativeStorageChange[] Rows;
            internal int CountReads, ItemReads, AdvertisedCount = -1;
            internal Action OnCount, OnItem;
            internal CallerList(params NativeStorageChange[] rows) { Rows = rows; }
            public int Count { get { CountReads++; OnCount?.Invoke(); return AdvertisedCount < 0 ? Rows.Length : AdvertisedCount; } }
            public NativeStorageChange this[int index] { get { ItemReads++; OnItem?.Invoke(); return Rows[index]; } }
            public IEnumerator<NativeStorageChange> GetEnumerator() => throw new InvalidOperationException("Caller enumeration is forbidden.");
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static NativeStorageProbe Store(int slot, uint gid = 10, int player = 1, StoragePool pool = StoragePool.Armory, int native = 0) =>
            new NativeStorageProbe(true, slot, gid, player, pool, native, pool == StoragePool.Armory ? 50 : 250);
        private static NativeStorageState Setup(out CustomGoodId good)
        {
            var state = new NativeStorageState(); good = new CustomGoodId("batch.owner", "armor");
            Assert.IsTrue(state.TryRegister(good, StoragePool.Armory, out string reason), reason); return state;
        }
        private static NativeStorageBatch Batch(Backend backend, CustomGoodId good, int gold, int left = 50, int right = 50)
        {
            Assert.IsTrue(NativeStorageBatch.TryCreate(good.OwnerGuid, backend.CurrentWorld,
                new[] { new NativeStorageBatchRow(good, Store(1), left), new NativeStorageBatchRow(good, Store(2, 20), right) },
                gold, out var batch, out string reason), reason); return batch;
        }
        private static NativeStorageProbe Read(int slot) => slot == 1 ? Store(1) : slot == 2 ? Store(2, 20) : Store(slot, (uint)(slot * 10));
        private static (byte[] Goods, byte[] Stores) Capture(NativeStorageState state)
        { state.Capture(out byte[] goods, out byte[] stores); return (goods, stores); }
        private static void Unchanged((byte[] Goods, byte[] Stores) before, NativeStorageState state)
        {
            var after = Capture(state); CollectionAssert.AreEqual(before.Goods, after.Goods); CollectionAssert.AreEqual(before.Stores, after.Stores);
        }
        private static void Stock(NativeStorageState state, CustomGoodId good, int total, int left, int right, long sequence)
        {
            Assert.IsTrue(state.TryGetBalance(good, 1, out var balance)); Assert.AreEqual(total, balance.Quantity); Assert.AreEqual(0, balance.Reserved);
            Assert.AreEqual(left, state.GetQuantity(good, Store(1))); Assert.AreEqual(right, state.GetQuantity(good, Store(2, 20)));
            Assert.AreEqual(sequence, state.GetLastSequence(good.OwnerGuid));
        }

        [TestMethod]
        public void HundredUnitTwoArmoryPurchaseAndSalePublishGoldLedgerAndBothStoresTogether()
        {
            var state = Setup(out var good);
            using (var backend = new Backend())
            {
                backend.Gold = 1000; var engine = new NativeStorageExchangeEngine(backend); var buy = Batch(backend, good, -1000);
                lock (backend.Gate)
                {
                    Assert.IsTrue(engine.TryExchangeBatch(state, buy, NativeStorageExchangeEngine.ReadBatchProbes(buy, Read), 1, Read, out var status, out string reason), reason);
                    Assert.AreEqual(GoodsTransactionStatus.Applied, status); Assert.AreEqual(0, backend.Gold); Stock(state, good, 100, 50, 50, 1);
                    var sell = Batch(backend, good, 1000, -50, -50);
                    Assert.IsTrue(engine.TryExchangeBatch(state, sell, NativeStorageExchangeEngine.ReadBatchProbes(sell, Read), 2, Read, out status, out reason), reason);
                    Assert.AreEqual(GoodsTransactionStatus.Applied, status); Assert.AreEqual(1000, backend.Gold); Stock(state, good, 0, 0, 0, 2);
                }
            }
        }

        [TestMethod]
        [DataRow("last-store", GoodsTransactionStatus.CapacityExceeded)]
        [DataRow("funds", GoodsTransactionStatus.InsufficientStock)]
        [DataRow("overflow", GoodsTransactionStatus.CapacityExceeded)]
        [DataRow("lookup", GoodsTransactionStatus.InvalidRequest)]
        [DataRow("throw", GoodsTransactionStatus.InvalidRequest)]
        public void FailureDoesNotPublishAnyRequestedRowOrChargeAndSameSequenceCanRetry(string fault, GoodsTransactionStatus rejected)
        {
            var state = Setup(out var good); var before = Capture(state);
            using (var backend = new Backend())
            {
                backend.Gold = fault == "funds" ? 999 : fault == "overflow" ? int.MaxValue : 1000;
                backend.Missing = fault == "lookup";
                if (fault == "throw") backend.BeforeResolve = () => throw new InvalidOperationException("Synthetic wallet failure.");
                int initial = backend.Gold; var engine = new NativeStorageExchangeEngine(backend);
                var batch = Batch(backend, good, fault == "overflow" ? 1 : -1000);
                var current = NativeStorageExchangeEngine.ReadBatchProbes(batch, Read);
                if (fault == "last-store") current[1] = Store(2, 20, native: 1);
                lock (backend.Gate)
                {
                    Assert.IsFalse(engine.TryExchangeBatch(state, batch, current, 1, Read, out var status, out string reason));
                    Assert.AreEqual(rejected, status); Assert.IsFalse(string.IsNullOrEmpty(reason));
                    Assert.AreEqual(initial, backend.Gold); Unchanged(before, state); Stock(state, good, 0, 0, 0, 0);
                    backend.BeforeResolve = null; backend.Missing = false; backend.Gold = fault == "overflow" ? 0 : 1000;
                    Assert.IsTrue(engine.TryExchangeBatch(state, batch, NativeStorageExchangeEngine.ReadBatchProbes(batch, Read), 1, Read, out status, out reason), reason);
                    Stock(state, good, 100, 50, 50, 1);
                }
            }
        }

        [TestMethod]
        public void FreeBatchIgnoresDebtAndMissingWalletAndNeverCallsTheLookup()
        {
            var state = Setup(out var good);
            using (var backend = new Backend())
            {
                backend.Gold = -50; backend.Missing = true; backend.BeforeResolve = () => Assert.Fail("Free batches have no wallet lookup.");
                var engine = new NativeStorageExchangeEngine(backend); var batch = Batch(backend, good, 0);
                lock (backend.Gate)
                    Assert.IsTrue(engine.TryExchangeBatch(state, batch, NativeStorageExchangeEngine.ReadBatchProbes(batch, Read), 1, Read, out _, out string reason), reason);
                Assert.AreEqual(-50, backend.Gold); Assert.AreEqual(0, backend.Resolves); Stock(state, good, 100, 50, 50, 1);
            }
        }

        [TestMethod]
        public void RetainedReceiptPrecedesLiveMaintenanceAndWalletAndChangedDistributionOrPriceConflicts()
        {
            var state = Setup(out var good);
            using (var backend = new Backend())
            {
                backend.Gold = 1000; var engine = new NativeStorageExchangeEngine(backend); var batch = Batch(backend, good, -1000);
                lock (backend.Gate)
                {
                    Assert.IsTrue(engine.TryExchangeBatch(state, batch, NativeStorageExchangeEngine.ReadBatchProbes(batch, Read), 1, Read, out _, out string reason), reason);
                    // Fill the actual bounded journal without overfilling physical storage. Loss
                    // maintenance would consume sequence1025 and evict the first receipt.
                    for (int sequence = 2; sequence <= 1024; sequence++)
                    {
                        int delta = sequence % 2 == 0 ? 1 : -1;
                        Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareExchange(good, sequence, Read(3), delta, 0, out var plan, out reason), reason);
                        using (plan) plan.Publish();
                    }
                    var before = Capture(state); int resolves = backend.Resolves; backend.Gold = -10; backend.Missing = true;
                    Assert.IsTrue(NativeStorageBatch.TryCreate(good.OwnerGuid, backend.CurrentWorld,
                        new[] { batch[1], batch[0] }, -1000, out var reordered, out reason), reason);
                    Assert.IsTrue(engine.ProbeBatchReceipt(state, reordered, 1, out var status, out reason), reason);
                    Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, status); Assert.AreEqual(resolves, backend.Resolves); Unchanged(before, state);
                    foreach (var changed in new[] { Batch(backend, good, -999), Batch(backend, good, -1000, 49, 51) })
                    {
                        Assert.IsTrue(engine.ProbeBatchReceipt(state, changed, 1, out status, out reason));
                        Assert.AreEqual(GoodsTransactionStatus.Conflict, status); Assert.AreEqual(resolves, backend.Resolves); Unchanged(before, state);
                    }
                    Assert.IsTrue(state.Reconcile(_ => default, out var reconciled, out reason), reason);
                    Assert.IsTrue(engine.ProbeBatchReceipt(reconciled, reordered, 1, out status, out reason));
                    Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, status, "An already evicted receipt must not be reexecuted or asserted successful.");
                }
            }
        }

        [TestMethod]
        [DataRow("native")]
        [DataRow("global")]
        [DataRow("owner")]
        [DataRow("pool")]
        [DataRow("dead")]
        [DataRow("world")]
        [DataRow("authority")]
        [DataRow("ready")]
        [DataRow("gold")]
        [DataRow("reader")]
        public void ChangesAfterPrepareRejectTheWholeBatchBeforePublication(string fault)
        {
            var state = Setup(out var good); var before = Capture(state);
            using (var backend = new Backend())
            {
                backend.Gold = 1000; var engine = new NativeStorageExchangeEngine(backend); var batch = Batch(backend, good, -1000);
                var finalSecond = Read(2);
                backend.BeforeResolve = () =>
                {
                    if (fault == "native") finalSecond = Store(2, 20, native: 1);
                    if (fault == "global") finalSecond = Store(2, 21);
                    if (fault == "owner") finalSecond = Store(2, 20, player: 2);
                    if (fault == "pool") finalSecond = Store(2, 20, pool: StoragePool.Granary);
                    if (fault == "dead") finalSecond = default;
                    if (fault == "world") backend.CurrentWorld = new object();
                    if (fault == "authority") backend.Allowed = false;
                    if (fault == "ready") backend.Ready = false;
                    if (fault == "gold") backend.BeforeWorld = () => backend.Gold = 999;
                };
                lock (backend.Gate)
                {
                    Assert.IsFalse(engine.TryExchangeBatch(state, batch, NativeStorageExchangeEngine.ReadBatchProbes(batch, Read), 1,
                        slot => fault == "reader" && slot == 2 ? throw new InvalidOperationException("Synthetic final probe failure.") :
                            slot == 2 ? finalSecond : Read(slot), out var status, out string reason));
                    Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status); Assert.IsFalse(string.IsNullOrEmpty(reason)); Unchanged(before, state);
                }
                Assert.AreEqual(fault == "gold" ? 999 : 1000, backend.Gold);
            }
        }

        [TestMethod]
        public void ReentrantPreparedBatchIsRejectedAndFailureDisposesTheSingleLease()
        {
            var state = Setup(out var good); var before = Capture(state);
            using (var backend = new Backend())
            {
                backend.Gold = 1000; var engine = new NativeStorageExchangeEngine(backend); var batch = Batch(backend, good, -1000);
                var probes = NativeStorageExchangeEngine.ReadBatchProbes(batch, Read);
                backend.BeforeResolve = () =>
                {
                    Assert.IsFalse(engine.TryExchangeBatch(state, batch, probes, 2, Read, out var nested, out _));
                    Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, nested);
                    Assert.IsFalse(state.TryRegister(new CustomGoodId("other", "good"), StoragePool.Armory, out _));
                    throw new InvalidOperationException("Stop after rejected reentry.");
                };
                lock (backend.Gate)
                {
                    Assert.IsFalse(engine.TryExchangeBatch(state, batch, probes, 1, Read, out _, out _)); Unchanged(before, state); Assert.AreEqual(1000, backend.Gold);
                    backend.BeforeResolve = null;
                    Assert.IsTrue(engine.TryExchangeBatch(state, batch, probes, 1, Read, out _, out string reason), reason);
                    Stock(state, good, 100, 50, 50, 1);
                }
            }
        }

        [TestMethod]
        public void BatchWarmupIsScratchOnlyAndSuccessfulCommitRunsNoReaderOrBackendAfterGoldStore()
        {
            var state = Setup(out var good); var before = Capture(state);
            using (var backend = new Backend())
            {
                backend.Gold = backend.InitialGold = 1000; backend.WatchWrites = true;
                NativeStorageExchangeEngine.WarmBatchCommitPath(); NativeStorageExchangeEngine.WarmBatchCommitPath(); Unchanged(before, state);
                Assert.AreEqual(1000, backend.Gold); Assert.AreEqual(0, backend.Resolves);
                var engine = new NativeStorageExchangeEngine(backend); var batch = Batch(backend, good, -1000);
                int reads = 0;
                Func<int, NativeStorageProbe> reader = slot => { backend.Touch(); reads++; return Read(slot); };
                lock (backend.Gate)
                    Assert.IsTrue(engine.TryExchangeBatch(state, batch, NativeStorageExchangeEngine.ReadBatchProbes(batch, reader), 1, reader, out _, out string reason), reason);
                Assert.AreEqual(4, reads); Assert.AreEqual(1, backend.Resolves); Assert.AreEqual(0, backend.AfterStoreCalls);
                Assert.AreEqual(0, backend.Gold); Stock(state, good, 100, 50, 50, 1);
            }
        }

        [TestMethod]
        public void SeveralGoodsInOneStoreShareOneProbeAndValidateFinalRatherThanIntermediateOccupancy()
        {
            var state = Setup(out var good); var second = new CustomGoodId(good.OwnerGuid, "second");
            Assert.IsTrue(state.TryRegister(second, StoragePool.Armory, out string reason), reason);
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareExchange(good, 1, Store(1), 50, 0, out var seed, out reason), reason);
            using (seed) seed.Publish();
            using (var backend = new Backend())
            {
                backend.Gold = 20;
                var rows = new[] { new NativeStorageBatchRow(good, Store(1), -50), new NativeStorageBatchRow(second, Store(1), 50),
                    new NativeStorageBatchRow(second, Store(2, 20), 1) };
                Assert.IsTrue(NativeStorageBatch.TryCreate(good.OwnerGuid, backend.CurrentWorld, rows, -20, out var batch, out reason), reason);
                // Existing native overfill may drain/rearrange an equal virtual claim. No
                // intermediate +50 prefix or another good's stock can satisfy a withdrawal.
                int reads = 0;
                Func<int, NativeStorageProbe> reader = slot => { reads++; return slot == 1 ? Store(1, native: 10) : Read(slot); };
                var current = NativeStorageExchangeEngine.ReadBatchProbes(batch, reader);
                Assert.AreEqual(2, reads);
                var engine = new NativeStorageExchangeEngine(backend);
                lock (backend.Gate)
                    Assert.IsTrue(engine.TryExchangeBatch(state, batch, current, 2, reader, out _, out reason), reason);
                Assert.AreEqual(4, reads); Assert.AreEqual(0, backend.Gold);
                Assert.AreEqual(0, state.GetQuantity(good, Store(1))); Assert.AreEqual(50, state.GetQuantity(second, Store(1)));
                Assert.AreEqual(1, state.GetQuantity(second, Store(2, 20))); Assert.AreEqual(50, state.GetOccupied(1, 10, 1, StoragePool.Armory));
            }
        }

        [TestMethod]
        public void RetainedBatchStillRequiresTheSameWorldAtTheEndOfReceiptChecking()
        {
            var state = Setup(out var good);
            using (var backend = new Backend())
            {
                var engine = new NativeStorageExchangeEngine(backend); var batch = Batch(backend, good, 0, 1, 1);
                lock (backend.Gate)
                {
                    Assert.IsTrue(engine.TryExchangeBatch(state, batch, NativeStorageExchangeEngine.ReadBatchProbes(batch, Read), 1, Read, out _, out string reason), reason);
                    var before = Capture(state); int worlds = 0;
                    backend.BeforeWorld = () => { if (++worlds == 2) backend.CurrentWorld = new object(); };
                    Assert.IsTrue(engine.ProbeBatchReceipt(state, batch, 1, out var status, out reason));
                    Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status); Unchanged(before, state); Assert.AreEqual(0, backend.Resolves);
                }
            }
        }

        [TestMethod]
        public void CallerListIsCopiedOnceIntoOwnedRowsAndNeverEnumeratedOrRevisited()
        {
            var state = Setup(out var good); var handle = new NativeStoredGood(good, StoragePool.Armory);
            using (var backend = new Backend())
            {
                var list = new CallerList(new NativeStorageChange(handle, new NativeStorageSnapshot(backend.CurrentWorld, Store(1), 0), 50),
                    new NativeStorageChange(handle, new NativeStorageSnapshot(backend.CurrentWorld, Store(2, 20), 0), 50));
                Assert.IsTrue(NativeStorageRuntime.TryCopyBatch(good.OwnerGuid, backend.CurrentWorld, list, new[] { handle }, 0, out var batch, out string reason), reason);
                list.Rows[0] = default; list.OnCount = () => Assert.Fail("Count was revisited."); list.OnItem = () => Assert.Fail("An indexed value was revisited.");
                var engine = new NativeStorageExchangeEngine(backend);
                lock (backend.Gate)
                    Assert.IsTrue(engine.TryExchangeBatch(state, batch, NativeStorageExchangeEngine.ReadBatchProbes(batch, Read), 1, Read, out _, out reason), reason);
                Assert.AreEqual(1, list.CountReads); Assert.AreEqual(2, list.ItemReads); Stock(state, good, 100, 50, 50, 1);
            }
        }

        [TestMethod]
        [DataRow("count")]
        [DataRow("item")]
        [DataRow("too-many")]
        [DataRow("empty")]
        [DataRow("handle")]
        [DataRow("owner")]
        [DataRow("world")]
        [DataRow("pool")]
        [DataRow("default")]
        public void InvalidOrThrowingCallerRowsFailBeforeAnyLeaseOrNativeLookup(string fault)
        {
            var state = Setup(out var good); var before = Capture(state); object world = new object();
            var handle = new NativeStoredGood(good, StoragePool.Armory);
            var supplied = fault == "handle" ? new NativeStoredGood(good, StoragePool.Armory) : handle;
            var snapshot = fault == "default" ? default : new NativeStorageSnapshot(fault == "world" ? new object() : world,
                Store(1, pool: fault == "pool" ? StoragePool.Granary : StoragePool.Armory), 0);
            var list = new CallerList(new NativeStorageChange(supplied, snapshot, 1));
            if (fault == "count") list.OnCount = () => throw new InvalidOperationException("Count failure.");
            if (fault == "item") list.OnItem = () => throw new InvalidOperationException("Getter failure.");
            if (fault == "too-many") list.AdvertisedCount = 65;
            if (fault == "empty") list.AdvertisedCount = 0;
            Assert.IsFalse(NativeStorageRuntime.TryCopyBatch(fault == "owner" ? "other.owner" : good.OwnerGuid,
                world, list, new[] { handle }, 0, out var batch, out string reason));
            Assert.IsNull(batch); Assert.IsFalse(string.IsNullOrEmpty(reason)); Unchanged(before, state); Assert.AreEqual(1, list.CountReads);
        }

        [TestMethod]
        public void ReentrantWorldReplacementDuringCallerCopyCannotApplyTheOldPayload()
        {
            var state = Setup(out var good); var handle = new NativeStoredGood(good, StoragePool.Armory); var before = Capture(state);
            using (var backend = new Backend())
            {
                object originalWorld = backend.CurrentWorld;
                var list = new CallerList(new NativeStorageChange(handle, new NativeStorageSnapshot(originalWorld, Store(1), 0), 1));
                list.OnCount = () => backend.CurrentWorld = new object();
                Assert.IsTrue(NativeStorageRuntime.TryCopyBatch(good.OwnerGuid, originalWorld, list, new[] { handle }, 0, out var batch, out string reason), reason);
                var engine = new NativeStorageExchangeEngine(backend);
                lock (backend.Gate)
                {
                    Assert.IsTrue(engine.ProbeBatchReceipt(state, batch, 1, out var status, out reason));
                    Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status);
                    Assert.IsFalse(engine.TryExchangeBatch(state, batch, new[] { Store(1) }, 1, _ => throw new InvalidOperationException("Old-world native read."), out status, out reason));
                }
                Unchanged(before, state); Assert.AreEqual(0, backend.Resolves);
            }
        }

        [TestMethod]
        [DataRow("monitor")]
        [DataRow("gate")]
        [DataRow("ready")]
        [DataRow("authority")]
        [DataRow("world")]
        public void BatchAdmissionRetainsTheExistingEngineAndAuthorityBoundary(string fault)
        {
            var state = Setup(out var good); var before = Capture(state);
            using (var backend = new Backend())
            {
                var batch = Batch(backend, good, -1, 1, 1); var engine = new NativeStorageExchangeEngine(backend); object gate = backend.Gate;
                Action verify = () =>
                {
                    Assert.IsTrue(engine.ProbeBatchReceipt(state, batch, 1, out var status, out _)); Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status);
                    Assert.IsFalse(engine.TryExchangeBatch(state, batch, new[] { Read(1), Read(2) }, 1, _ => throw new InvalidOperationException("Forbidden native read."), out status, out _));
                    Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status);
                };
                if (fault == "monitor") verify();
                else lock (gate)
                {
                    if (fault == "gate") backend.Gate = null;
                    if (fault == "ready") backend.Ready = false;
                    if (fault == "authority") backend.Allowed = false;
                    if (fault == "world") backend.CurrentWorld = null;
                    verify();
                }
                Assert.AreEqual(0, backend.Resolves); Unchanged(before, state);
            }
        }

        [TestMethod]
        [DataRow(true, 0)]
        [DataRow(false, 1)]
        [DataRow(false, -1)]
        public void FrozenCheckpointOrAddGoodsChainRejectsBatchEntry(bool frozen, int depth)
        {
            Assert.IsFalse(NativeStorageRuntime.BatchEntryAllowed(frozen, depth, out string reason)); Assert.IsFalse(string.IsNullOrEmpty(reason));
            Assert.IsTrue(NativeStorageRuntime.BatchEntryAllowed(false, 0, out reason)); Assert.AreEqual(string.Empty, reason);
        }
    }
}
