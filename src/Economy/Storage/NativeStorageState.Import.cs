using System;
using System.Collections.Generic;
using APIShared.Goods;

namespace APIShared.Economy
{
    internal sealed partial class NativeStorageState
    {
        // This cold, private import seam accepts typed additions, never caller-supplied ledger
        // bytes. The caller owns archive/world qualification and the one-use import receipt,
        // holds the engine monitor, and swaps the returned state only after its final checks.
        // Every publication below belongs to a discarded clone until the entire import succeeds.
        internal bool TryImport(string owner, Row[] additions, CargoLot[] cargoAdditions, byte[] canonicalIntent,
            Func<int, NativeStorageProbe> probe, out NativeStorageState candidate, out string reason)
        {
            candidate = null; reason = string.Empty;
            if (activePlan != null || activeCargoPlan != null)
            { reason = "A storage/cargo publication is already prepared."; return false; }
            if (additions == null || cargoAdditions == null || additions.Length > MaximumRows ||
                cargoAdditions.Length > MaximumCargoRecords)
            { reason = "Bounded typed storage import arrays are required."; return false; }
            if (string.IsNullOrEmpty(owner) || !CargoOwnerKnown(owner))
            { reason = "The import owner has no registered storage goods."; return false; }
            if (canonicalIntent == null || canonicalIntent.Length != 32 || additions.Length != 0 && probe == null)
            { reason = "Imports require a 32-byte canonical intent and physical additions require a native probe."; return false; }

            try
            {
                var rows = (Row[])additions.Clone();
                var lots = (CargoLot[])cargoAdditions.Clone();
                Array.Sort(rows, RowOrder.Instance);
                Array.Sort(lots, ImportCargoOrder.Instance);
                var totals = new SortedDictionary<GoodPlayer, long>(GoodPlayerOrder.Instance);
                var stores = new Dictionary<int, ImportStore>();
                Published before = published;
                int nextCount = before.Rows.Length;
                for (int i = 0; i < rows.Length; i++)
                {
                    Row row = rows[i];
                    if (!ImportGood(owner, row.Good, row.Kind) || row.Slot <= 0 || row.Slot >= BuildingSlots ||
                        row.GlobalId == 0 || row.Player < 1 || row.Player > 8 || row.Quantity <= 0)
                    { reason = "An import row has an invalid owner, active good, pool, identity or quantity."; return false; }
                    if (i != 0 && RowOrder.Instance.Compare(rows[i - 1], row) == 0)
                    { reason = "The import repeats an exact good/storage row."; return false; }
                    if (!stores.TryGetValue(row.Slot, out ImportStore store))
                    {
                        NativeStorageProbe current = probe(row.Slot);
                        BuildingIndex occupied = before.Index[row.Slot];
                        if (!current.IsLive || current.Slot != row.Slot || occupied.Quantity != 0 && !occupied.Matches(current))
                        { reason = "An import destination is unavailable or its existing identity changed."; return false; }
                        store = new ImportStore(current, occupied.Quantity); stores.Add(row.Slot, store);
                    }
                    if (store.Probe.GlobalId != row.GlobalId || store.Probe.PlayerId != row.Player || store.Probe.Kind != row.Kind)
                    { reason = "An import row does not name its exact live storage instance, player and pool."; return false; }
                    store.Added += row.Quantity;
                    if ((long)store.Probe.NativeUsed + store.Before + store.Added > store.Probe.Capacity)
                    { reason = "Native stock and all owners' virtual allocations exceed import destination capacity."; return false; }
                    var account = new GoodPlayer(row.Good, row.Player);
                    totals.TryGetValue(account, out long prior);
                    long total = prior + row.Quantity;
                    if (total > int.MaxValue || !totals.ContainsKey(account) && totals.Count == 32)
                    { reason = "The import exceeds its 32 aggregated good/player accounts or quantity bound."; return false; }
                    totals[account] = total;
                    if (Array.BinarySearch(before.Rows, row, RowOrder.Instance) < 0) nextCount++;
                }
                if (nextCount > MaximumRows)
                { reason = "The import exceeds the world physical allocation bound."; return false; }
                for (int i = 0; i < lots.Length; i++)
                {
                    CargoLot lot = lots[i];
                    if (!ImportGood(owner, lot.Good, lot.Kind) || lot.Sequence <= 0 || lot.Player < 1 || lot.Player > 8 ||
                        lot.Issued <= 0 || lot.Remaining != lot.Issued || !ImportUntouched(lot.Receipt))
                    { reason = "Imported cargo must be an untouched positive lot for this owner's active registered good."; return false; }
                    if (i != 0 && lots[i - 1].Sequence == lot.Sequence ||
                        cargo.Lots.ContainsKey(new CargoKey(owner, lot.Sequence)))
                    { reason = "The import repeats or reuses a cargo creation key."; return false; }
                }

                // Restore only our own coherent checkpoint. This preserves dormant registrations,
                // other owners, current rows, retained operations and both cargo replay histories.
                Capture(out byte[] goods, out byte[] storage);
                if (!TryRestore(GetRegistrations(), goods, storage, out NativeStorageState next, out reason)) return false;
                if (rows.Length != 0)
                {
                    var merged = new Row[nextCount];
                    int current = 0, added = 0, output = 0;
                    while (current < before.Rows.Length || added < rows.Length)
                    {
                        int order = current == before.Rows.Length ? 1 : added == rows.Length ? -1 :
                            RowOrder.Instance.Compare(before.Rows[current], rows[added]);
                        if (order < 0) merged[output++] = before.Rows[current++];
                        else
                        {
                            Row row = rows[added++];
                            if (order == 0) row = row.WithQuantity(checked(row.Quantity + before.Rows[current++].Quantity));
                            merged[output++] = row;
                        }
                    }
                    Published publication = BuildPublished(merged);
                    var changes = new GoodsChange[totals.Count];
                    int index = 0;
                    foreach (var total in totals)
                        changes[index++] = new GoodsChange(total.Key.Good, total.Key.Player, (int)total.Value);
                    CustomGoodsClient client = next.ledger.ForOwner(owner);
                    long sequence = client.LastAcceptedSequence;
                    if (sequence == long.MaxValue)
                    { reason = "The import owner's storage command sequence is exhausted."; return false; }
                    GoodsTransactionStatus status = client.PrepareNative(sequence + 1, changes, canonicalIntent,
                        out CustomGoodsInventory.PreparedGoodsCommit prepared);
                    using (prepared)
                    {
                        if (status != GoodsTransactionStatus.Applied || prepared == null || !prepared.IsPrepared)
                        { reason = "The private ledger rejected the import: " + status; return false; }
                        prepared.Publish(); next.published = publication;
                    }
                }
                // Creation order is canonical and uses the existing eviction/sequence rules.
                // A late refusal discards even already-published physical changes on this clone.
                foreach (CargoLot lot in lots)
                {
                    GoodsTransactionStatus status = next.PrepareCreateCargo(lot.Good, lot.Sequence, lot.Player, lot.Issued,
                        out PreparedCargoChange prepared, out _, out reason);
                    using (prepared)
                    {
                        if (status != GoodsTransactionStatus.Applied || prepared == null || !prepared.IsPrepared)
                        { reason = "Cargo import creation refused: " + status + ". " + reason; return false; }
                        prepared.Publish();
                    }
                }
                next.ValidateLedgerRows();
                // Import must not publish authority which its existing paired checkpoint cannot
                // retain. The source can already be close to the bounded ledger/store formats.
                next.Capture(out byte[] importedGoods, out byte[] importedStorage);
                if (importedGoods.Length > MaximumSnapshotBytes || importedStorage.Length > MaximumSnapshotBytes)
                { reason = "The imported authority exceeds the bounded storage checkpoint."; return false; }
                candidate = next; reason = string.Empty; return true;
            }
            catch (Exception error)
            { reason = "Native storage import preparation failed: " + error.Message; return false; }
        }

        private bool ImportGood(string owner, CustomGoodId good, StoragePool kind) =>
            StringComparer.Ordinal.Equals(owner, good.OwnerGuid) && ValidGood(good) &&
            catalog.TryGetValue(good, out CatalogEntry entry) && entry.Active && entry.Kind == kind;

        private static bool ImportUntouched(CargoReceipt receipt)
        {
            NativeStorageProbe destination = receipt.Destination;
            return receipt.Kind == CargoReceiptKind.None && receipt.Before == 0 && receipt.Quantity == 0 &&
                !destination.Alive && destination.Slot == 0 && destination.GlobalId == 0 && destination.PlayerId == 0 &&
                (int)destination.Kind == 0 && destination.NativeUsed == 0 && destination.Capacity == 0;
        }

        private sealed class ImportStore
        {
            internal readonly NativeStorageProbe Probe;
            internal readonly int Before;
            internal long Added;
            internal ImportStore(NativeStorageProbe probe, int before) { Probe = probe; Before = before; }
        }

        private sealed class ImportCargoOrder : IComparer<CargoLot>
        {
            internal static readonly ImportCargoOrder Instance = new ImportCargoOrder();
            public int Compare(CargoLot left, CargoLot right) => left.Sequence.CompareTo(right.Sequence);
        }
    }
}
