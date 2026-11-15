using System;
using System.Collections.Generic;
using System.IO;
using APIShared.Economy;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    [TestClass]
    public sealed class NativeStorageCargoStateTests
    {
        private static NativeStorageState Setup(out CustomGoodId good, StoragePool pool = StoragePool.Granary)
        {
            var state = new NativeStorageState(); good = new CustomGoodId("fish.owner", "fish");
            Assert.IsTrue(state.TryRegister(good, pool, out string reason), reason); return state;
        }
        private static NativeStorageProbe Store(int slot = 1, uint global = 10, int player = 1, StoragePool pool = StoragePool.Granary, int native = 0) =>
            new NativeStorageProbe(true, slot, global, player, pool, native, pool == StoragePool.Armory ? 50 : 250);
        private static NativeStorageState.CargoLot Create(NativeStorageState state, CustomGoodId good, long sequence = 1, int quantity = 8, int player = 1)
        {
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareCreateCargo(good, sequence, player, quantity, out var plan, out var lot, out string reason), reason);
            using (plan) { Assert.IsTrue(plan.IsPrepared); plan.Publish(); } return lot;
        }
        private static NativeStorageState.CargoLot Deposit(NativeStorageState state, NativeStorageState.CargoLot lot, NativeStorageProbe store, int quantity)
        {
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareDepositCargo(lot, store, quantity, out var plan, out var next, out string reason), reason);
            using (plan) plan.Publish(); return next;
        }
        private static NativeStorageState.CargoLot Discard(NativeStorageState state, NativeStorageState.CargoLot lot)
        {
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareDiscardCargo(lot, out var plan, out var next, out string reason), reason);
            using (plan) plan.Publish(); return next;
        }
        private static (byte[] Goods, byte[] Stores) Capture(NativeStorageState state)
        { state.Capture(out byte[] goods, out byte[] stores); return (goods, stores); }
        private static void Unchanged((byte[] Goods, byte[] Stores) before, NativeStorageState state)
        {
            var after = Capture(state); CollectionAssert.AreEqual(before.Goods, after.Goods); CollectionAssert.AreEqual(before.Stores, after.Stores);
        }
        private static void Balance(NativeStorageState state, CustomGoodId good, int quantity)
        { Assert.IsTrue(state.TryGetBalance(good, 1, out GoodsBalance balance)); Assert.AreEqual(quantity, balance.Quantity); Assert.AreEqual(0, balance.Reserved); }

        [TestMethod]
        public void CargoCreationHasNoLedgerOrStorageEffectAndExactRetryReturnsCurrentRemainder()
        {
            var state = Setup(out var good); var lot = Create(state, good);
            Balance(state, good, 0); Assert.AreEqual(0, state.RowCount); Assert.AreEqual(0L, state.GetLastSequence(good.OwnerGuid));
            Assert.AreEqual(1L, state.GetLastCargoSequence(good.OwnerGuid)); Assert.IsTrue(state.HasCargoHistory);
            var current = Deposit(state, lot, Store(), 3); Assert.AreEqual(5, current.Remaining); Balance(state, good, 3);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, state.PrepareCreateCargo(good, 1, 1, 8, out var retry, out var same, out _));
            Assert.IsNull(retry); Assert.AreEqual(5, same.Remaining); Assert.AreEqual(8, same.Issued);
            Assert.AreEqual(GoodsTransactionStatus.Conflict, state.PrepareCreateCargo(good, 1, 1, 9, out retry, out _, out _));
            Assert.AreEqual(GoodsTransactionStatus.Conflict, state.PrepareCreateCargo(good, 1, 2, 8, out retry, out _, out _));
            var other = new CustomGoodId(good.OwnerGuid, "other"); Assert.IsTrue(state.TryRegister(other, StoragePool.Granary, out _));
            var withOther = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.Conflict, state.PrepareCreateCargo(other, 1, 1, 8, out retry, out _, out _));
            Unchanged(withOther, state); Assert.AreEqual(1L, state.GetLastCargoSequence(good.OwnerGuid));
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        public void DepositPublishesOneConservedQuantityAndFailedCapacityLeavesTheLotRetryable(int pool)
        {
            var kind = (StoragePool)pool; var state = Setup(out var good, kind); var lot = Create(state, good, quantity: 4);
            var store = Store(pool: kind); int capacity = store.Capacity;
            var before = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded,
                state.PrepareDepositCargo(lot, Store(pool: kind, native: capacity - 2), 3, out var refused, out _, out _));
            Assert.IsNull(refused); Unchanged(before, state);
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareDepositCargo(lot, store, 3, out var plan, out var next, out _));
            Assert.IsTrue(state.TryGetCargo(good.OwnerGuid, 1, out var pending)); Assert.AreEqual(4, pending.Remaining); Balance(state, good, 0);
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, state.PrepareCreateCargo(good, 2, 1, 1, out _, out _, out _));
            Assert.ThrowsExactly<InvalidOperationException>(() => state.Capture(out _, out _));
            plan.Dispose(); Unchanged(before, state);
            next = Deposit(state, lot, store, 3); Assert.AreEqual(1, next.Remaining); Balance(state, good, 3);
            Assert.AreEqual(3, state.GetQuantity(good, store)); Assert.AreEqual(3, state.GetOccupied(store.Slot, store.GlobalId, 1, kind));
            Assert.AreEqual(lot.Issued, next.Remaining + state.GetQuantity(good, store));
        }

        [TestMethod]
        public void RetainedCheckpointReplayPrecedesDestroyedDestinationAndNeverConsumesCargoTwice()
        {
            var state = Setup(out var good); var original = Create(state, good); var store = Store();
            var remaining = Deposit(state, original, store, 3);
            Assert.IsTrue(state.Reconcile(_ => default, out var reconciled, out string reason), reason); state = reconciled;
            Assert.IsTrue(state.TryGetCargo(good.OwnerGuid, 1, out var afterLoss)); Assert.AreEqual(5, afterLoss.Remaining); Balance(state, good, 0);
            var before = Capture(state);
            var vanished = new NativeStorageProbe(false, store.Slot, store.GlobalId, store.PlayerId, store.Kind, 0, store.Capacity);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, state.PrepareDepositCargo(original, vanished, 3, out var retry, out var current, out _));
            Assert.IsNull(retry); Assert.AreEqual(5, current.Remaining); Unchanged(before, state);
            Assert.AreEqual(GoodsTransactionStatus.Conflict, state.PrepareDepositCargo(original, vanished, 2, out _, out _, out _));
            Assert.AreEqual(GoodsTransactionStatus.Conflict, state.PrepareDepositCargo(original, Store(slot: 2, global: 20), 3, out _, out _, out _));
            remaining = Deposit(state, remaining, Store(slot: 2, global: 20), 2);
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, state.PrepareDepositCargo(original, vanished, 3, out _, out _, out _));
            Assert.AreEqual(3, remaining.Remaining); Balance(state, good, 2);
        }

        [TestMethod]
        public void DiscardRecordsOnlyUndeliveredLossAndCannotActOnAStaleCheckpoint()
        {
            var state = Setup(out var good); var original = Create(state, good); var remaining = Deposit(state, original, Store(), 3);
            Assert.AreEqual(GoodsTransactionStatus.Conflict, state.PrepareDiscardCargo(original, out _, out _, out _));
            var terminal = Discard(state, remaining); Assert.AreEqual(0, terminal.Remaining); Balance(state, good, 3);
            var before = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, state.PrepareDiscardCargo(remaining, out var retry, out var zero, out _));
            Assert.IsNull(retry); Assert.AreEqual(0, zero.Remaining);
            Assert.AreEqual(GoodsTransactionStatus.Conflict, state.PrepareDepositCargo(remaining, Store(), 5, out _, out _, out _));
            Assert.AreEqual(1L, state.GetLastSequence(good.OwnerGuid)); Assert.AreEqual(1L, state.GetLastCargoSequence(good.OwnerGuid));
            Assert.IsTrue(state.HasCargoHistory); Unchanged(before, state);
        }

        [TestMethod]
        public void PreparedMetadataDoesNotPublishOnDisposeAndBlocksCaptureRegistrationAndReentry()
        {
            var state = Setup(out var good); var before = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareCreateCargo(good, 1, 1, 8, out var create, out _, out _));
            Assert.AreEqual(0L, state.GetLastCargoSequence(good.OwnerGuid)); Assert.IsFalse(state.HasCargoHistory);
            Assert.IsFalse(state.TryRegister(new CustomGoodId(good.OwnerGuid, "other"), StoragePool.Armory, out _));
            Assert.ThrowsExactly<InvalidOperationException>(() => state.Capture(out _, out _));
            Assert.IsFalse(state.Reconcile(_ => Store(), out _, out _));
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, state.PrepareExchange(good, 1, Store(), 1, 0, out _, out _));
            create.Dispose(); Unchanged(before, state);
            var lot = Create(state, good);
            var issued = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareDiscardCargo(lot, out var discard, out _, out _));
            discard.Dispose(); Unchanged(issued, state);
        }

        [TestMethod]
        public void FullRegistryNeverEvictsActiveCargoButCompletedReceiptsCanExpireWithoutRecreation()
        {
            var state = Setup(out var good); NativeStorageState.CargoLot first = default;
            for (int sequence = 1; sequence <= NativeStorageState.MaximumCargoRecords; sequence++)
            { var created = Create(state, good, sequence, 1); if (sequence == 1) first = created; }
            long next = NativeStorageState.MaximumCargoRecords + 1L;
            Assert.AreEqual(GoodsTransactionStatus.LimitReached, state.PrepareCreateCargo(good, next, 1, 1, out _, out _, out _));
            Assert.AreEqual(next - 1, state.GetLastCargoSequence(good.OwnerGuid));
            Discard(state, first); Create(state, good, next, 1);
            Assert.AreEqual(NativeStorageState.MaximumCargoRecords, state.CargoRecordCount);
            Assert.IsFalse(state.TryGetCargo(good.OwnerGuid, 1, out _)); Assert.IsTrue(state.TryGetCargo(good.OwnerGuid, 2, out var active)); Assert.AreEqual(1, active.Remaining);
            var saved = Capture(state);
            Assert.IsTrue(NativeStorageState.TryRestore(state.GetRegistrations(), saved.Goods, saved.Stores, out var restored, out string reason), reason);
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, restored.PrepareCreateCargo(good, 1, 1, 1, out _, out _, out _));
            Assert.AreEqual(next, restored.GetLastCargoSequence(good.OwnerGuid)); Assert.IsTrue(restored.HasCargoHistory);
        }

        [TestMethod]
        public void SchemaOneBytesStayExactUntilCargoUseAndSchemaTwoRoundtripsWithReplayAndDormantOwners()
        {
            var state = Setup(out var good); var legacy = Capture(state);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x3153474E); writer.Write(1); writer.Write(1); writer.Write(good.OwnerGuid); writer.Write(good.LocalId); writer.Write((byte)StoragePool.Granary); writer.Write(0);
                CollectionAssert.AreEqual(stream.ToArray(), legacy.Stores);
            }
            var original = Create(state, good); var remaining = Deposit(state, original, Store(), 3);
            Discard(state, Create(state, good, 2, 1));
            var saved = Capture(state); Assert.AreEqual(2, BitConverter.ToInt32(saved.Stores, 4));
            Assert.IsTrue(NativeStorageState.TryRestore(state.GetRegistrations(), saved.Goods, saved.Stores, out var restored, out string reason), reason);
            Unchanged(saved, restored);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, restored.PrepareDepositCargo(original, Store(), 3, out _, out var current, out _)); Assert.AreEqual(5, current.Remaining);
            Assert.IsTrue(NativeStorageState.TryRestore(Array.Empty<NativeStorageGoodRegistration>(), saved.Goods, saved.Stores, out var dormant, out reason), reason);
            Assert.IsTrue(dormant.TryGetCargo(good.OwnerGuid, 1, out current)); Assert.AreEqual(5, current.Remaining);
            Assert.AreEqual(GoodsTransactionStatus.UnknownGood, dormant.PrepareDepositCargo(remaining, Store(), 1, out _, out _, out _));
            Discard(restored, remaining); var completed = Capture(restored); Assert.AreEqual(2, BitConverter.ToInt32(completed.Stores, 4));
            Assert.IsTrue(NativeStorageState.TryRestore(restored.GetRegistrations(), completed.Goods, completed.Stores, out restored, out reason), reason);
            Assert.AreEqual(2L, restored.GetLastCargoSequence(good.OwnerGuid)); Assert.IsTrue(restored.HasCargoHistory);
        }

        [TestMethod]
        public void CanonicalCargoCodecRejectsInvalidBoundsReceiptsCountsAndMissingSchemaTwoMetadata()
        {
            var state = Setup(out var good); Deposit(state, Create(state, good), Store(), 3); var saved = Capture(state);
            int lotCount, lot = CargoOffset(saved.Stores, out lotCount);
            var bad = new List<byte[]>();
            byte[] Changed(int offset, byte value) { var bytes = (byte[])saved.Stores.Clone(); bytes[offset] = value; return bytes; }
            bad.Add(Changed(lot + 12, 0)); // Invalid producing player.
            bad.Add(Changed(lot + 21, 255)); // Unknown receipt kind.
            bad.Add(Changed(lot + 22, 0)); // Before quantity cannot be zero for delivered cargo.
            bad.Add(Changed(lot + 39, (byte)StoragePool.Armory)); // Good/receipt pool disagreement.
            var overflowCount = (byte[])saved.Stores.Clone(); Array.Copy(BitConverter.GetBytes(NativeStorageState.MaximumCargoRecords + 1), 0, overflowCount, lotCount, 4); bad.Add(overflowCount);
            var trailing = new byte[saved.Stores.Length + 1]; Array.Copy(saved.Stores, trailing, saved.Stores.Length); bad.Add(trailing);
            var missing = new byte[lotCount]; Array.Copy(saved.Stores, missing, missing.Length); bad.Add(missing);
            var noLots = new byte[lotCount + 4]; Array.Copy(saved.Stores, noLots, noLots.Length); Array.Clear(noLots, lotCount, 4); bad.Add(noLots);
            int metadata = CargoMetadataOffset(saved.Stores); var noHistory = new byte[metadata + 8]; Array.Copy(saved.Stores, noHistory, metadata); bad.Add(noHistory);
            foreach (byte[] corrupt in bad)
            {
                Assert.IsFalse(NativeStorageState.TryRestore(state.GetRegistrations(), saved.Goods, corrupt, out var rejected, out string reason));
                Assert.IsNull(rejected); Assert.IsFalse(string.IsNullOrEmpty(reason)); Unchanged(saved, state);
            }
        }
        private static int CargoMetadataOffset(byte[] stores)
        {
            using (var stream = new MemoryStream(stores, false))
            using (var reader = new BinaryReader(stream))
            {
                reader.ReadInt32(); reader.ReadInt32(); int catalog = reader.ReadInt32();
                for (int i = 0; i < catalog; i++) { reader.ReadString(); reader.ReadString(); reader.ReadByte(); }
                int rows = reader.ReadInt32(); return checked((int)stream.Position + rows * 18);
            }
        }
        private static int CargoOffset(byte[] stores, out int lotCountOffset)
        {
            using (var stream = new MemoryStream(stores, false))
            using (var reader = new BinaryReader(stream))
            {
                reader.ReadInt32(); Assert.AreEqual(2, reader.ReadInt32());
                int catalog = reader.ReadInt32(); for (int i = 0; i < catalog; i++) { reader.ReadString(); reader.ReadString(); reader.ReadByte(); }
                int rows = reader.ReadInt32(); stream.Position += rows * 18;
                int owners = reader.ReadInt32(); for (int i = 0; i < owners; i++) { reader.ReadString(); reader.ReadInt64(); }
                lotCountOffset = (int)stream.Position; Assert.AreEqual(1, reader.ReadInt32()); return (int)stream.Position;
            }
        }
    }
}
