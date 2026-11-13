using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using APIShared.Economy;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    // Actual pure state/Content ledger paths, with native building reads represented by values.
    // These tests do not establish installed native-hook, engine-lock or gold-write correctness.
    [TestClass]
    public class NativeStorageStateTests
    {
        public TestContext TestContext { get; set; }

        private static CustomGoodId Register(NativeStorageState state, string owner, string local = "ore", StoragePool pool = StoragePool.Armory)
        {
            var good = new CustomGoodId(owner, local);
            Assert.IsTrue(state.TryRegister(good, pool, out string reason), reason);
            return good;
        }
        private static NativeStorageProbe Probe(int slot = 1, uint globalId = 10, int player = 1,
            StoragePool kind = StoragePool.Armory, int nativeUsed = 0, int capacity = 50)
            => new NativeStorageProbe(true, slot, globalId, player, kind, nativeUsed, capacity);
        private static void Apply(NativeStorageState state, CustomGoodId good, long sequence, NativeStorageProbe building, int delta, int gold = 0)
        {
            GoodsTransactionStatus status = state.PrepareExchange(good, sequence, building, delta, gold,
                out NativeStorageState.PreparedExchange plan, out string reason);
            Assert.AreEqual(GoodsTransactionStatus.Applied, status, reason);
            using (plan) { Assert.IsTrue(plan.IsPrepared); plan.Publish(); }
        }
        private static GoodsBalance Balance(NativeStorageState state, CustomGoodId good, int player = 1)
        {
            Assert.IsTrue(state.TryGetBalance(good, player, out GoodsBalance result));
            Assert.AreEqual(0, result.Reserved);
            return result;
        }
        private static (byte[] Goods, byte[] Stores) Capture(NativeStorageState state)
        { state.Capture(out byte[] goods, out byte[] stores); return (goods, stores); }
        private static void EqualSnapshot((byte[] Goods, byte[] Stores) expected, NativeStorageState state)
        {
            var actual = Capture(state);
            CollectionAssert.AreEqual(expected.Goods, actual.Goods);
            CollectionAssert.AreEqual(expected.Stores, actual.Stores);
        }

        [TestMethod]
        public void DisposingPreparationLeavesBothLedgerAndPhysicalRowsUnchanged()
        {
            var state = new NativeStorageState();
            CustomGoodId good = Register(state, "third.party");
            NativeStorageProbe building = Probe();
            var before = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareExchange(good, 1, building, 3, -7, out var abandoned, out string reason), reason);
            Assert.IsTrue(abandoned.IsPrepared);
            Assert.AreEqual(0, state.GetOccupied(1, 10, 1, StoragePool.Armory));
            abandoned.Dispose();
            EqualSnapshot(before, state);
            Assert.AreEqual(0L, state.GetLastSequence(good.OwnerGuid));
            Apply(state, good, 1, building, 3, -8);
            Assert.IsTrue(state.HasRows); Assert.AreEqual(1, state.RowCount);
            Assert.AreEqual(3, Balance(state, good).Quantity);
            Assert.AreEqual(3, state.GetQuantity(good, building));
            Assert.AreEqual(3, state.GetOccupied(1, 10, 1, StoragePool.Armory));
        }

        [TestMethod]
        public void NativeAndAllOwnersVirtualGoodsShareOnePhysicalPool()
        {
            var state = new NativeStorageState();
            CustomGoodId first = Register(state, "first.owner"), second = Register(state, "second.owner");
            NativeStorageProbe building = Probe(nativeUsed: 30);
            Apply(state, first, 1, building, 12);
            Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, state.PrepareExchange(second, 1, building, 9, 0, out var rejected, out _));
            Assert.IsNull(rejected); Assert.AreEqual(0L, state.GetLastSequence(second.OwnerGuid));
            Apply(state, second, 1, building, 8);
            Assert.AreEqual(20, state.GetOccupied(1, 10, 1, StoragePool.Armory));
            Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, state.PrepareExchange(first, 2, Probe(nativeUsed: 31), 1, 0, out rejected, out _));
            Assert.IsNull(rejected);
            // Existing overfull storage may be drained; it may not accept an increase.
            Apply(state, first, 2, Probe(nativeUsed: 60), -2);
            Assert.AreEqual(10, Balance(state, first).Quantity);
            Assert.AreEqual(8, Balance(state, second).Quantity);
            Assert.AreEqual(18, state.GetOccupied(1, 10, 1, StoragePool.Armory));
        }

        [TestMethod]
        [DataRow(1, 0)]
        [DataRow(1, 51)]
        [DataRow(2, 50)]
        [DataRow(2, 251)]
        public void IncorrectPoolCapacityCannotAdmitANewExchangeOrChangeItsLedger(int pool, int capacity)
        {
            var kind = (StoragePool)pool;
            var state = new NativeStorageState();
            CustomGoodId good = Register(state, "third.party", pool: kind);
            int fixedCapacity = kind == StoragePool.Armory ? 50 : 250;
            Apply(state, good, 1, Probe(kind: kind, capacity: fixedCapacity), 1);
            var before = Capture(state);
            NativeStorageProbe wrong = Probe(kind: kind, capacity: capacity);
            Assert.IsFalse(wrong.IsLive);
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, state.PrepareExchange(good, 2, wrong, 1, 0, out var rejected, out _));
            Assert.IsNull(rejected); EqualSnapshot(before, state);
            Assert.AreEqual(1L, state.GetLastSequence(good.OwnerGuid));
            // A retained completion is still checked before the now-invalid live probe.
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, state.PrepareExchange(good, 1, wrong, 1, 0, out rejected, out _));
            Assert.IsNull(rejected); EqualSnapshot(before, state);
        }

        [TestMethod]
        [DataRow(1, 50)]
        [DataRow(2, 250)]
        public void ForeignNativeOverfillKeepsValidClaimsAndAllowsThemToDrain(int pool, int capacity)
        {
            var kind = (StoragePool)pool;
            var state = new NativeStorageState();
            CustomGoodId good = Register(state, "third.party", pool: kind);
            Apply(state, good, 1, Probe(kind: kind, capacity: capacity), 3);
            NativeStorageProbe overfull = Probe(kind: kind, nativeUsed: capacity + 100, capacity: capacity);
            Assert.IsTrue(overfull.IsLive);
            Assert.IsTrue(state.Reconcile(_ => overfull, out var candidate, out string reason), reason);
            Assert.AreSame(state, candidate);
            Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, state.PrepareExchange(good, 2, overfull, 1, 0, out var rejected, out _));
            Assert.IsNull(rejected);
            Apply(state, good, 2, overfull, -1);
            Assert.AreEqual(2, state.GetOccupied(1, 10, 1, kind));
            Assert.AreEqual(2, Balance(state, good).Quantity);
        }

        [TestMethod]
        public void ReceiptChecksPrecedeLiveBindingAndIncludeBuildingAndGoldIntent()
        {
            var state = new NativeStorageState();
            CustomGoodId good = Register(state, "third.party");
            Apply(state, good, 1, Probe(), 2, -5);
            var before = Capture(state);
            var gone = new NativeStorageProbe(false, 1, 10, 1, StoragePool.Armory, 0, 0);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, state.PrepareExchange(good, 1, gone, 2, -5, out var retry, out _));
            Assert.IsNull(retry);
            Assert.AreEqual(GoodsTransactionStatus.Conflict, state.PrepareExchange(good, 1, gone, 2, -6, out retry, out _));
            Assert.IsNull(retry);
            Assert.AreEqual(GoodsTransactionStatus.Conflict, state.PrepareExchange(good, 1, Probe(globalId: 11), 2, -5, out retry, out _));
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, state.PrepareExchange(good, 2, gone, 2, -5, out retry, out _));
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, state.PrepareExchange(good, 2, Probe(globalId: 11), 2, -5, out retry, out _));
            Assert.IsNull(retry);
            EqualSnapshot(before, state);
            Assert.AreEqual(0, state.GetOccupied(1, 11, 1, StoragePool.Armory));
            Assert.AreEqual(0, state.GetOccupied(1, 10, 2, StoragePool.Armory));
            Assert.AreEqual(0, state.GetOccupied(1, 10, 1, StoragePool.Granary));
        }

        [TestMethod]
        public void ABuildingCannotSpendAnotherBuildingsAllocationOfTheSameGood()
        {
            var state = new NativeStorageState();
            CustomGoodId good = Register(state, "third.party");
            Apply(state, good, 1, Probe(), 3);
            Apply(state, good, 2, Probe(2, 20), 7);
            var before = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.InsufficientStock, state.PrepareExchange(good, 3, Probe(), -4, 4, out var rejected, out _));
            Assert.IsNull(rejected); EqualSnapshot(before, state);
            Apply(state, good, 3, Probe(), -3, 3);
            Assert.AreEqual(1, state.RowCount);
            Assert.AreEqual(0, state.GetOccupied(1, 10, 1, StoragePool.Armory));
            Assert.AreEqual(7, Balance(state, good).Quantity);
        }

        [TestMethod]
        public void RestoreUnionsTheSavedCatalogAndKeepsAbsentOwnersDormantButOccupied()
        {
            var source = new NativeStorageState();
            CustomGoodId first = Register(source, "first.owner"), absent = Register(source, "absent.owner");
            Apply(source, first, 1, Probe(), 3); Apply(source, absent, 1, Probe(), 4);
            var saved = Capture(source);
            var active = new[] { new NativeStorageGoodRegistration(first, StoragePool.Armory) };
            Assert.IsTrue(NativeStorageState.TryRestore(active, saved.Goods, saved.Stores, out var restored, out string reason), reason);
            Assert.AreEqual(1, restored.GetRegistrations().Length);
            Assert.AreEqual(2, restored.GetRegistrations(false).Length);
            Assert.AreEqual(7, restored.GetOccupied(1, 10, 1, StoragePool.Armory));
            Assert.AreEqual(4, Balance(restored, absent).Quantity);
            Assert.AreEqual(GoodsTransactionStatus.UnknownGood, restored.PrepareExchange(absent, 2, Probe(), 1, 0, out var rejected, out _));
            Assert.IsNull(rejected);
            Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, restored.PrepareExchange(first, 2, Probe(nativeUsed: 40), 4, 0, out rejected, out _));
            Apply(restored, first, 2, Probe(nativeUsed: 40), 3);
            Assert.IsFalse(restored.TryRegister(absent, StoragePool.Granary, out _));
            Assert.IsTrue(restored.TryRegister(absent, StoragePool.Armory, out reason), reason);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, restored.PrepareExchange(absent, 1, Probe(), 4, 0, out rejected, out _));
            Assert.AreEqual(6, Balance(restored, first).Quantity);
            Assert.AreEqual(4, Balance(restored, absent).Quantity);
            Assert.AreEqual(10, restored.GetOccupied(1, 10, 1, StoragePool.Armory));
            EqualSnapshot(saved, source);
        }

        [TestMethod]
        public void RestoreRejectsCrossEntryBalanceMismatchesAndPrivateLedgerHolds()
        {
            var source = new NativeStorageState();
            CustomGoodId good = Register(source, "third.party");
            Apply(source, good, 1, Probe(), 3);
            var earlier = Capture(source);
            Apply(source, good, 2, Probe(), 2);
            var later = Capture(source);
            var active = source.GetRegistrations();
            Assert.IsFalse(NativeStorageState.TryRestore(active, earlier.Goods, later.Stores, out var rejected, out _));
            Assert.IsNull(rejected);
            Assert.IsFalse(NativeStorageState.TryRestore(active, later.Goods, earlier.Stores, out rejected, out _));
            Assert.IsNull(rejected);

            var foreignLedger = new CustomGoodsInventory();
            CustomGoodsClient client = foreignLedger.ForOwner(good.OwnerGuid);
            Assert.IsTrue(client.TryRegister(good.LocalId, int.MaxValue, out _, out _));
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, new[] { new GoodsChange(good, 1, 5) }));
            Assert.AreEqual(GoodsTransactionStatus.Reserved, client.Reserve(2, new[] { new GoodsChange(good, 1, -1) }, out _));
            Assert.IsFalse(NativeStorageState.TryRestore(active, foreignLedger.Capture(), later.Stores, out rejected, out _));
            Assert.IsNull(rejected);
            EqualSnapshot(later, source);
        }

        [TestMethod]
        [DataRow(1, 1000, 0)]
        [DataRow(1, 30, 30)]
        [DataRow(2, 1000, 0)]
        [DataRow(2, 150, 150)]
        public void RestoreRejectsImpossibleVirtualClaimsEvenWhenTheLedgerMatches(int pool, int firstQuantity, int secondQuantity)
        {
            var kind = (StoragePool)pool;
            var first = new CustomGoodId("third.party", "a");
            var second = new CustomGoodId("third.party", "b");
            var active = new[] { new NativeStorageGoodRegistration(first, kind), new NativeStorageGoodRegistration(second, kind) };
            (byte[] Goods, byte[] Stores) Snapshot(int a, int b)
            {
                var inventory = new CustomGoodsInventory();
                CustomGoodsClient client = inventory.ForOwner(first.OwnerGuid);
                Assert.IsTrue(client.TryRegister(first.LocalId, int.MaxValue, out _, out _));
                Assert.IsTrue(client.TryRegister(second.LocalId, int.MaxValue, out _, out _));
                var changes = new List<GoodsChange> { new GoodsChange(first, 1, a) };
                if (b != 0) changes.Add(new GoodsChange(second, 1, b));
                Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, changes));
                // Synthetic schema-1 storage bytes deliberately bypass exchange validation.
                // Exact wire fields are necessary here: a matching, otherwise valid ledger
                // must not make an impossible persisted physical allocation trustworthy.
                using (var stream = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                    {
                        writer.Write(0x3153474E); writer.Write(1); writer.Write(2);
                        writer.Write(first.OwnerGuid); writer.Write(first.LocalId); writer.Write((byte)kind);
                        writer.Write(second.OwnerGuid); writer.Write(second.LocalId); writer.Write((byte)kind);
                        writer.Write(b == 0 ? 1 : 2);
                        void Row(int goodIndex, int quantity)
                        {
                            writer.Write(goodIndex); writer.Write(1); writer.Write(10u);
                            writer.Write((byte)1); writer.Write((byte)kind); writer.Write(quantity);
                        }
                        Row(0, a);
                        if (b != 0) Row(1, b);
                    }
                    return (inventory.Capture(), stream.ToArray());
                }
            }
            int capacity = kind == StoragePool.Armory ? 50 : 250;
            var valid = Snapshot(capacity / 2, capacity - capacity / 2);
            Assert.IsTrue(NativeStorageState.TryRestore(active, valid.Goods, valid.Stores, out var existing, out string reason), reason);
            Assert.AreEqual(capacity, existing.GetOccupied(1, 10, 1, kind));
            var before = Capture(existing);
            var impossible = Snapshot(firstQuantity, secondQuantity);
            Assert.IsFalse(NativeStorageState.TryRestore(active, impossible.Goods, impossible.Stores, out var rejected, out reason));
            Assert.IsNull(rejected); Assert.IsFalse(string.IsNullOrEmpty(reason));
            EqualSnapshot(before, existing);
        }

        [TestMethod]
        public void SnapshotRoundtripIsCanonicalAndCorruptOrIncompatibleDataDoesNotProduceACandidate()
        {
            var source = new NativeStorageState();
            CustomGoodId first = Register(source, "z.owner", "a"), second = Register(source, "a.owner", "z", StoragePool.Granary);
            Apply(source, first, 1, Probe(), 3);
            Apply(source, second, 1, Probe(2, 20, kind: StoragePool.Granary, capacity: 250), 4);
            var saved = Capture(source);
            var active = source.GetRegistrations().Reverse().ToArray();
            Assert.IsTrue(NativeStorageState.TryRestore(active, saved.Goods, saved.Stores, out var restored, out string reason), reason);
            EqualSnapshot(saved, restored);
            var wrongKind = new[] { new NativeStorageGoodRegistration(first, StoragePool.Granary) };
            Assert.IsFalse(NativeStorageState.TryRestore(wrongKind, saved.Goods, saved.Stores, out var rejected, out _));
            Assert.IsNull(rejected);
            byte[] badMagic = (byte[])saved.Stores.Clone(); badMagic[0] ^= 0xFF;
            foreach (byte[] invalid in new[] { null, Array.Empty<byte>(), badMagic, saved.Stores.Take(saved.Stores.Length - 1).ToArray(),
                saved.Stores.Concat(new byte[] { 0 }).ToArray(), new byte[NativeStorageState.MaximumSnapshotBytes + 1] })
            {
                Assert.IsFalse(NativeStorageState.TryRestore(active, saved.Goods, invalid, out rejected, out reason));
                Assert.IsNull(rejected); Assert.IsFalse(string.IsNullOrEmpty(reason));
            }
            EqualSnapshot(saved, source);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public void ReconciliationLosesStockForDestroyedReusedCapturedOrRetypedBuildings(int change)
        {
            var source = new NativeStorageState();
            CustomGoodId good = Register(source, "third.party");
            Apply(source, good, 1, Probe(), 3); Apply(source, good, 2, Probe(2, 20), 4);
            NativeStorageProbe first = change switch {
                0 => new NativeStorageProbe(false, 1, 10, 1, StoragePool.Armory, 0, 50),
                1 => Probe(globalId: 11),
                2 => Probe(player: 2),
                _ => Probe(kind: StoragePool.Granary, capacity: 250) };
            int calls = 0;
            NativeStorageProbe Read(int slot) { calls++; return slot == 1 ? first : Probe(2, 20); }
            var before = Capture(source);
            Assert.IsTrue(source.Reconcile(Read, out var candidate, out string reason), reason);
            Assert.AreNotSame(source, candidate);
            Assert.AreEqual(2, calls);
            Assert.AreEqual(1, candidate.RowCount);
            Assert.AreEqual(0, candidate.GetOccupied(1, 10, 1, StoragePool.Armory));
            Assert.AreEqual(4, candidate.GetOccupied(2, 20, 1, StoragePool.Armory));
            Assert.AreEqual(4, Balance(candidate, good).Quantity);
            Assert.AreEqual(3L, candidate.GetLastSequence(good.OwnerGuid));
            EqualSnapshot(before, source);
        }

        [TestMethod]
        public void ReconciliationBatchesLargeLossesAndLeavesTheOriginalWorldUntouchedOnFailure()
        {
            var source = new NativeStorageState();
            var goods = new List<CustomGoodId>();
            for (int i = 0; i < 40; i++)
            {
                CustomGoodId good = Register(source, "third.party", "good-" + i);
                goods.Add(good); Apply(source, good, i + 1, Probe(), 1);
            }
            Assert.IsTrue(source.Reconcile(_ => default, out var candidate, out string reason), reason);
            Assert.IsFalse(candidate.HasRows);
            Assert.AreEqual(42L, candidate.GetLastSequence("third.party"));
            foreach (CustomGoodId good in goods) Assert.AreEqual(0, Balance(candidate, good).Quantity);
            Assert.AreEqual(40, source.GetOccupied(1, 10, 1, StoragePool.Armory));
            var before = Capture(source);
            Assert.IsFalse(source.Reconcile(_ => throw new InvalidOperationException("probe failed"), out var failed, out reason));
            Assert.AreSame(source, failed); EqualSnapshot(before, source);
        }

        [TestMethod]
        public void ExhaustedMaintenanceSequencesDoNotPartiallyDiscardRows()
        {
            var source = new NativeStorageState();
            CustomGoodId good = Register(source, "third.party");
            Apply(source, good, long.MaxValue, Probe(), 3);
            var before = Capture(source);
            Assert.IsFalse(source.Reconcile(_ => default, out var candidate, out string reason));
            Assert.AreSame(source, candidate); Assert.IsFalse(string.IsNullOrEmpty(reason));
            EqualSnapshot(before, source);
        }

        [TestMethod]
        public void WarmUnchangedReconciliationAndMarkedReadsAllocateNothing()
        {
            var state = new NativeStorageState();
            CustomGoodId good = Register(state, "third.party");
            NativeStorageProbe building = Probe();
            Apply(state, good, 1, building, 3);
            Func<int, NativeStorageProbe> read = _ => building;
            for (int i = 0; i < 100; i++) state.Reconcile(read, out _, out _);
            NativeStorageState candidate = null;
            bool success = true; int occupied = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                success &= state.Reconcile(read, out candidate, out _);
                occupied += state.GetOccupied(1, 10, 1, StoragePool.Armory);
                occupied += state.GetOccupied(2, 20, 1, StoragePool.Armory);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.IsTrue(success); Assert.AreSame(state, candidate);
            Assert.AreEqual(3000, occupied); Assert.AreEqual(0L, allocated);

            // Timing is separate from allocation measurement. This synthetic .NET 10 fixture
            // has one claimed store and does not measure installed hooks, Unity/Mono or FPS.
            const int timingIterations = 10000;
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < timingIterations; i++)
            {
                success &= state.Reconcile(read, out candidate, out _);
                occupied += state.GetOccupied(1, 10, 1, StoragePool.Armory);
                occupied += state.GetOccupied(2, 20, 1, StoragePool.Armory);
            }
            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            TestContext.WriteLine("STORAGE_STATE_TIMING runtime={0}; synthetic_claimed_stores=1; iterations={1}; reconciliation_and_two_reads_ms={2:F3}",
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, timingIterations, milliseconds);
            Assert.IsTrue(success); Assert.AreSame(state, candidate);
            Assert.AreEqual(33000, occupied);
        }

        [TestMethod]
        public void WarmPreparedPublicationAllocatesNothing()
        {
            NativeStorageState.WarmPublication();
            var state = new NativeStorageState();
            CustomGoodId good = Register(state, "third.party");
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareExchange(good, 1, Probe(), 3, -1, out var plan, out string reason), reason);
            long before = GC.GetAllocatedBytesForCurrentThread();
            plan.Publish();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            plan.Dispose();
            Assert.AreEqual(0L, allocated);
            Assert.AreEqual(3, Balance(state, good).Quantity);
        }
    }
}
