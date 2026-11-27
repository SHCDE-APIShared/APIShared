using System;
using APIShared.Economy;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Exercise the production state, private ledger, cargo rules and snapshot codec directly.
    // The probe supplies bounded native values; this establishes no live archive/world admission,
    // runtime receipt, installed-game ABI, native execution or gameplay acceptance.
    [TestClass]
    public sealed class NativeStorageImportStateTests
    {
        private const string Owner = "import.owner";
        private static byte[] Intent() => new byte[32];
        private static NativeStorageProbe Store(int slot, int player = 1, StoragePool kind = StoragePool.Armory, int native = 0) =>
            new NativeStorageProbe(true, slot, (uint)(slot * 10), player, kind, native, NativeStorageState.PoolCapacity(kind));
        private static NativeStorageState.Row Row(CustomGoodId good, int slot, int quantity, int player = 1,
            StoragePool kind = StoragePool.Armory) => new NativeStorageState.Row(good, slot, (uint)(slot * 10), player, kind, quantity);
        private static NativeStorageState.CargoLot Lot(CustomGoodId good, long sequence, int quantity = 7, int player = 1,
            StoragePool kind = StoragePool.Armory) => new NativeStorageState.CargoLot(good, sequence, player, kind, quantity, quantity, default);
        private static NativeStorageState Setup(out CustomGoodId armor, out CustomGoodId foreign)
        {
            var state = new NativeStorageState();
            armor = new CustomGoodId(Owner, "armor"); foreign = new CustomGoodId("other.owner", "shield");
            Assert.IsTrue(state.TryRegister(armor, StoragePool.Armory, out string reason), reason);
            Assert.IsTrue(state.TryRegister(foreign, StoragePool.Armory, out reason), reason);
            return state;
        }
        private static void Stock(NativeStorageState state, CustomGoodId good, long sequence, int quantity, int slot = 1)
        {
            Assert.AreEqual(GoodsTransactionStatus.Applied,
                state.PrepareExchange(good, sequence, Store(slot), quantity, 0, out var prepared, out string reason), reason);
            using (prepared) prepared.Publish();
        }
        private static void Cargo(NativeStorageState state, CustomGoodId good, long sequence, int quantity = 7)
        {
            Assert.AreEqual(GoodsTransactionStatus.Applied,
                state.PrepareCreateCargo(good, sequence, 1, quantity, out var prepared, out _, out string reason), reason);
            using (prepared) prepared.Publish();
        }
        private static (byte[] Goods, byte[] Stores) Capture(NativeStorageState state)
        { state.Capture(out byte[] goods, out byte[] stores); return (goods, stores); }
        private static void Unchanged((byte[] Goods, byte[] Stores) before, NativeStorageState state)
        {
            var after = Capture(state);
            CollectionAssert.AreEqual(before.Goods, after.Goods); CollectionAssert.AreEqual(before.Stores, after.Stores);
        }
        private static void Balance(NativeStorageState state, CustomGoodId good, int player, int expected)
        {
            Assert.IsTrue(state.TryGetBalance(good, player, out var balance));
            Assert.AreEqual(expected, balance.Quantity); Assert.AreEqual(0, balance.Reserved);
        }
        private static void Refused(NativeStorageState state, NativeStorageState.Row[] rows,
            NativeStorageState.CargoLot[] lots, Func<int, NativeStorageProbe> probe)
        {
            var before = Capture(state); object physical = state.PublicationIdentity, cargo = state.CargoPublicationIdentity;
            Assert.IsFalse(state.TryImport(Owner, rows, lots, Intent(), probe, out var candidate, out string reason));
            Assert.IsNull(candidate); Assert.IsFalse(string.IsNullOrEmpty(reason)); Unchanged(before, state);
            Assert.AreSame(physical, state.PublicationIdentity); Assert.AreSame(cargo, state.CargoPublicationIdentity);
        }

        [TestMethod]
        public void MixedImportMergesPhysicalRowsAndSortedCargoPreservingBothOwnersAndRoundTrips()
        {
            var state = Setup(out var armor, out var foreign);
            var food = new CustomGoodId(Owner, "food");
            Assert.IsTrue(state.TryRegister(food, StoragePool.Granary, out _));
            Stock(state, armor, 1, 5); Stock(state, foreign, 1, 4);
            Cargo(state, armor, 5); Cargo(state, foreign, 20, 11);
            var before = Capture(state);
            var rows = new[] { Row(food, 2, 8, kind: StoragePool.Granary), Row(armor, 3, 3, player: 2), Row(armor, 1, 6) };
            var lots = new[] { Lot(food, 9, 13, 2, StoragePool.Granary), Lot(armor, 8, 12) };
            NativeStorageProbe Probe(int slot) => slot == 2 ? Store(2, kind: StoragePool.Granary, native: 100) :
                slot == 3 ? Store(3, player: 2) : Store(1, native: 10);
            Assert.IsTrue(state.TryImport(Owner, rows, lots, Intent(), Probe, out var candidate, out string reason), reason);
            Assert.AreNotSame(state, candidate); Unchanged(before, state);
            Assert.AreEqual(3, candidate.GetRegistrations().Length); Assert.AreEqual(4, candidate.RowCount);
            Assert.AreEqual(11, candidate.GetQuantity(armor, Store(1))); Assert.AreEqual(4, candidate.GetQuantity(foreign, Store(1)));
            Assert.AreEqual(15, candidate.GetOccupied(1, 10, 1, StoragePool.Armory));
            Assert.AreEqual(8, candidate.GetQuantity(food, Store(2, kind: StoragePool.Granary)));
            Balance(candidate, armor, 1, 11); Balance(candidate, armor, 2, 3); Balance(candidate, food, 1, 8); Balance(candidate, foreign, 1, 4);
            Assert.AreEqual(2L, candidate.GetLastSequence(Owner)); Assert.AreEqual(1L, candidate.GetLastSequence(foreign.OwnerGuid));
            Assert.AreEqual(9L, candidate.GetLastCargoSequence(Owner)); Assert.AreEqual(20L, candidate.GetLastCargoSequence(foreign.OwnerGuid));
            Assert.IsTrue(candidate.TryGetCargo(Owner, 5, out var old)); Assert.AreEqual(7, old.Remaining);
            Assert.IsTrue(candidate.TryGetCargo(Owner, 8, out var armorLot)); Assert.AreEqual(12, armorLot.Remaining);
            Assert.IsTrue(candidate.TryGetCargo(Owner, 9, out var foodLot)); Assert.AreEqual(13, foodLot.Remaining); Assert.AreEqual(2, foodLot.Player);
            Assert.IsTrue(candidate.TryGetCargo(foreign.OwnerGuid, 20, out var foreignLot)); Assert.AreEqual(11, foreignLot.Remaining);
            var imported = Capture(candidate);
            Assert.IsTrue(NativeStorageState.TryRestore(candidate.GetRegistrations(), imported.Goods, imported.Stores,
                out var restored, out reason), reason);
            Unchanged(imported, restored); Balance(restored, armor, 1, 11);
            Assert.AreEqual(15, restored.GetOccupied(1, 10, 1, StoragePool.Armory));
            Assert.IsTrue(restored.TryGetCargo(Owner, 9, out foodLot)); Assert.AreEqual(13, foodLot.Remaining);
        }

        [TestMethod]
        public void CargoOnlySkipsPhysicalProbeAndExhaustedLedgerStreamButPhysicalImportRefusesExhaustion()
        {
            var state = Setup(out var armor, out _); Stock(state, armor, long.MaxValue, 5);
            var before = Capture(state);
            Assert.IsTrue(state.TryImport(Owner, Array.Empty<NativeStorageState.Row>(), new[] { Lot(armor, 1) }, Intent(),
                _ => throw new InvalidOperationException("Cargo must not probe storage."), out var candidate, out string reason), reason);
            Unchanged(before, state); CollectionAssert.AreEqual(before.Goods, Capture(candidate).Goods);
            Assert.AreEqual(long.MaxValue, candidate.GetLastSequence(Owner)); Assert.AreEqual(1L, candidate.GetLastCargoSequence(Owner));
            Balance(candidate, armor, 1, 5); Assert.AreEqual(5, candidate.GetQuantity(armor, Store(1)));
            Assert.IsTrue(candidate.TryGetCargo(Owner, 1, out var lot)); Assert.AreEqual(7, lot.Remaining);
            Refused(state, new[] { Row(armor, 1, 1) }, Array.Empty<NativeStorageState.CargoLot>(), StoreProbe);
        }

        private static NativeStorageProbe StoreProbe(int slot) => Store(slot);

        [TestMethod]
        public void ActorOnlyImportCanReturnACoherentCloneWithoutInventingStockOrSequences()
        {
            var state = Setup(out _, out _); var before = Capture(state);
            Assert.IsTrue(state.TryImport(Owner, Array.Empty<NativeStorageState.Row>(), Array.Empty<NativeStorageState.CargoLot>(),
                Intent(), null, out var candidate, out string reason), reason);
            Assert.IsNotNull(candidate); Unchanged(before, state); Unchanged(before, candidate);
            Assert.AreEqual(0L, candidate.GetLastSequence(Owner)); Assert.AreEqual(0L, candidate.GetLastCargoSequence(Owner));
            Assert.IsFalse(candidate.HasRows); Assert.IsFalse(candidate.HasCargoHistory);
        }

        [TestMethod]
        [DataRow("duplicate")]
        [DataRow("foreign")]
        [DataRow("unknown")]
        [DataRow("zero")]
        [DataRow("negative")]
        [DataRow("global")]
        [DataRow("player")]
        [DataRow("pool")]
        [DataRow("slot")]
        [DataRow("dead")]
        [DataRow("overcapacity")]
        [DataRow("probe-throws")]
        public void InvalidPhysicalRowsAndNativeBindingsRefuseWithoutChangingEitherSnapshot(string fault)
        {
            var state = Setup(out var armor, out var foreign); Stock(state, armor, 1, 5); Stock(state, foreign, 1, 4);
            var row = Row(armor, 1, 4);
            if (fault == "foreign") row = Row(foreign, 1, 4);
            if (fault == "unknown") row = Row(new CustomGoodId(Owner, "unknown"), 1, 4);
            if (fault == "zero" || fault == "negative") row = Row(armor, 1, fault == "zero" ? 0 : -1);
            if (fault == "global") row = new NativeStorageState.Row(armor, 1, 11, 1, StoragePool.Armory, 4);
            if (fault == "player") row = Row(armor, 1, 4, player: 2);
            if (fault == "pool") row = Row(armor, 1, 4, kind: StoragePool.Granary);
            NativeStorageProbe Probe(int slot)
            {
                if (fault == "probe-throws") throw new InvalidOperationException("Synthetic native read failure.");
                if (fault == "dead") return default;
                return Store(fault == "slot" ? 2 : slot, native: fault == "overcapacity" ? 38 : 0);
            }
            Refused(state, fault == "duplicate" ? new[] { row, row } : new[] { row }, Array.Empty<NativeStorageState.CargoLot>(), Probe);
        }

        [TestMethod]
        public void ReusedCurrentSlotAndDormantRegistrationAreNotReactivatedByTypedImport()
        {
            var state = Setup(out var armor, out var foreign); Stock(state, foreign, 1, 4);
            Refused(state, new[] { Row(armor, 1, 1) }, Array.Empty<NativeStorageState.CargoLot>(),
                slot => new NativeStorageProbe(true, slot, 11, 1, StoragePool.Armory, 0, 50));
            var saved = Capture(state);
            Assert.IsTrue(NativeStorageState.TryRestore(new[] { new NativeStorageGoodRegistration(foreign, StoragePool.Armory) },
                saved.Goods, saved.Stores, out var dormant, out string reason), reason);
            Refused(dormant, new[] { Row(armor, 1, 1) }, Array.Empty<NativeStorageState.CargoLot>(), StoreProbe);
            Refused(dormant, Array.Empty<NativeStorageState.Row>(), new[] { Lot(armor, 1) }, null);
        }

        [TestMethod]
        public void ImportAcceptsTheWorldRowBoundWithoutThePublicBatchLimitAndRejectsOnlyNewRowsBeyondIt()
        {
            var state = Setup(out _, out _);
            var goods = new CustomGoodId[5];
            for (int i = 0; i < goods.Length; i++)
            { goods[i] = new CustomGoodId(Owner, "row-good-" + i); Assert.IsTrue(state.TryRegister(goods[i], StoragePool.Armory, out _)); }
            var rows = new NativeStorageState.Row[NativeStorageState.MaximumRows];
            for (int i = 0; i < rows.Length; i++) rows[i] = Row(goods[i / (NativeStorageState.BuildingSlots - 1)], i % (NativeStorageState.BuildingSlots - 1) + 1, 1);
            var before = Capture(state);
            Assert.IsTrue(state.TryImport(Owner, rows, Array.Empty<NativeStorageState.CargoLot>(), Intent(), StoreProbe,
                out var full, out string reason), reason);
            Unchanged(before, state); Assert.AreEqual(NativeStorageState.MaximumRows, full.RowCount);
            Assert.AreEqual(1L, full.GetLastSequence(Owner)); Balance(full, goods[0], 1, NativeStorageState.BuildingSlots - 1);
            int nextSlot = NativeStorageState.MaximumRows % (NativeStorageState.BuildingSlots - 1) + 1;
            Refused(full, new[] { Row(goods[4], nextSlot, 1) }, Array.Empty<NativeStorageState.CargoLot>(), StoreProbe);
            Assert.IsTrue(full.TryImport(Owner, new[] { Row(goods[0], 1, 1) }, Array.Empty<NativeStorageState.CargoLot>(), Intent(), StoreProbe,
                out var merged, out reason), reason);
            Assert.AreEqual(NativeStorageState.MaximumRows, merged.RowCount); Assert.AreEqual(2, merged.GetQuantity(goods[0], Store(1)));
            Assert.AreEqual(1, full.GetQuantity(goods[0], Store(1)));
        }

        [TestMethod]
        public void PhysicalAccountTotalsAreAggregatedAndBoundedByTheExistingLedgerTransaction()
        {
            var state = Setup(out _, out _); var rows = new NativeStorageState.Row[33];
            for (int i = 0; i < rows.Length; i++)
            {
                var good = new CustomGoodId(Owner, "account-" + i);
                Assert.IsTrue(state.TryRegister(good, StoragePool.Armory, out _)); rows[i] = Row(good, i + 1, 1);
            }
            Refused(state, rows, Array.Empty<NativeStorageState.CargoLot>(), StoreProbe);
            var accepted = new NativeStorageState.Row[32]; Array.Copy(rows, accepted, accepted.Length);
            Assert.IsTrue(state.TryImport(Owner, accepted, Array.Empty<NativeStorageState.CargoLot>(), Intent(), StoreProbe,
                out var candidate, out string reason), reason);
            Assert.AreEqual(32, candidate.RowCount); Assert.AreEqual(1L, candidate.GetLastSequence(Owner));
            foreach (var row in accepted) Balance(candidate, row.Good, row.Player, 1);
        }

        [TestMethod]
        [DataRow("foreign")]
        [DataRow("duplicate")]
        [DataRow("current-key")]
        [DataRow("remaining")]
        [DataRow("receipt")]
        [DataRow("pool")]
        [DataRow("sequence")]
        [DataRow("player")]
        [DataRow("issued")]
        public void ImportedCargoMustBeNewUntouchedOwnerBoundProductionLots(string fault)
        {
            var state = Setup(out var armor, out var foreign); Cargo(state, armor, 1);
            var lot = Lot(armor, 2);
            if (fault == "foreign") lot = Lot(foreign, 2);
            if (fault == "current-key") lot = Lot(armor, 1);
            if (fault == "remaining") lot = lot.WithRemaining(6, default);
            if (fault == "receipt") lot = lot.WithRemaining(7,
                new NativeStorageState.CargoReceipt(NativeStorageState.CargoReceiptKind.Deposit, 8, 1, Store(1)));
            if (fault == "pool") lot = Lot(armor, 2, kind: StoragePool.Granary);
            if (fault == "sequence") lot = Lot(armor, 0);
            if (fault == "player") lot = Lot(armor, 2, player: 9);
            if (fault == "issued") lot = Lot(armor, 2, quantity: 0);
            Refused(state, new[] { Row(armor, 1, 3) }, fault == "duplicate" ? new[] { lot, lot } : new[] { lot }, StoreProbe);
        }

        [TestMethod]
        public void LateCargoSequenceFailureDiscardsTheAlreadyPreparedPhysicalCloneAndAllowsACorrectedRetry()
        {
            var state = Setup(out var armor, out var foreign); Stock(state, foreign, 1, 4); Cargo(state, armor, 10);
            Refused(state, new[] { Row(armor, 1, 3) }, new[] { Lot(armor, 5) }, StoreProbe);
            Assert.IsTrue(state.TryImport(Owner, new[] { Row(armor, 1, 3) }, new[] { Lot(armor, 11) }, Intent(), StoreProbe,
                out var candidate, out string reason), reason);
            Balance(candidate, armor, 1, 3); Balance(state, armor, 1, 0); Assert.AreEqual(1L, candidate.GetLastSequence(Owner));
            Assert.AreEqual(11L, candidate.GetLastCargoSequence(Owner)); Assert.AreEqual(10L, state.GetLastCargoSequence(Owner));
        }

        [TestMethod]
        public void LateFullCargoTableFailureDiscardsEarlierCargoAndPhysicalChangesTogether()
        {
            var state = Setup(out var armor, out _);
            for (int sequence = 1; sequence < NativeStorageState.MaximumCargoRecords; sequence++) Cargo(state, armor, sequence, 1);
            Refused(state, new[] { Row(armor, 1, 3) }, new[] {
                Lot(armor, NativeStorageState.MaximumCargoRecords), Lot(armor, NativeStorageState.MaximumCargoRecords + 1) }, StoreProbe);
            Assert.AreEqual(NativeStorageState.MaximumCargoRecords - 1, state.CargoRecordCount);
            Assert.AreEqual(0L, state.GetLastSequence(Owner));
            Assert.IsFalse(state.TryGetCargo(Owner, NativeStorageState.MaximumCargoRecords, out _));
        }
    }
}
