using System;
using APIShared.Economy;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Exercise the production value boundary after the existing protected EnsureWorld/native
    // probe binding. These fixtures do not read a native manager or execute game code.
    [TestClass]
    public sealed class NativeStorageQuantityTests
    {
        private static NativeStorageProbe Store(int slot = 1, uint global = 0xF0000000, int player = 1, StoragePool pool = StoragePool.Armory) =>
            new NativeStorageProbe(true, slot, global, player, pool, 0, pool == StoragePool.Armory ? 50 : 250);
        private static NativeStorageState Setup(out CustomGoodId good)
        {
            var state = new NativeStorageState(); good = new CustomGoodId("armor.owner", "armor");
            Assert.IsTrue(state.TryRegister(good, StoragePool.Armory, out _));
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareExchange(good, 1, Store(), 3, 0, out var plan, out string reason), reason);
            using (plan) plan.Publish(); return state;
        }
        [TestMethod]
        public void ProtectedQuantityBoundaryReturnsOnlyTheRequestedGoodWithoutAdvancingState()
        {
            var state = Setup(out var good); object world = new object(); var expected = new NativeStorageSnapshot(world, Store(), 5);
            state.Capture(out byte[] beforeGoods, out byte[] beforeStores); object publication = state.PublicationIdentity, cargo = state.CargoPublicationIdentity;
            Assert.IsTrue(NativeStorageRuntime.TryReadQuantity(state, good, world, false, expected, Store(), out int quantity, out string reason), reason);
            Assert.AreEqual(3, quantity, "A snapshot's old combined virtual occupancy is not this good's quantity.");
            Assert.AreEqual(1L, state.GetLastSequence(good.OwnerGuid)); Assert.AreSame(publication, state.PublicationIdentity); Assert.AreSame(cargo, state.CargoPublicationIdentity);
            state.Capture(out byte[] afterGoods, out byte[] afterStores); CollectionAssert.AreEqual(beforeGoods, afterGoods); CollectionAssert.AreEqual(beforeStores, afterStores);
        }
        [TestMethod]
        [DataRow("world")]
        [DataRow("slot")]
        [DataRow("global")]
        [DataRow("owner")]
        [DataRow("pool")]
        [DataRow("dead")]
        [DataRow("default")]
        [DataRow("blocked")]
        [DataRow("pending")]
        [DataRow("unknown-world")]
        public void StaleOrUnavailableObservationFailsInsteadOfLeakingOldQuantity(string mismatch)
        {
            var state = Setup(out var good); object world = new object(); var expected = new NativeStorageSnapshot(world, Store(), 3);
            var current = mismatch == "slot" ? Store(slot: 2) : mismatch == "global" ? Store(global: 11) : mismatch == "owner" ? Store(player: 2) :
                mismatch == "pool" ? Store(pool: StoragePool.Granary) : mismatch == "dead" ? default : Store();
            Assert.IsFalse(NativeStorageRuntime.TryReadQuantity(mismatch == "pending" ? null : state, good,
                mismatch == "world" ? new object() : mismatch == "unknown-world" ? null : world, mismatch == "blocked",
                mismatch == "default" ? default : expected, current, out int quantity, out string reason));
            Assert.AreEqual(0, quantity); Assert.IsFalse(string.IsNullOrEmpty(reason));
        }
        [TestMethod]
        public void FreshReusedOrCapturedSnapshotIsValidEmptyAndTheOldSnapshotStaysStale()
        {
            var state = Setup(out var good); object world = new object(); var old = new NativeStorageSnapshot(world, Store(), 3);
            foreach (var current in new[] { Store(global: 11), Store(player: 2) })
            {
                Assert.IsFalse(NativeStorageRuntime.TryReadQuantity(state, good, world, false, old, current, out int quantity, out _)); Assert.AreEqual(0, quantity);
                var fresh = new NativeStorageSnapshot(world, current, 0);
                Assert.IsTrue(NativeStorageRuntime.TryReadQuantity(state, good, world, false, fresh, current, out quantity, out string reason), reason); Assert.AreEqual(0, quantity);
            }
            Assert.AreEqual(1L, state.GetLastSequence(good.OwnerGuid));
        }
        [TestMethod]
        public void RestoredQuantityRequiresActiveGoodAndReacquiredCurrentWorldButNotTheOldStateObject()
        {
            var state = Setup(out var good); state.Capture(out byte[] goods, out byte[] stores); object oldWorld = new object(), currentWorld = new object();
            Assert.IsTrue(NativeStorageState.TryRestore(state.GetRegistrations(), goods, stores, out var restored, out string reason), reason);
            var beforeLoad = new NativeStorageSnapshot(oldWorld, Store(), 3); var afterLoad = new NativeStorageSnapshot(currentWorld, Store(), 3);
            Assert.IsFalse(NativeStorageRuntime.TryReadQuantity(restored, good, currentWorld, false, beforeLoad, Store(), out int quantity, out _)); Assert.AreEqual(0, quantity);
            Assert.IsTrue(NativeStorageRuntime.TryReadQuantity(restored, good, currentWorld, false, afterLoad, Store(), out quantity, out reason), reason); Assert.AreEqual(3, quantity);
            Assert.IsTrue(NativeStorageState.TryRestore(Array.Empty<NativeStorageGoodRegistration>(), goods, stores, out var dormant, out reason), reason);
            Assert.IsFalse(NativeStorageRuntime.TryReadQuantity(dormant, good, currentWorld, false, afterLoad, Store(), out quantity, out _)); Assert.AreEqual(0, quantity);
        }
        [TestMethod]
        public void PublicQueryRejectsAFabricatedHandleBeforeAnyNativeWorldRead()
        {
            // Same-shaped values are not the process-owned registered instance, regardless of
            // whether some other test has initialized the framework. No native state is touched.
            var unregistered = new NativeStoredGood(new CustomGoodId("quantity.test", "armor"), StoragePool.Armory);
            Assert.IsFalse(unregistered.TryGetQuantity(default, out int quantity, out string reason));
            Assert.AreEqual(0, quantity); Assert.IsFalse(string.IsNullOrEmpty(reason));
        }
    }
}
