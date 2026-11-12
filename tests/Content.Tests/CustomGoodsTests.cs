using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Content.Tests
{
    [TestClass]
    public class CustomGoodsTests
    {
        private static CustomGoodId Register(CustomGoodsClient client, string localId, int capacity = 100)
        {
            Assert.IsTrue(client.TryRegister(localId, capacity, out CustomGoodId good, out string reason), reason);
            return good;
        }

        private static GoodsBalance Balance(CustomGoodsClient client, string localId, int player = 1)
        {
            Assert.IsTrue(client.TryGetBalance(localId, player, out GoodsBalance balance));
            return balance;
        }

        private static void AssertBalance(CustomGoodsClient client, string localId, int quantity, int reserved = 0, int player = 1)
        {
            GoodsBalance actual = Balance(client, localId, player);
            Assert.AreEqual(quantity, actual.Quantity, localId + " quantity");
            Assert.AreEqual(reserved, actual.Reserved, localId + " reserved");
            Assert.AreEqual(quantity - reserved, actual.Available, localId + " available");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void InsufficientSecondAccountRejectsTheEntireBatchAndDoesNotClaimItsSequence(bool reserve)
        {
            var inventory = new CustomGoodsInventory();
            CustomGoodsClient client = inventory.ForOwner("third.party");
            CustomGoodId first = Register(client, "a", 10), second = Register(client, "z", 10);
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, new[] {
                new GoodsChange(first, 1, 3), new GoodsChange(second, 1, 1) }));
            byte[] before = inventory.Capture();
            var rejected = new[] { new GoodsChange(first, 1, -2), new GoodsChange(second, 1, -2) };
            GoodsReservation handle = null;
            GoodsTransactionStatus result = reserve ? client.Reserve(2, rejected, out handle) : client.Apply(2, rejected);
            Assert.AreEqual(GoodsTransactionStatus.InsufficientStock, result);
            Assert.IsNull(handle);
            CollectionAssert.AreEqual(before, inventory.Capture());
            AssertBalance(client, "a", 3); AssertBalance(client, "z", 1);

            var corrected = new[] { new GoodsChange(first, 1, -2), new GoodsChange(second, 1, -1) };
            result = reserve ? client.Reserve(2, corrected, out handle) : client.Apply(2, corrected);
            Assert.AreEqual(reserve ? GoodsTransactionStatus.Reserved : GoodsTransactionStatus.Applied, result);
            if (reserve)
            {
                AssertBalance(client, "a", 3, 2); AssertBalance(client, "z", 1, 1);
                Assert.AreEqual(GoodsTransactionStatus.Committed, handle.Commit());
            }
            AssertBalance(client, "a", 1); AssertBalance(client, "z", 0);
        }

        [TestMethod]
        public void CapacityFailureOnTheSecondAccountDoesNotPublishTheFirstDeposit()
        {
            var inventory = new CustomGoodsInventory();
            CustomGoodsClient client = inventory.ForOwner("third.party");
            CustomGoodId first = Register(client, "a", 10), second = Register(client, "z", 10);
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, new[] { new GoodsChange(second, 1, 9) }));
            byte[] before = inventory.Capture();
            Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, client.Apply(2, new[] {
                new GoodsChange(first, 1, 2), new GoodsChange(second, 1, 2) }));
            CollectionAssert.AreEqual(before, inventory.Capture());
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(2, new[] {
                new GoodsChange(first, 1, 2), new GoodsChange(second, 1, 1) }));
            AssertBalance(client, "a", 2); AssertBalance(client, "z", 10);
        }

        [TestMethod]
        public void DuplicateLinesAreNettedBeforeAffordabilityAndReceiptComparison()
        {
            var inventory = new CustomGoodsInventory();
            CustomGoodsClient client = inventory.ForOwner("third.party");
            CustomGoodId good = Register(client, "ore", 10);
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, new[] { new GoodsChange(good, 1, 5) }));
            var lines = new[] { new GoodsChange(good, 1, -10), new GoodsChange(good, 1, 7), new GoodsChange(good, 1, 2) };
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(2, lines));
            AssertBalance(client, "ore", 4);
            lines[0] = new GoodsChange(good, 1, 100);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, client.Apply(2, new[] { new GoodsChange(good, 1, -1) }));
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(3, new[] {
                new GoodsChange(good, 1, -99), new GoodsChange(good, 1, 99) }));
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, client.Apply(3, new[] {
                new GoodsChange(good, 1, 2), new GoodsChange(good, 1, -2) }));
            AssertBalance(client, "ore", 4);
        }

        [TestMethod]
        public void Int32ExtremesAreOrderIndependentAndNeverWrapStockOrHolds()
        {
            byte[] canonical = null;
            foreach (int[] deltas in new[] {
                new[] { int.MaxValue, int.MaxValue, -int.MaxValue },
                new[] { -int.MaxValue, int.MaxValue, int.MaxValue } })
            {
                var inventory = new CustomGoodsInventory();
                CustomGoodsClient client = inventory.ForOwner("third.party");
                CustomGoodId good = Register(client, "ore", int.MaxValue);
                Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, deltas.Select(delta => new GoodsChange(good, 1, delta)).ToArray()));
                AssertBalance(client, "ore", int.MaxValue);
                byte[] before = inventory.Capture();
                if (canonical == null) canonical = before; else CollectionAssert.AreEqual(canonical, before);
                Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, client.Apply(2, new[] { new GoodsChange(good, 1, 1) }));
                Assert.AreEqual(GoodsTransactionStatus.InsufficientStock, client.Apply(3, new[] { new GoodsChange(good, 1, int.MinValue) }));
                Assert.AreEqual(GoodsTransactionStatus.InsufficientStock, client.Reserve(4, new[] { new GoodsChange(good, 1, int.MinValue) }, out GoodsReservation rejected));
                Assert.IsNull(rejected);
                Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, client.Apply(5, new[] {
                    new GoodsChange(good, 1, int.MaxValue), new GoodsChange(good, 1, int.MaxValue) }));
                CollectionAssert.AreEqual(before, inventory.Capture());
                Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(2, new[] { new GoodsChange(good, 1, -int.MaxValue) }));
                Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, client.Apply(1, new[] { new GoodsChange(good, 1, int.MaxValue) }));
                AssertBalance(client, "ore", 0);
            }
        }

        [TestMethod]
        public void ReceiptsAreOwnerScopedAndRejectChangedPayloadsOrOperationKinds()
        {
            var inventory = new CustomGoodsInventory();
            CustomGoodsClient first = inventory.ForOwner("Case.Mod"), second = inventory.ForOwner("case.mod");
            CustomGoodId firstGood = Register(first, "ore", 10), secondGood = Register(second, "ore", 10);
            Assert.AreNotEqual(firstGood, secondGood);
            Assert.AreEqual(GoodsTransactionStatus.Applied, first.Apply(1, new[] { new GoodsChange(firstGood, 1, 5) }));
            Assert.AreEqual(GoodsTransactionStatus.Applied, second.Apply(1, new[] { new GoodsChange(secondGood, 1, 3) }));
            Assert.AreEqual(GoodsTransactionStatus.Applied, first.Apply(2, new[] { new GoodsChange(firstGood, 1, -2) }));
            byte[] before = inventory.Capture();
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, first.Apply(2, new[] {
                new GoodsChange(firstGood, 1, -1), new GoodsChange(firstGood, 1, -1) }));
            Assert.AreEqual(GoodsTransactionStatus.Conflict, first.Apply(2, new[] { new GoodsChange(firstGood, 1, -1) }));
            Assert.AreEqual(GoodsTransactionStatus.Conflict, first.Reserve(2, new[] { new GoodsChange(firstGood, 1, -2) }, out GoodsReservation conflicting));
            Assert.IsNull(conflicting);
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, first.Apply(3, new[] { new GoodsChange(secondGood, 1, -1) }));
            CollectionAssert.AreEqual(before, inventory.Capture());
            AssertBalance(first, "ore", 3); AssertBalance(second, "ore", 3);
        }

        [TestMethod]
        public void HoldsPreventOverbookingAndCommitOrReleaseExactlyOnce()
        {
            var inventory = new CustomGoodsInventory();
            CustomGoodsClient client = inventory.ForOwner("third.party");
            CustomGoodId good = Register(client, "ore", 10);
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, new[] { new GoodsChange(good, 1, 10) }));
            var held = new[] { new GoodsChange(good, 1, -6) };
            Assert.AreEqual(GoodsTransactionStatus.Reserved, client.Reserve(2, held, out GoodsReservation handle));
            Assert.AreEqual(GoodsTransactionStatus.AlreadyReserved, client.Reserve(2, new[] {
                new GoodsChange(good, 1, -3), new GoodsChange(good, 1, -3) }, out GoodsReservation duplicate));
            AssertBalance(client, "ore", 10, 6);
            Assert.AreEqual(GoodsTransactionStatus.InsufficientStock, client.Apply(3, new[] { new GoodsChange(good, 1, -5) }));
            Assert.AreEqual(GoodsTransactionStatus.InsufficientStock, client.Reserve(4, new[] { new GoodsChange(good, 1, -5) }, out GoodsReservation rejected));
            Assert.IsNull(rejected);
            Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, client.Apply(5, new[] { new GoodsChange(good, 1, 1) }));
            Assert.AreEqual(GoodsTransactionStatus.Committed, handle.Commit());
            Assert.AreEqual(GoodsTransactionStatus.AlreadyCommitted, duplicate.Commit());
            Assert.AreEqual(GoodsTransactionStatus.Conflict, handle.Release());
            Assert.AreEqual(GoodsTransactionStatus.AlreadyCommitted, client.Reserve(2, held, out GoodsReservation committed));
            Assert.AreEqual(GoodsTransactionStatus.AlreadyCommitted, committed.Commit());
            AssertBalance(client, "ore", 4);

            var releaseLines = new[] { new GoodsChange(good, 1, -3) };
            Assert.AreEqual(GoodsTransactionStatus.Reserved, client.Reserve(3, releaseLines, out GoodsReservation release));
            Assert.AreEqual(GoodsTransactionStatus.Released, release.Release());
            Assert.AreEqual(GoodsTransactionStatus.AlreadyReleased, release.Release());
            Assert.AreEqual(GoodsTransactionStatus.Conflict, release.Commit());
            Assert.AreEqual(GoodsTransactionStatus.AlreadyReleased, client.Reserve(3, releaseLines, out GoodsReservation released));
            Assert.AreEqual(GoodsTransactionStatus.AlreadyReleased, released.Release());
            AssertBalance(client, "ore", 4);
        }

        [TestMethod]
        public void ResetInvalidatesOldHandlesAndClearsReceiptsButRetainsTheCatalog()
        {
            var inventory = new CustomGoodsInventory();
            CustomGoodsClient client = inventory.ForOwner("third.party");
            CustomGoodId good = Register(client, "ore", 10);
            var seed = new[] { new GoodsChange(good, 1, 5) };
            var lines = new[] { new GoodsChange(good, 1, -2) };
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, seed));
            Assert.AreEqual(GoodsTransactionStatus.Reserved, client.Reserve(2, lines, out GoodsReservation old));
            inventory.Reset();
            AssertBalance(client, "ore", 0);
            Assert.AreEqual(10, Balance(client, "ore").Capacity);
            Assert.AreEqual(GoodsTransactionStatus.StaleReservation, old.Commit());
            Assert.AreEqual(GoodsTransactionStatus.StaleReservation, old.Release());
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, seed));
            Assert.AreEqual(GoodsTransactionStatus.Reserved, client.Reserve(2, lines, out GoodsReservation current));
            Assert.AreEqual(GoodsTransactionStatus.Committed, current.Commit());
            AssertBalance(client, "ore", 3);
        }

        [TestMethod]
        public void CaptureIsCanonicalAcrossRegistrationOwnerAndLineOrder()
        {
            byte[] Capture(bool reverse)
            {
                var inventory = new CustomGoodsInventory();
                CustomGoodsClient a = inventory.ForOwner("A.owner"), z = inventory.ForOwner("z.owner");
                var registrations = new Action[] { () => Register(a, "z"), () => Register(a, "a"), () => Register(z, "a") };
                foreach (Action registration in reverse ? registrations.Reverse() : registrations) registration();
                var az = new CustomGoodId("A.owner", "z");
                var aa = new CustomGoodId("A.owner", "a");
                var za = new CustomGoodId("z.owner", "a");
                // Arrival order may differ between owners. Within an owner, later accepted
                // sequences close lower gaps, so keep that owner's accepted history identical.
                var actions = new Action[] {
                    () => {
                        Assert.AreEqual(GoodsTransactionStatus.Applied, a.Apply(1, new[] { new GoodsChange(az, 8, 2), new GoodsChange(aa, 1, 3) }));
                        Assert.AreEqual(GoodsTransactionStatus.Applied, a.Apply(2, reverse ? new[] { new GoodsChange(aa, 1, 4), new GoodsChange(az, 8, 5) } : new[] { new GoodsChange(az, 8, 5), new GoodsChange(aa, 1, 4) }));
                    },
                    () => Assert.AreEqual(GoodsTransactionStatus.Applied, z.Apply(1, new[] { new GoodsChange(za, 1, 9) })) };
                foreach (Action action in reverse ? actions.Reverse() : actions) action();
                return inventory.Capture();
            }
            CollectionAssert.AreEqual(Capture(false), Capture(true));
        }

        [TestMethod]
        public void RestoreResumesHoldsAndTerminalReceiptsWithoutHoldingOrDebitingTwice()
        {
            var source = new CustomGoodsInventory();
            CustomGoodsClient saved = source.ForOwner("third.party");
            CustomGoodId good = Register(saved, "ore", 20);
            var seed = new[] { new GoodsChange(good, 1, 10) };
            var activeLines = new[] { new GoodsChange(good, 1, -4) };
            var committedLines = new[] { new GoodsChange(good, 1, -2) };
            var releasedLines = new[] { new GoodsChange(good, 1, -1) };
            Assert.AreEqual(GoodsTransactionStatus.Applied, saved.Apply(1, seed));
            Assert.AreEqual(GoodsTransactionStatus.Reserved, saved.Reserve(2, activeLines, out GoodsReservation sourceHandle));
            Assert.AreEqual(GoodsTransactionStatus.Reserved, saved.Reserve(3, committedLines, out GoodsReservation committed));
            Assert.AreEqual(GoodsTransactionStatus.Committed, committed.Commit());
            Assert.AreEqual(GoodsTransactionStatus.Reserved, saved.Reserve(4, releasedLines, out GoodsReservation released));
            Assert.AreEqual(GoodsTransactionStatus.Released, released.Release());
            byte[] snapshot = source.Capture();

            var restored = new CustomGoodsInventory();
            CustomGoodsClient client = restored.ForOwner("third.party");
            Register(client, "ore", 20);
            CustomGoodId extra = Register(client, "extra", 20);
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, new[] { new GoodsChange(extra, 1, 5) }));
            Assert.AreEqual(GoodsTransactionStatus.Reserved, client.Reserve(2, new[] { new GoodsChange(extra, 1, -1) }, out GoodsReservation old));
            Assert.IsTrue(restored.TryRestore(snapshot, out string reason), reason);
            Assert.AreEqual(GoodsTransactionStatus.StaleReservation, old.Commit());
            Assert.AreEqual(GoodsTransactionStatus.StaleReservation, old.Release());
            AssertBalance(client, "extra", 0); AssertBalance(client, "ore", 8, 4);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, client.Apply(1, seed));
            Assert.AreEqual(GoodsTransactionStatus.AlreadyReserved, client.Reserve(2, activeLines, out GoodsReservation resumed));
            AssertBalance(client, "ore", 8, 4);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyCommitted, client.Reserve(3, committedLines, out GoodsReservation completed));
            Assert.AreEqual(GoodsTransactionStatus.Conflict, completed.Release());
            Assert.AreEqual(GoodsTransactionStatus.AlreadyReleased, client.Reserve(4, releasedLines, out GoodsReservation cancelled));
            Assert.AreEqual(GoodsTransactionStatus.Conflict, cancelled.Commit());
            Assert.AreEqual(GoodsTransactionStatus.Committed, resumed.Commit());
            Assert.AreEqual(GoodsTransactionStatus.AlreadyCommitted, resumed.Commit());
            AssertBalance(client, "ore", 4);
            AssertBalance(saved, "ore", 8, 4);
            byte[] independent = restored.Capture();
            Assert.AreEqual(GoodsTransactionStatus.Committed, sourceHandle.Commit());
            AssertBalance(saved, "ore", 4);
            CollectionAssert.AreEqual(independent, restored.Capture());
        }

        [TestMethod]
        public void CorruptSnapshotsPreserveBalancesReceiptsAndExistingHandleValidity()
        {
            var inventory = new CustomGoodsInventory();
            CustomGoodsClient client = inventory.ForOwner("third.party");
            CustomGoodId good = Register(client, "ore", 10);
            var seed = new[] { new GoodsChange(good, 1, 7) };
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, seed));
            Assert.AreEqual(GoodsTransactionStatus.Reserved, client.Reserve(2, new[] { new GoodsChange(good, 1, -2) }, out GoodsReservation handle));
            byte[] valid = inventory.Capture();
            byte[] badMagic = (byte[])valid.Clone(); badMagic[0] ^= 0xFF;
            // Schema-1 strings use a 7-bit encoded length. Five continuation bytes must be
            // rejected as malformed input, rather than escaping TryRestore as FormatException.
            byte[] badLength;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                { writer.Write(0x31494743); writer.Write(1); writer.Write(1); writer.Write(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 }); }
                badLength = stream.ToArray();
            }
            foreach (byte[] invalid in new[] { null, Array.Empty<byte>(), badMagic, valid.Take(valid.Length - 1).ToArray(),
                valid.Concat(new byte[] { 0 }).ToArray(), badLength, new byte[CustomGoodsInventory.MaximumSnapshotBytes + 1] })
            {
                Assert.IsFalse(inventory.TryRestore(invalid, out string reason));
                Assert.IsFalse(string.IsNullOrEmpty(reason));
                CollectionAssert.AreEqual(valid, inventory.Capture());
            }
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, client.Apply(1, seed));
            Assert.AreEqual(GoodsTransactionStatus.Committed, handle.Commit());
            AssertBalance(client, "ore", 5);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RestoreRejectsUnknownGoodsOrChangedCapacitiesWithoutReplacingLiveState(bool changedCapacity)
        {
            var source = new CustomGoodsInventory();
            CustomGoodsClient saved = source.ForOwner("third.party");
            CustomGoodId savedGood = Register(saved, "ore", 10);
            Assert.AreEqual(GoodsTransactionStatus.Applied, saved.Apply(1, new[] { new GoodsChange(savedGood, 1, 5) }));
            var target = new CustomGoodsInventory();
            CustomGoodsClient live = target.ForOwner("third.party");
            if (changedCapacity) Register(live, "ore", 11);
            CustomGoodId current = Register(live, "current", 20);
            Assert.AreEqual(GoodsTransactionStatus.Applied, live.Apply(1, new[] { new GoodsChange(current, 1, 9) }));
            Assert.AreEqual(GoodsTransactionStatus.Reserved, live.Reserve(2, new[] { new GoodsChange(current, 1, -3) }, out GoodsReservation handle));
            byte[] before = target.Capture();
            Assert.IsFalse(target.TryRestore(source.Capture(), out string reason));
            Assert.IsFalse(string.IsNullOrEmpty(reason));
            CollectionAssert.AreEqual(before, target.Capture());
            Assert.AreEqual(GoodsTransactionStatus.Committed, handle.Commit());
            AssertBalance(live, "current", 6);
        }

        [TestMethod]
        public void RecentJournalEvictsTerminalReceiptsWithoutAllowingOldSequencesToExecuteAgain()
        {
            var inventory = new CustomGoodsInventory(maximumOperations: 3);
            CustomGoodsClient client = inventory.ForOwner("third.party");
            CustomGoodId good = Register(client, "ore", 2000);
            var deposit = new[] { new GoodsChange(good, 1, 1) };
            // Exceed the maximum supported recent-journal size as well as this instance's
            // small bound. A long mission must keep progressing without forgetting safety.
            for (long sequence = 1; sequence <= 1030; sequence++)
                Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(sequence, deposit));
            AssertBalance(client, "ore", 1030);
            Assert.AreEqual(1030L, client.LastAcceptedSequence);
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, client.Apply(1, deposit));
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, client.Apply(1030, deposit));
            Assert.AreEqual(GoodsTransactionStatus.Conflict, client.Apply(1030, new[] { new GoodsChange(good, 1, 2) }));
            AssertBalance(client, "ore", 1030);

            var restored = new CustomGoodsInventory(maximumOperations: 3);
            CustomGoodsClient resumed = restored.ForOwner("third.party");
            Register(resumed, "ore", 2000);
            Assert.IsTrue(restored.TryRestore(inventory.Capture(), out string reason), reason);
            Assert.AreEqual(1030L, resumed.LastAcceptedSequence);
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, resumed.Apply(1, deposit));
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, resumed.Apply(1030, deposit));
            Assert.AreEqual(GoodsTransactionStatus.Applied, resumed.Apply(1031, deposit));
            AssertBalance(resumed, "ore", 1031);
        }

        [TestMethod]
        public void ActiveHoldsAreNeverEvictedAndAFullActiveJournalDoesNotAdvanceTheSequence()
        {
            var inventory = new CustomGoodsInventory(maximumOperations: 2);
            CustomGoodsClient client = inventory.ForOwner("third.party");
            CustomGoodId good = Register(client, "ore", 20);
            var firstHold = new[] { new GoodsChange(good, 1, -2) };
            var secondHold = new[] { new GoodsChange(good, 1, -3) };
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, new[] { new GoodsChange(good, 1, 10) }));
            Assert.AreEqual(GoodsTransactionStatus.Reserved, client.Reserve(2, firstHold, out GoodsReservation handle));
            Assert.AreEqual(GoodsTransactionStatus.Reserved, client.Reserve(3, secondHold, out GoodsReservation active));
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, client.Apply(1, new[] { new GoodsChange(good, 1, 10) }));
            byte[] full = inventory.Capture();
            Assert.AreEqual(GoodsTransactionStatus.LimitReached, client.Apply(4, new[] { new GoodsChange(good, 1, 1) }));
            Assert.AreEqual(GoodsTransactionStatus.LimitReached, client.Reserve(5, new[] { new GoodsChange(good, 1, -1) }, out GoodsReservation rejected));
            Assert.IsNull(rejected);
            Assert.AreEqual(3L, client.LastAcceptedSequence);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyReserved, client.Reserve(2, firstHold, out _));
            Assert.AreEqual(GoodsTransactionStatus.AlreadyReserved, client.Reserve(3, secondHold, out _));
            CollectionAssert.AreEqual(full, inventory.Capture());
            Assert.AreEqual(GoodsTransactionStatus.Committed, handle.Commit());
            Assert.AreEqual(GoodsTransactionStatus.AlreadyCommitted, handle.Commit());
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(4, new[] { new GoodsChange(good, 1, 1) }));
            Assert.AreEqual(4L, client.LastAcceptedSequence);
            AssertBalance(client, "ore", 9, 3);
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, handle.Commit());
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, handle.Release());
            Assert.AreEqual(GoodsTransactionStatus.AlreadyReserved, client.Reserve(3, secondHold, out _));
            AssertBalance(client, "ore", 9, 3);
            Assert.AreEqual(GoodsTransactionStatus.Released, active.Release());
            AssertBalance(client, "ore", 9);
        }

        [TestMethod]
        public void AcceptedSequencesCloseGapsButRejectedRequestsDoNotAdvanceTheHighWaterMark()
        {
            var inventory = new CustomGoodsInventory();
            CustomGoodsClient client = inventory.ForOwner("third.party");
            CustomGoodId good = Register(client, "ore", 20);
            var one = new[] { new GoodsChange(good, 1, 1) };
            Assert.AreEqual(0L, client.LastAcceptedSequence);
            foreach (long invalid in new[] { 0L, -1L, long.MinValue })
                Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, client.Apply(invalid, one));
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(10, new[] { new GoodsChange(good, 1, 5) }));
            Assert.AreEqual(10L, client.LastAcceptedSequence);
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, client.Apply(1, one));
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, client.Apply(9, one));
            Assert.AreEqual(GoodsTransactionStatus.InsufficientStock, client.Apply(20, new[] { new GoodsChange(good, 1, -6) }));
            Assert.AreEqual(10L, client.LastAcceptedSequence);
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(11, new[] { new GoodsChange(good, 1, -1) }));
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(15, one));
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, client.Apply(12, one));
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(long.MaxValue, one));
            Assert.AreEqual(long.MaxValue, client.LastAcceptedSequence);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, client.Apply(long.MaxValue, one));
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, client.Apply(long.MaxValue - 1, one));
            Assert.AreEqual(0L, inventory.ForOwner("other.owner").LastAcceptedSequence);

            var restored = new CustomGoodsInventory();
            CustomGoodsClient resumed = restored.ForOwner("third.party");
            Register(resumed, "ore", 20);
            Assert.IsTrue(restored.TryRestore(inventory.Capture(), out string reason), reason);
            Assert.AreEqual(long.MaxValue, resumed.LastAcceptedSequence);
            Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, resumed.Apply(long.MaxValue, one));
            Assert.AreEqual(GoodsTransactionStatus.ExpiredOperation, resumed.Apply(long.MaxValue - 1, one));
            AssertBalance(resumed, "ore", 6);
            restored.Reset();
            Assert.AreEqual(0L, resumed.LastAcceptedSequence);
            Assert.AreEqual(GoodsTransactionStatus.Applied, resumed.Apply(1, one));
            AssertBalance(resumed, "ore", 1);
        }

        [TestMethod]
        public void CallerListIsSnapshottedWithoutCallingConsumerCodeUnderTheInventoryLock()
        {
            var inventory = new CustomGoodsInventory();
            CustomGoodsClient client = inventory.ForOwner("third.party");
            CustomGoodId good = Register(client, "ore", 10);
            var list = new ChangingList(new GoodsChange(good, 1, 3), () => client.TryGetBalance("ore", 1, out _));
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, list));
            AssertBalance(client, "ore", 3);
        }

        [TestMethod]
        public void ConcurrentDebitsConserveStockAcrossAcceptedAndExpiredSequences()
        {
            const int stock = 64, attempts = 128;
            var inventory = new CustomGoodsInventory();
            CustomGoodsClient client = inventory.ForOwner("third.party");
            CustomGoodId good = Register(client, "ore", attempts);
            Assert.AreEqual(GoodsTransactionStatus.Applied, client.Apply(1, new[] { new GoodsChange(good, 1, stock) }));
            var results = new GoodsTransactionStatus[attempts];
            Parallel.For(0, attempts, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index => {
                results[index] = client.Apply(index + 2L, new[] { new GoodsChange(good, 1, -1), new GoodsChange(good, 2, 1) });
            });
            int applied = results.Count(result => result == GoodsTransactionStatus.Applied);
            Assert.IsTrue(applied >= 1 && applied <= stock);
            Assert.AreEqual(attempts, results.Count(result => result == GoodsTransactionStatus.Applied ||
                result == GoodsTransactionStatus.InsufficientStock || result == GoodsTransactionStatus.ExpiredOperation));
            AssertBalance(client, "ore", stock - applied); AssertBalance(client, "ore", applied, player: 2);
            Assert.AreEqual(Enumerable.Range(0, attempts).Where(index => results[index] == GoodsTransactionStatus.Applied).Max(index => index + 2L),
                client.LastAcceptedSequence);
            for (int index = 0; index < attempts; index++)
                if (results[index] == GoodsTransactionStatus.Applied)
                    Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, client.Apply(index + 2L,
                        new[] { new GoodsChange(good, 2, 1), new GoodsChange(good, 1, -1) }));
            Assert.AreEqual(stock, Balance(client, "ore").Quantity + Balance(client, "ore", 2).Quantity);
        }

        private sealed class ChangingList : IReadOnlyList<GoodsChange>
        {
            private readonly GoodsChange change;
            private readonly Func<bool> readBalance;
            private int countReads;
            internal ChangingList(GoodsChange change, Func<bool> readBalance)
            { this.change = change; this.readBalance = readBalance; }
            // A later Count is intentionally inconsistent. One initial snapshot must determine
            // the submitted payload; an independently scheduled read exposes lock-held consumer callbacks.
            public int Count => Interlocked.Increment(ref countReads) == 1 ? 1 : 33;
            public GoodsChange this[int index]
            {
                get
                {
                    if (index != 0) throw new IndexOutOfRangeException();
                    Task<bool> read = Task.Run(readBalance);
                    if (!read.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Caller list getter could not read the inventory from another thread.");
                    Assert.IsTrue(read.Result);
                    return change;
                }
            }
            public IEnumerator<GoodsChange> GetEnumerator() { yield return this[0]; }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
