using System;
using APIShared.Economy;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    [TestClass]
    public sealed class NativeStorageQuantityStateTests
    {
        private static NativeStorageProbe Store(int slot = 1, uint global = 0xF0000000, int player = 1, StoragePool pool = StoragePool.Armory) =>
            new NativeStorageProbe(true, slot, global, player, pool, 0, pool == StoragePool.Armory ? 50 : 250);
        private static CustomGoodId Register(NativeStorageState state, string owner, string local, StoragePool pool = StoragePool.Armory)
        {
            var good = new CustomGoodId(owner, local); Assert.IsTrue(state.TryRegister(good, pool, out string reason), reason); return good;
        }
        private static void Add(NativeStorageState state, CustomGoodId good, NativeStorageProbe store, int quantity)
        {
            Assert.AreEqual(GoodsTransactionStatus.Applied,
                state.PrepareExchange(good, state.GetLastSequence(good.OwnerGuid) + 1, store, quantity, 0, out var plan, out string reason), reason);
            using (plan) plan.Publish();
        }

        [TestMethod]
        public void QuantitySeparatesGoodsAndBuildingsAndDoesNotChangeAnyPublishedOrPersistedState()
        {
            var state = new NativeStorageState(); var armor = Register(state, "armor.owner", "armor"); var other = Register(state, "other.owner", "other");
            var first = Store(); var second = Store(2, 20);
            Add(state, armor, first, 3); Add(state, other, first, 2); Add(state, armor, second, 4);
            state.Capture(out byte[] beforeGoods, out byte[] beforeStores); object rows = state.PublicationIdentity, cargo = state.CargoPublicationIdentity;
            long sequence = state.GetLastSequence(armor.OwnerGuid);
            Assert.AreEqual(5, state.GetOccupied(first.Slot, first.GlobalId, 1, first.Kind));
            Assert.IsTrue(state.TryGetQuantity(armor, first, out int quantity, out string reason), reason); Assert.AreEqual(3, quantity);
            Assert.IsTrue(state.TryGetQuantity(other, first, out quantity, out reason), reason); Assert.AreEqual(2, quantity);
            Assert.IsTrue(state.TryGetQuantity(armor, second, out quantity, out reason), reason); Assert.AreEqual(4, quantity);
            Assert.IsTrue(state.TryGetQuantity(armor, Store(3, 30), out quantity, out reason), reason); Assert.AreEqual(0, quantity);
            Assert.AreSame(rows, state.PublicationIdentity); Assert.AreSame(cargo, state.CargoPublicationIdentity);
            Assert.AreEqual(sequence, state.GetLastSequence(armor.OwnerGuid));
            state.Capture(out byte[] afterGoods, out byte[] afterStores);
            CollectionAssert.AreEqual(beforeGoods, afterGoods); CollectionAssert.AreEqual(beforeStores, afterStores);
        }

        [TestMethod]
        public void FreshReusedOrCapturedIdentityReadsEmptyWithoutReconcilingOldRowsDuringObservation()
        {
            var state = new NativeStorageState(); var good = Register(state, "owner", "armor"); var original = Store(); Add(state, good, original, 3);
            state.Capture(out byte[] beforeGoods, out byte[] beforeStores); object publication = state.PublicationIdentity;
            foreach (var fresh in new[] { Store(global: 11), Store(player: 2) })
            {
                Assert.IsTrue(state.TryGetQuantity(good, fresh, out int quantity, out string reason), reason); Assert.AreEqual(0, quantity);
            }
            Assert.AreSame(publication, state.PublicationIdentity);
            state.Capture(out byte[] afterGoods, out byte[] afterStores);
            CollectionAssert.AreEqual(beforeGoods, afterGoods); CollectionAssert.AreEqual(beforeStores, afterStores);
        }

        [TestMethod]
        public void UnknownDormantAndWrongPoolQueriesFailWhileAValidEmptyPoolReturnsZero()
        {
            var state = new NativeStorageState(); var armor = Register(state, "owner", "armor"); var food = Register(state, "owner", "food", StoragePool.Granary);
            Add(state, armor, Store(), 3); state.Capture(out byte[] goods, out byte[] stores);
            Assert.IsFalse(state.TryGetQuantity(new CustomGoodId("missing", "armor"), Store(), out int quantity, out _)); Assert.AreEqual(0, quantity);
            Assert.IsFalse(state.TryGetQuantity(armor, Store(pool: StoragePool.Granary), out quantity, out _)); Assert.AreEqual(0, quantity);
            Assert.IsFalse(state.TryGetQuantity(armor, default, out quantity, out _)); Assert.AreEqual(0, quantity);
            Assert.IsTrue(state.TryGetQuantity(food, Store(2, 20, pool: StoragePool.Granary), out quantity, out string reason), reason); Assert.AreEqual(0, quantity);
            Assert.IsTrue(NativeStorageState.TryRestore(Array.Empty<NativeStorageGoodRegistration>(), goods, stores, out var dormant, out reason), reason);
            Assert.IsFalse(dormant.TryGetQuantity(armor, Store(), out quantity, out _)); Assert.AreEqual(0, quantity);
            Assert.IsTrue(dormant.TryRegister(armor, StoragePool.Armory, out reason), reason);
            Assert.IsTrue(dormant.TryGetQuantity(armor, Store(), out quantity, out reason), reason); Assert.AreEqual(3, quantity);
        }

        [TestMethod]
        public void ObservationContinuesAfterSameWorldLossReconciliationReplacesThePrivateState()
        {
            var state = new NativeStorageState(); var good = Register(state, "owner", "armor"); var first = Store(); var second = Store(2, 20);
            Add(state, good, first, 3); Add(state, good, second, 4);
            Assert.IsTrue(state.Reconcile(slot => slot == first.Slot ? first : default, out var reconciled, out string reason), reason);
            Assert.AreNotSame(state, reconciled); long sequence = reconciled.GetLastSequence(good.OwnerGuid);
            Assert.IsTrue(reconciled.TryGetQuantity(good, first, out int quantity, out reason), reason); Assert.AreEqual(3, quantity);
            Assert.AreEqual(sequence, reconciled.GetLastSequence(good.OwnerGuid));
        }
    }
}
