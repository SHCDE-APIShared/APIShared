using System;
using System.Collections.Generic;
using APIShared.Goods;

namespace APIShared.Economy
{
    internal sealed partial class NativeStorageState
    {
        // A retained decision precedes maintenance/live reads. A new request may allocate a
        // discarded candidate here; its affordability/limit result is deferred until reconciliation.
        internal GoodsTransactionStatus ProbeBatchReceipt(NativeStorageBatch batch, long sequence,
            out bool final, out string reason)
        {
            final = true;
            GoodsTransactionStatus status = ValidateBatch(batch, sequence, out reason);
            if (status != GoodsTransactionStatus.Applied) return status;
            CustomGoodsInventory.PreparedGoodsCommit lease = null;
            try { status = batch.PrepareLedger(ledger, sequence, out lease); }
            finally { lease?.Dispose(); }
            final = status == GoodsTransactionStatus.AlreadyApplied || status == GoodsTransactionStatus.Conflict ||
                status == GoodsTransactionStatus.ExpiredOperation || status == GoodsTransactionStatus.InvalidRequest || status == GoodsTransactionStatus.UnknownGood;
            if (final && status != GoodsTransactionStatus.AlreadyApplied)
                reason = "The storage batch receipt rejected this request: " + status;
            return status;
        }

        internal GoodsTransactionStatus PrepareBatchExchange(NativeStorageBatch batch, long sequence,
            NativeStorageProbe[] currentByRow, out PreparedExchange prepared, out string reason)
        {
            prepared = null;
            GoodsTransactionStatus status = ValidateBatch(batch, sequence, out reason);
            if (status != GoodsTransactionStatus.Applied) return status;
            if (currentByRow == null || currentByRow.Length != batch.Count)
                return Reject(GoodsTransactionStatus.InvalidRequest, "Current storage probes do not match the frozen batch.", out reason);
            status = batch.PrepareLedger(ledger, sequence, out CustomGoodsInventory.PreparedGoodsCommit lease);
            if (status != GoodsTransactionStatus.Applied)
            {
                lease?.Dispose();
                if (status != GoodsTransactionStatus.AlreadyApplied) reason = "The private ledger rejected this batch: " + status;
                return status;
            }
            try
            {
                if (lease == null || !lease.IsPrepared)
                    return Reject(GoodsTransactionStatus.InvalidRequest, "The ledger did not prepare this batch.", out reason);
                Published before = published;
                var updated = new Row[batch.Count];
                var stores = new Dictionary<int, BatchStore>();
                int nextCount = before.Rows.Length;
                for (int i = 0; i < batch.Count; i++)
                {
                    NativeStorageBatchRow change = batch[i]; NativeStorageProbe current = currentByRow[i];
                    if (!current.IsLive || !NativeStorageBatch.SameIdentity(change.Expected, current))
                        return Reject(GoodsTransactionStatus.InvalidRequest, "A batch destination is no longer the exact live store.", out reason);
                    BuildingIndex occupied = before.Index[current.Slot];
                    if (occupied.Quantity != 0 && !occupied.Matches(current))
                        return Reject(GoodsTransactionStatus.InvalidRequest, "A reused batch store requires reconciliation.", out reason);
                    if (!stores.TryGetValue(current.Slot, out BatchStore store))
                    { store = new BatchStore(current, occupied.Quantity); stores.Add(current.Slot, store); }
                    else if (!NativeStorageBatch.SameIdentity(store.Probe, current) ||
                        store.Probe.NativeUsed != current.NativeUsed || store.Probe.Capacity != current.Capacity)
                        return Reject(GoodsTransactionStatus.InvalidRequest, "One batch store has inconsistent current probes.", out reason);
                    store.Delta += change.QuantityDelta;
                    var key = new Row(change.Good, current.Slot, current.GlobalId, current.PlayerId, current.Kind, 0);
                    int position = Array.BinarySearch(before.Rows, key, RowOrder.Instance);
                    long quantity = (position >= 0 ? before.Rows[position].Quantity : 0L) + change.QuantityDelta;
                    if (quantity < 0)
                        return Reject(GoodsTransactionStatus.InsufficientStock, "A batch row lacks the requested good in this exact store.", out reason);
                    if (quantity > int.MaxValue)
                        return Reject(GoodsTransactionStatus.CapacityExceeded, "A batch row exceeds its quantity limit.", out reason);
                    updated[i] = key.WithQuantity((int)quantity);
                    if (position < 0 && quantity != 0) nextCount++;
                    else if (position >= 0 && quantity == 0) nextCount--;
                }
                foreach (BatchStore store in stores.Values)
                {
                    long occupied = store.Before + store.Delta;
                    if (occupied < 0 || occupied > PoolCapacity(store.Probe.Kind) ||
                        occupied > store.Before && (long)store.Probe.NativeUsed + occupied > store.Probe.Capacity)
                        return Reject(GoodsTransactionStatus.CapacityExceeded, "The batch exceeds joint native/virtual pooled capacity.", out reason);
                }
                if (nextCount > MaximumRows)
                    return Reject(GoodsTransactionStatus.LimitReached, "The batch exceeds the physical allocation table bound.", out reason);
                var nextRows = new Row[nextCount];
                int source = 0, changeIndex = 0, output = 0;
                while (source < before.Rows.Length || changeIndex < updated.Length)
                {
                    int order = source == before.Rows.Length ? 1 : changeIndex == updated.Length ? -1 :
                        RowOrder.Instance.Compare(before.Rows[source], updated[changeIndex]);
                    if (order < 0) nextRows[output++] = before.Rows[source++];
                    else
                    {
                        Row change = updated[changeIndex++];
                        if (change.Quantity != 0) nextRows[output++] = change;
                        if (order == 0) source++;
                    }
                }
                Published next = BuildPublished(nextRows);
                prepared = new PreparedExchange(this, lease, next); activePlan = prepared; lease = null;
                return GoodsTransactionStatus.Applied;
            }
            finally { lease?.Dispose(); }
        }

        private GoodsTransactionStatus ValidateBatch(NativeStorageBatch batch, long sequence, out string reason)
        {
            if (batch == null || sequence <= 0)
                return Reject(GoodsTransactionStatus.InvalidRequest, "A frozen storage batch and positive owner sequence are required.", out reason);
            if (activePlan != null || activeCargoPlan != null)
                return Reject(GoodsTransactionStatus.InvalidRequest, "A storage/cargo publication is already prepared.", out reason);
            for (int i = 0; i < batch.Count; i++)
            {
                NativeStorageBatchRow row = batch[i];
                if (!catalog.TryGetValue(row.Good, out CatalogEntry entry) || !entry.Active)
                    return Reject(GoodsTransactionStatus.UnknownGood, "A batch good is not currently registered.", out reason);
                if (entry.Kind != row.Expected.Kind)
                    return Reject(GoodsTransactionStatus.InvalidRequest, "A batch store has the wrong registered pool.", out reason);
            }
            reason = string.Empty; return GoodsTransactionStatus.Applied;
        }

        private sealed class BatchStore
        {
            internal readonly NativeStorageProbe Probe;
            internal readonly int Before;
            internal long Delta;
            internal BatchStore(NativeStorageProbe probe, int before) { Probe = probe; Before = before; }
        }
    }
}
