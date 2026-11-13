using System;
using System.Threading;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Content.Tests
{
    [TestClass]
    public sealed class PreparedGoodsCommitTests
    {
        private static CustomGoodsClient Setup(out CustomGoodsInventory inventory, out CustomGoodId good)
        {
            inventory = new CustomGoodsInventory(); var client = inventory.ForOwner("owner");
            Assert.IsTrue(client.TryRegister("armor", 10, out good, out _)); return client;
        }
        [TestMethod]
        public void ScratchPublicationWarmupDoesNotChangeAnExistingInventoryOrItsGeneration()
        {
            var client = Setup(out var inventory, out var good); byte[] before = inventory.Capture();
            CustomGoodsInventory.WarmNativePublication(); CustomGoodsInventory.WarmNativePublication();
            CollectionAssert.AreEqual(before, inventory.Capture()); Assert.AreEqual(0L, client.LastAcceptedSequence);
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, new[] { new GoodsChange(good, 1, 1) }));
        }
        [TestMethod]
        public void DisposingAnUnpublishedLeasePreservesStateWatermarkAndSchemaOneBytes()
        {
            var client = Setup(out var inventory, out var good); byte[] before = inventory.Capture();
            Assert.AreEqual(1, BitConverter.ToInt32(before, 4));
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.PrepareNative(1, new[] { new GoodsChange(good, 1, 1) }, new byte[] { 1, 5 }, out var lease));
            using (lease)
            {
                Assert.IsTrue(lease.IsPrepared);
                Assert.AreEqual(0L, client.LastAcceptedSequence);
                Assert.IsTrue(client.TryGetBalance("armor", 1, out var balance)); Assert.AreEqual(0, balance.Quantity);
                Assert.AreEqual(1, lease.OwnedChanges.Count);
            }
            CollectionAssert.AreEqual(before, inventory.Capture());
        }
        [TestMethod]
        public void NativeIntentPublishesOnceAndReplayIncludesPriceAndOperationKind()
        {
            var client = Setup(out var inventory, out var good); var lines = new[] { new GoodsChange(good, 1, 2) };
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.PrepareNative(1, lines, new byte[] { 1, 5 }, out var lease));
            using (lease) { lease.Publish(); lease.Publish(); Assert.IsTrue(lease.IsPublished); }
            Assert.AreEqual(1L, client.LastAcceptedSequence);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, client.PrepareNative(1, lines, new byte[] { 1, 5 }, out lease));
            lease.Dispose();
            Assert.AreEqual(GoodsTransactionStatus.Conflict, client.PrepareNative(1, lines, new byte[] { 1, 6 }, out lease)); Assert.IsNull(lease);
            Assert.AreEqual(GoodsTransactionStatus.Conflict, client.PrepareNative(1, lines, new byte[] { 2, 5 }, out lease));
            Assert.AreEqual(GoodsTransactionStatus.Conflict, client.Apply(1, lines));
            Assert.IsTrue(client.TryGetBalance("armor", 1, out var balance)); Assert.AreEqual(2, balance.Quantity);
            Assert.AreEqual(2, BitConverter.ToInt32(inventory.Capture(), 4));
        }
        [TestMethod]
        public void SchemaTwoRestoresIntentAndSchemaOneStillRoundTripsUnchanged()
        {
            var client = Setup(out var inventory, out var good);
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, new[] { new GoodsChange(good, 1, 4) }));
            byte[] one = inventory.Capture(); Assert.AreEqual(1, BitConverter.ToInt32(one, 4));
            var restored = Setup(out var next, out var nextGood);
            Assert.IsTrue(next.TryRestore(one, out var reason), reason); CollectionAssert.AreEqual(one, next.Capture());
            var lines = new[] { new GoodsChange(nextGood, 1, -1) };
            Assert.AreEqual(GoodsTransactionStatus.Applied, restored.PrepareNative(2, lines, new byte[] { 2, 9 }, out var lease));
            using (lease) lease.Publish();
            byte[] two = next.Capture(); Assert.AreEqual(2, BitConverter.ToInt32(two, 4));
            var third = Setup(out var thirdInventory, out var thirdGood);
            Assert.IsTrue(thirdInventory.TryRestore(two, out reason), reason); CollectionAssert.AreEqual(two, thirdInventory.Capture());
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, third.PrepareNative(2, new[] { new GoodsChange(thirdGood, 1, -1) }, new byte[] { 2, 9 }, out lease)); lease.Dispose();
            Assert.AreEqual(GoodsTransactionStatus.Conflict, third.PrepareNative(2, new[] { new GoodsChange(thirdGood, 1, -1) }, new byte[] { 2, 8 }, out lease));
            var malformed = new byte[two.Length + 1]; Array.Copy(two, malformed, two.Length);
            Assert.IsFalse(thirdInventory.TryRestore(malformed, out _)); CollectionAssert.AreEqual(two, thirdInventory.Capture());
        }
        [TestMethod]
        public void ReentrantWritesAndForeignThreadDisposalCannotInvalidatePreparedPublication()
        {
            var client = Setup(out var inventory, out var good); byte[] before = inventory.Capture();
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.PrepareNative(1, new[] { new GoodsChange(good, 1, 1) }, new byte[] { 1 }, out var lease));
            using (lease)
            {
                Assert.AreEqual(GoodsTransactionStatus.Conflict, client.Apply(2, new[] { new GoodsChange(good, 1, 3) }));
                Assert.IsFalse(client.TryRegister("extra", 2, out _, out _));
                Assert.IsFalse(inventory.TryRestore(before, out _));
                Assert.ThrowsExactly<InvalidOperationException>(inventory.Reset);
                Exception error = null; var thread = new Thread(() => { try { lease.Dispose(); } catch (Exception ex) { error = ex; } });
                thread.Start(); Assert.IsTrue(thread.Join(5000)); Assert.IsInstanceOfType<SynchronizationLockException>(error);
                Assert.IsTrue(lease.IsPrepared); lease.Publish();
            }
            Assert.IsTrue(client.TryGetBalance("armor", 1, out var balance)); Assert.AreEqual(1, balance.Quantity);
        }
        [TestMethod]
        public void RejectedIntentOrFundsDoNotEnterSchemaTwoOrAdvanceTheJournal()
        {
            var client = Setup(out var inventory, out var good); byte[] before = inventory.Capture();
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, client.PrepareNative(1, new[] { new GoodsChange(good, 1, 1) }, new byte[257], out _));
            Assert.AreEqual(GoodsTransactionStatus.InsufficientStock, client.PrepareNative(1, new[] { new GoodsChange(good, 1, -1) }, new byte[] { 1 }, out _));
            CollectionAssert.AreEqual(before, inventory.Capture());
        }
    }
}
