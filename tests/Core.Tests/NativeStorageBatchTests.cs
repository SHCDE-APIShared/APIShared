using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using APIShared.Economy;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    // Actual pure state, canonical fingerprint and unchanged Content codec. These fixtures
    // establish no native wallet/manager, engine monitor, multiplayer or game execution claim.
    [TestClass]
    public sealed class NativeStorageBatchTests
    {
        private const string Owner = "batch.owner";
        private static readonly object World = new object();
        private static NativeStorageProbe Store(int slot = 1, int used = 0, int player = 1,
            StoragePool pool = StoragePool.Armory, uint global = 0xF0000001) =>
            new NativeStorageProbe(true, slot, global, player, pool, used, NativeStorageState.PoolCapacity(pool));
        private static CustomGoodId Register(NativeStorageState state, string local = "ore", string owner = Owner, StoragePool pool = StoragePool.Armory)
        {
            var good = new CustomGoodId(owner, local);
            Assert.IsTrue(state.TryRegister(good, pool, out string reason), reason); return good;
        }
        private static NativeStorageBatchRow Change(CustomGoodId good, NativeStorageProbe store, int delta) => new NativeStorageBatchRow(good, store, delta);
        private static NativeStorageBatch Batch(params NativeStorageBatchRow[] rows) => BatchWithGold(0, rows);
        private static NativeStorageBatch BatchWithGold(int gold, params NativeStorageBatchRow[] rows)
        {
            Assert.IsTrue(NativeStorageBatch.TryCreate(Owner, World, rows, gold, out var batch, out string reason), reason); return batch;
        }
        private static NativeStorageProbe[] Current(NativeStorageBatch batch) => Enumerable.Range(0, batch.Count).Select(i => batch[i].Expected).ToArray();
        private static void Apply(NativeStorageState state, NativeStorageBatch batch, long sequence)
        {
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareBatchExchange(batch, sequence, Current(batch), out var plan, out string reason), reason);
            using (plan) { Assert.IsTrue(plan.IsPrepared); plan.Publish(); }
        }
        private static void Seed(NativeStorageState state, CustomGoodId good, long sequence, NativeStorageProbe store, int quantity)
        {
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareExchange(good, sequence, store, quantity, 0, out var plan, out string reason), reason);
            using (plan) plan.Publish();
        }
        private static (byte[] Goods, byte[] Stores) Capture(NativeStorageState state)
        { state.Capture(out var goods, out var stores); return (goods, stores); }
        private static void EqualSnapshot((byte[] Goods, byte[] Stores) before, NativeStorageState state)
        { var after = Capture(state); CollectionAssert.AreEqual(before.Goods, after.Goods); CollectionAssert.AreEqual(before.Stores, after.Stores); }

        [TestMethod]
        public void HundredUnitTwoArmoryLotHasOnePublicationAndCanBeWithdrawnAsOneLot()
        {
            var state = new NativeStorageState(); var good = Register(state);
            var lot = BatchWithGold(-100, Change(good, Store(), 50), Change(good, Store(2), 50));
            var before = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareBatchExchange(lot, 1, Current(lot), out var abandoned, out string reason), reason);
            Assert.AreEqual(0, state.RowCount); Assert.AreEqual(0L, state.GetLastSequence(Owner));
            abandoned.Dispose(); EqualSnapshot(before, state);
            Apply(state, lot, 1);
            Assert.AreEqual(50, state.GetQuantity(good, Store())); Assert.AreEqual(50, state.GetQuantity(good, Store(2)));
            Assert.IsTrue(state.TryGetBalance(good, 1, out var balance)); Assert.AreEqual(100, balance.Quantity);
            Apply(state, BatchWithGold(100, Change(good, Store(), -50), Change(good, Store(2), -50)), 2);
            Assert.AreEqual(0, state.RowCount); Assert.IsFalse(state.HasRows);
            Assert.IsTrue(state.TryGetBalance(good, 1, out balance)); Assert.AreEqual(0, balance.Quantity);
        }

        [TestMethod]
        public void FailedLastRowCannotPublishAPrefixAndAnotherStoresStockCannotCoverItsWithdrawal()
        {
            var state = new NativeStorageState(); var good = Register(state);
            var deposit = Batch(Change(good, Store(), 50), Change(good, Store(2, used: 1), 50)); var before = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, state.PrepareBatchExchange(deposit, 1, Current(deposit), out var rejected, out _));
            Assert.IsNull(rejected); EqualSnapshot(before, state); Assert.AreEqual(0L, state.GetLastSequence(Owner));
            Seed(state, good, 1, Store(), 40); Seed(state, good, 2, Store(2), 1); before = Capture(state);
            var withdraw = Batch(Change(good, Store(), -1), Change(good, Store(2), -2));
            Assert.AreEqual(GoodsTransactionStatus.InsufficientStock, state.PrepareBatchExchange(withdraw, 3, Current(withdraw), out rejected, out _));
            Assert.IsNull(rejected); EqualSnapshot(before, state); Assert.AreEqual(2L, state.GetLastSequence(Owner));
        }

        [TestMethod]
        public void FinalPooledCapacityIncludesOtherOwnersAndAllowsNeutralSwapOrDrainWhileNativeStockIsOverfull()
        {
            var state = new NativeStorageState(); var a = Register(state, "a"); var b = Register(state, "b");
            var foreign = Register(state, owner: "other.owner"); var store = Store(used: 5);
            Seed(state, foreign, 1, store, 25); Seed(state, a, 1, store, 10); Seed(state, b, 2, store, 10);
            var over = Batch(Change(a, store, 5), Change(b, store, 1)); var before = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, state.PrepareBatchExchange(over, 3, Current(over), out _, out _)); EqualSnapshot(before, state);
            var swap = Batch(Change(a, Store(used: 60), -5), Change(b, Store(used: 60), 5)); Apply(state, swap, 3);
            Assert.AreEqual(5, state.GetQuantity(a, store)); Assert.AreEqual(15, state.GetQuantity(b, store));
            Assert.AreEqual(25, state.GetQuantity(foreign, store)); Assert.AreEqual(45, state.GetOccupied(1, store.GlobalId, 1, StoragePool.Armory));
            Apply(state, Batch(Change(a, Store(used: 60), -1)), 4);
            var increase = Batch(Change(a, Store(used: 60), 1));
            Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, state.PrepareBatchExchange(increase, 5, Current(increase), out _, out _));
        }

        [TestMethod]
        public void CanonicalRowsAreOwnedAndPermutationRetriesDoNotDependOnOccupancyOrLiveProbes()
        {
            var state = new NativeStorageState(); var good = Register(state);
            var rows = new[] { Change(good, Store(2), 8), Change(good, Store(), 12) };
            var first = BatchWithGold(-20, rows); rows[0] = Change(good, Store(3), 50);
            Apply(state, first, 1); Assert.AreEqual(8, state.GetQuantity(good, Store(2))); Assert.AreEqual(0, state.GetQuantity(good, Store(3)));
            var retry = BatchWithGold(-20, Change(good, Store(1, used: 99), 12), Change(good, Store(2, used: 99), 8));
            var before = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, state.ProbeBatchReceipt(retry, 1, out bool final, out _)); Assert.IsTrue(final);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, state.PrepareBatchExchange(retry, 1, new NativeStorageProbe[2], out var plan, out _));
            Assert.IsNull(plan); EqualSnapshot(before, state);
            foreach (var changed in new[] {
                BatchWithGold(-21, Change(good, Store(), 12), Change(good, Store(2), 8)),
                BatchWithGold(-20, Change(good, Store(), 11), Change(good, Store(2), 9)),
                BatchWithGold(-20, Change(good, Store(), 12), Change(good, Store(3), 8)) })
            {
                Assert.AreEqual(GoodsTransactionStatus.Conflict, state.ProbeBatchReceipt(changed, 1, out final, out _)); Assert.IsTrue(final);
                EqualSnapshot(before, state);
            }
        }

        [TestMethod]
        public void NewReceiptProbeNeverPublishesAndDefersNewAffordabilityFailure()
        {
            var state = new NativeStorageState(); var good = Register(state); var debit = Batch(Change(good, Store(), -1)); var before = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.InsufficientStock, state.ProbeBatchReceipt(debit, 1, out bool final, out _)); Assert.IsFalse(final);
            EqualSnapshot(before, state); Assert.AreEqual(0L, state.GetLastSequence(Owner));
            var credit = Batch(Change(good, Store(), 1));
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.ProbeBatchReceipt(credit, 1, out final, out _)); Assert.IsFalse(final);
            EqualSnapshot(before, state); Assert.AreEqual(0L, state.GetLastSequence(Owner)); Apply(state, credit, 1);
        }

        [TestMethod]
        public void RetainedBatchSurvivesLossReconciliationWithoutResurrectingItsGoods()
        {
            var state = new NativeStorageState(); var good = Register(state); var batch = Batch(Change(good, Store(), 5), Change(good, Store(2), 5)); Apply(state, batch, 1);
            Assert.IsTrue(state.Reconcile(_ => default, out var lost, out string reason), reason);
            Assert.AreEqual(0, lost.RowCount); var before = Capture(lost);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, lost.ProbeBatchReceipt(batch, 1, out bool final, out _)); Assert.IsTrue(final);
            EqualSnapshot(before, lost); Assert.IsTrue(lost.TryGetBalance(good, 1, out var balance)); Assert.AreEqual(0, balance.Quantity);
        }

        [TestMethod]
        public void SixtyFourRowsCanIncludeMoreThanThirtyTwoGoodsWhenOnlyThirtyTwoAccountsChange()
        {
            var state = new NativeStorageState(); var rows = new List<NativeStorageBatchRow>();
            for (int i = 0; i < 16; i++)
            {
                var good = Register(state, "move-" + i); Seed(state, good, i + 1, Store(100 + i), 1);
                rows.Add(Change(good, Store(100 + i), -1)); rows.Add(Change(good, Store(200 + i), 1));
            }
            for (int i = 0; i < 32; i++) rows.Add(Change(Register(state, "new-" + i), Store(), 1));
            var batch = Batch(rows.ToArray()); Apply(state, batch, 17);
            Assert.AreEqual(48, state.RowCount); Assert.AreEqual(32, state.GetOccupied(1, Store().GlobalId, 1, StoragePool.Armory));
            var saved = Capture(state);
            Assert.IsTrue(NativeStorageState.TryRestore(state.GetRegistrations(), saved.Goods, saved.Stores, out var restored, out string reason), reason);
            EqualSnapshot(saved, restored);
            var changedZeroNet = rows.ToArray();
            changedZeroNet[1] = Change(changedZeroNet[1].Good, Store(300), 1);
            Assert.AreEqual(GoodsTransactionStatus.Conflict, restored.ProbeBatchReceipt(Batch(changedZeroNet), 17, out bool final, out _)); Assert.IsTrue(final);
            var onlyNew = state.GetRegistrations().Where(registration => registration.Good.LocalId.StartsWith("new-", StringComparison.Ordinal)).ToArray();
            Assert.IsTrue(NativeStorageState.TryRestore(onlyNew, saved.Goods, saved.Stores, out var dormant, out reason), reason);
            Assert.AreEqual(GoodsTransactionStatus.UnknownGood, dormant.ProbeBatchReceipt(batch, 17, out final, out _)); Assert.IsTrue(final);
            var many = new List<NativeStorageBatchRow>();
            for (int i = 0; i < 33; i++) many.Add(Change(new CustomGoodId(Owner, "account-" + i), Store(i + 1), 1));
            Assert.IsFalse(NativeStorageBatch.TryCreate(Owner, World, many.ToArray(), 0, out _, out _));
            var good64 = new CustomGoodId(Owner, "same");
            var sixtyFour = Enumerable.Range(1, 64).Select(i => Change(good64, Store(i), 1)).ToArray();
            Assert.IsTrue(NativeStorageBatch.TryCreate(Owner, World, sixtyFour, 0, out _, out _));
            Assert.IsFalse(NativeStorageBatch.TryCreate(Owner, World, sixtyFour.Concat(new[] { Change(good64, Store(65), 1) }).ToArray(), 0, out _, out _));
        }

        [TestMethod]
        public void FinalRowBoundAllowsRemovalAndInsertionTogetherButRejectsAnAdditionalRow()
        {
            NativeStorageState state = StateAtRowBound(out CustomGoodId first, out CustomGoodId second, out int usedSlots);
            var replace = Batch(Change(first, Store(1, global: 1), -1), Change(second, Store(usedSlots + 1, global: (uint)(usedSlots + 1)), 1));
            Apply(state, replace, 2); Assert.AreEqual(NativeStorageState.MaximumRows, state.RowCount);
            var extra = Batch(Change(second, Store(usedSlots + 2, global: (uint)(usedSlots + 2)), 1)); var before = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.LimitReached, state.PrepareBatchExchange(extra, 3, Current(extra), out var rejected, out _));
            Assert.IsNull(rejected); EqualSnapshot(before, state); Assert.AreEqual(2L, state.GetLastSequence(Owner));
        }

        [TestMethod]
        [DataRow("empty")]
        [DataRow("zero")]
        [DataRow("duplicate")]
        [DataRow("foreign")]
        [DataRow("player")]
        [DataRow("slot-identity")]
        [DataRow("dead-original")]
        [DataRow("default-good")]
        [DataRow("no-world")]
        [DataRow("net-zero")]
        public void StructuralInvalidInputsCannotBecomeBatchIntents(string invalid)
        {
            var good = new CustomGoodId(Owner, "ore"); var rows = new[] { Change(good, Store(), 1) }; object world = World;
            switch (invalid)
            {
                case "empty": rows = Array.Empty<NativeStorageBatchRow>(); break;
                case "zero": rows[0] = Change(good, Store(), 0); break;
                case "duplicate": rows = new[] { rows[0], rows[0] }; break;
                case "foreign": rows[0] = Change(new CustomGoodId("other", "ore"), Store(), 1); break;
                case "player": rows = new[] { rows[0], Change(good, Store(2, player: 2), 1) }; break;
                case "slot-identity": rows = new[] { rows[0], Change(new CustomGoodId(Owner, "second"), Store(global: 7), 1) }; break;
                case "dead-original": rows[0] = Change(good, default, 1); break;
                case "default-good": rows[0] = Change(default, Store(), 1); break;
                case "no-world": world = null; break;
                case "net-zero": rows = new[] { rows[0], Change(good, Store(2), -1) }; break;
                default: Assert.Fail(invalid); break;
            }
            Assert.IsFalse(NativeStorageBatch.TryCreate(Owner, world, rows, 7, out var batch, out string reason));
            Assert.IsNull(batch); Assert.IsFalse(string.IsNullOrEmpty(reason));
        }

        [TestMethod]
        public void Int32ExtremesAreAggregatedWithoutOverflowAndCannotPublishImpossibleStock()
        {
            var state = new NativeStorageState(); var good = Register(state); var before = Capture(state);
            Assert.IsFalse(NativeStorageBatch.TryCreate(Owner, World, new[] { Change(good, Store(), int.MaxValue), Change(good, Store(2), 1) }, 0, out _, out _));
            Assert.IsFalse(NativeStorageBatch.TryCreate(Owner, World, new[] { Change(good, Store(), int.MinValue), Change(good, Store(2), -1) }, 0, out _, out _));
            var large = Batch(Change(good, Store(), int.MaxValue));
            Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, state.PrepareBatchExchange(large, 1, Current(large), out _, out _)); EqualSnapshot(before, state);
            var negative = Batch(Change(good, Store(), int.MinValue));
            Assert.AreEqual(GoodsTransactionStatus.InsufficientStock, state.PrepareBatchExchange(negative, 1, Current(negative), out _, out _)); EqualSnapshot(before, state);
            Assert.IsTrue(NativeStorageBatch.TryCreate(Owner, World, new[] {
                Change(good, Store(), int.MaxValue), Change(good, Store(2), int.MaxValue),
                Change(good, Store(3), -int.MaxValue), Change(good, Store(4), -int.MaxValue + 1) }, 0, out _, out _));
        }

        [TestMethod]
        public void RegistrationPoolAndCurrentIdentityAreRequiredAndInconsistentStoreReadsReject()
        {
            var state = new NativeStorageState(); var a = Register(state, "a"); var b = Register(state, "b");
            var batch = Batch(Change(a, Store(), 1), Change(b, Store(), 1)); var before = Capture(state);
            var current = Current(batch); current[1] = Store(used: 1);
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, state.PrepareBatchExchange(batch, 1, current, out _, out _)); EqualSnapshot(before, state);
            current = Current(batch); current[1] = Store(global: 7);
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, state.PrepareBatchExchange(batch, 1, current, out _, out _)); EqualSnapshot(before, state);
            var wrongPool = Batch(Change(a, Store(pool: StoragePool.Granary), 1));
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, state.ProbeBatchReceipt(wrongPool, 1, out bool final, out _)); Assert.IsTrue(final);
            var missing = Batch(Change(new CustomGoodId(Owner, "unregistered"), Store(), 1));
            Assert.AreEqual(GoodsTransactionStatus.UnknownGood, state.ProbeBatchReceipt(missing, 1, out final, out _)); Assert.IsTrue(final); EqualSnapshot(before, state);
            Seed(state, a, 1, Store(), 1); var saved = Capture(state);
            Assert.IsTrue(NativeStorageState.TryRestore(Array.Empty<NativeStorageGoodRegistration>(), saved.Goods, saved.Stores, out var dormant, out string reason), reason);
            Assert.AreEqual(GoodsTransactionStatus.UnknownGood, dormant.ProbeBatchReceipt(batch, 1, out final, out _)); Assert.IsTrue(final);
        }

        [TestMethod]
        public void ExistingCargoAndPhysicalPreparedGuardsCoverBatchAndItsPublicationKeepsCargoHistory()
        {
            var state = new NativeStorageState(); var good = Register(state); var batch = Batch(Change(good, Store(), 1));
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareCreateCargo(good, 1, 1, 3, out var cargoPlan, out _, out _));
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, state.ProbeBatchReceipt(batch, 1, out bool final, out _)); Assert.IsTrue(final);
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, state.PrepareBatchExchange(batch, 1, Current(batch), out _, out _));
            using (cargoPlan) cargoPlan.Publish(); object cargo = state.CargoPublicationIdentity;
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareBatchExchange(batch, 1, Current(batch), out var prepared, out _));
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, state.PrepareCreateCargo(good, 2, 1, 1, out _, out _, out _));
            Assert.ThrowsExactly<InvalidOperationException>(() => state.Capture(out _, out _));
            using (prepared) prepared.Publish(); Assert.AreSame(cargo, state.CargoPublicationIdentity);
            Assert.IsTrue(state.TryGetCargo(Owner, 1, out var lot)); Assert.AreEqual(3, lot.Remaining);
            var saved = Capture(state);
            Assert.IsTrue(NativeStorageState.TryRestore(state.GetRegistrations(), saved.Goods, saved.Stores, out var restored, out string reason), reason);
            Assert.IsTrue(restored.HasCargoHistory); Assert.IsTrue(restored.TryGetCargo(Owner, 1, out lot)); Assert.AreEqual(3, lot.Remaining); EqualSnapshot(saved, restored);
        }

        [TestMethod]
        public void UnknownOpaqueBatchIntentUsesExistingCodecAndDoesNotChangeSingleExchangeBytes()
        {
            var state = new NativeStorageState(); var good = Register(state); var batch = Batch(Change(good, Store(), 1)); Apply(state, batch, 1);
            Seed(state, good, 2, Store(), 1); var saved = Capture(state);
            byte[] batchIntent = ReceiptIntent(saved.Goods, 1), singleIntent = ReceiptIntent(saved.Goods, 2);
            Assert.AreEqual(34, batchIntent.Length); Assert.AreEqual((byte)5, batchIntent[0]); Assert.AreEqual((byte)1, batchIntent[1]);
            CollectionAssert.AreEqual(new byte[] { 2, 1, 1, 0, 0, 0, 1, 0, 0, 240, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, singleIntent);
            var opaqueReader = new CustomGoodsInventory(); Assert.IsTrue(opaqueReader.ForOwner(Owner).TryRegister(good.LocalId, int.MaxValue, out _, out _));
            Assert.IsTrue(opaqueReader.TryRestore(saved.Goods, out string reason), reason); CollectionAssert.AreEqual(saved.Goods, opaqueReader.Capture());
            Assert.IsTrue(NativeStorageState.TryRestore(state.GetRegistrations(), saved.Goods, saved.Stores, out var restored, out reason), reason);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, restored.ProbeBatchReceipt(batch, 1, out bool final, out _)); Assert.IsTrue(final);
            Assert.AreEqual(GoodsTransactionStatus.Conflict, restored.PrepareExchange(good, 1, Store(), 1, 0, out _, out _));
        }

        [TestMethod]
        public void BoundedReceiptsExpireRatherThanExecutingAnOldLotAgain()
        {
            var state = new NativeStorageState(); var good = Register(state); var first = Batch(Change(good, Store(), 1)); Apply(state, first, 1);
            // The published Content bound is 1024 total receipts, not an unbounded per-owner history.
            NativeStorageBatch last = first;
            for (long sequence = 2; sequence <= 1026; sequence++)
            { last = Batch(Change(good, Store(), sequence % 2 == 0 ? -1 : 1)); Apply(state, last, sequence); }
            var before = Capture(state);
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, state.ProbeBatchReceipt(first, 1, out bool final, out _)); Assert.IsTrue(final);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, state.ProbeBatchReceipt(last, 1026, out final, out _)); Assert.IsTrue(final);
            Assert.AreEqual(1026L, state.GetLastSequence(Owner)); EqualSnapshot(before, state);
            Assert.IsTrue(NativeStorageState.TryRestore(state.GetRegistrations(), before.Goods, before.Stores, out _, out string reason), reason);
        }

        // This parser deliberately binds the established schema-2 receipt wire, to prove the new
        // opaque intent fits unchanged older decoding and leaves the old single intent exact.
        private static byte[] ReceiptIntent(byte[] data, long sequence)
        {
            using (var reader = new BinaryReader(new MemoryStream(data)))
            {
                reader.ReadInt32(); Assert.AreEqual(2, reader.ReadInt32());
                int catalog = reader.ReadInt32();
                for (int i = 0; i < catalog; i++) { reader.ReadString(); reader.ReadString(); reader.ReadInt32(); for (int player = 0; player < 8; player++) reader.ReadInt32(); }
                int owners = reader.ReadInt32(); for (int i = 0; i < owners; i++) { reader.ReadString(); reader.ReadInt64(); }
                int receipts = reader.ReadInt32();
                for (int i = 0; i < receipts; i++)
                {
                    string owner = reader.ReadString(); long id = reader.ReadInt64(); reader.ReadBoolean(); reader.ReadByte();
                    int lines = reader.ReadInt32(); for (int line = 0; line < lines; line++) { reader.ReadInt32(); reader.ReadByte(); reader.ReadInt32(); }
                    byte[] intent = reader.ReadBytes(reader.ReadInt32()); if (owner == Owner && id == sequence) return intent;
                }
            }
            Assert.Fail("The operation receipt was not retained."); return null;
        }

        // Build a valid existing schema-1 pair at the documented row bound without thousands
        // of setup publications. This fixture encodes a real codec contract, not private state.
        private static NativeStorageState StateAtRowBound(out CustomGoodId first, out CustomGoodId second, out int usedSlots)
        {
            const int goodCount = 5;
            int perGood = NativeStorageState.MaximumRows / goodCount, remainder = NativeStorageState.MaximumRows % goodCount;
            usedSlots = perGood + (remainder == 0 ? 0 : 1);
            var ledger = new CustomGoodsInventory(); var client = ledger.ForOwner(Owner);
            var goods = new CustomGoodId[goodCount]; var totals = new GoodsChange[goodCount]; var registrations = new NativeStorageGoodRegistration[goodCount];
            for (int good = 0; good < goodCount; good++)
            {
                Assert.IsTrue(client.TryRegister("g" + good, int.MaxValue, out goods[good], out _));
                totals[good] = new GoodsChange(goods[good], 1, perGood + (good < remainder ? 1 : 0));
                registrations[good] = new NativeStorageGoodRegistration(goods[good], StoragePool.Armory);
            }
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, totals));
            byte[] stores;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x3153474E); writer.Write(1); writer.Write(goodCount);
                foreach (CustomGoodId good in goods) { writer.Write(good.OwnerGuid); writer.Write(good.LocalId); writer.Write((byte)StoragePool.Armory); }
                writer.Write(NativeStorageState.MaximumRows);
                for (int good = 0; good < goodCount; good++)
                    for (int slot = 1; slot <= totals[good].Delta; slot++)
                    { writer.Write(good); writer.Write(slot); writer.Write((uint)slot); writer.Write((byte)1); writer.Write((byte)StoragePool.Armory); writer.Write(1); }
                writer.Flush(); stores = stream.ToArray();
            }
            Assert.IsTrue(NativeStorageState.TryRestore(registrations, ledger.Capture(), stores, out var state, out string reason), reason);
            first = goods[0]; second = goods[1]; return state;
        }
    }
}
