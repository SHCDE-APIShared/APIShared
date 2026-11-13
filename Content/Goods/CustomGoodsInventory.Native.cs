using System;
using System.Threading;
using System.Collections.Generic;

namespace APIShared.Goods
{
    public sealed partial class CustomGoodsClient
    {
        internal CustomGoodsInventory InventoryIdentity => inventory;
        // Trusted runtime seam only. Arrays are private runtime copies; normalization makes owned lines.
        // The caller already holds the engine monitor and must keep the lease on this thread.
        internal GoodsTransactionStatus PrepareNative(long sequence, GoodsChange[] changes, byte[] canonicalIntent,
            out CustomGoodsInventory.PreparedGoodsCommit lease) => inventory.PrepareNative(OwnerGuid, sequence, changes, canonicalIntent, out lease);
    }

    public sealed partial class CustomGoodsInventory
    {
        internal const int MaximumNativeIntentBytes = 256;
        private static readonly object publicationWarmSync = new object();
        private static bool publicationWarmed;

        // Scratch-only execution primes the real publication and Dispose methods on Mono too,
        // without depending on optional PrepareMethod behavior. Never call from PrepareNative.
        internal static PreparedGoodsCommit CreateWarmCommit()
        {
            var scratch = new CustomGoodsInventory(1, 1);
            var owner = scratch.ForOwner("APIShared.Internal.Warmup");
            if (!owner.TryRegister("scratch", 1, out var good, out _)) throw new InvalidOperationException("Could not prepare publication warmup.");
            var status = owner.PrepareNative(1, new[] { new GoodsChange(good, 1, 1) }, new byte[] { 255 }, out var prepared);
            if (status != GoodsTransactionStatus.Applied || prepared == null)
            { prepared?.Dispose(); throw new InvalidOperationException("Could not prepare publication warmup."); }
            return prepared;
        }
        internal static void WarmNativePublication()
        {
            lock (publicationWarmSync)
            {
                if (publicationWarmed) return;
                using (var scratch = CreateWarmCommit()) scratch.Publish();
                publicationWarmed = true;
            }
        }

        // Holds the inventory monitor until Dispose. Every allocation, journal eviction, bound and replay
        // decision precedes native writes. No consumer callback or public implementation runs here.
        internal GoodsTransactionStatus PrepareNative(string owner, long sequence, GoodsChange[] changes, byte[] canonicalIntent,
            out PreparedGoodsCommit lease)
        {
            lease = null;
            if (sequence <= 0 || changes == null || changes.Length < 1 || changes.Length > 32 ||
                canonicalIntent == null || canonicalIntent.Length < 1 || canonicalIntent.Length > MaximumNativeIntentBytes)
                return GoodsTransactionStatus.InvalidRequest;
            byte[] intent = (byte[])canonicalIntent.Clone();
            Monitor.Enter(sync);
            bool retained = false;
            try
            {
                if (nativeLeaseActive) return GoodsTransactionStatus.Conflict;
                GoodsTransactionStatus result = Normalize(owner, changes, false, out var normalized);
                if (result != GoodsTransactionStatus.Applied) return result;
                var operation = new Operation(owner, sequence);
                if (state.Journal.TryGetValue(operation, out var prior))
                {
                    if (prior.Reservation || prior.Intent == null || !Same(prior.Changes, normalized) || !SameIntent(prior.Intent, intent))
                        return GoodsTransactionStatus.Conflict;
                    lease = new PreparedGoodsCommit(this, GoodsTransactionStatus.AlreadyApplied, null, normalized, intent, epoch);
                    nativeLeaseActive = retained = true; return lease.Status;
                }
                if (state.Watermarks.TryGetValue(owner, out long watermark) && sequence <= watermark)
                    return GoodsTransactionStatus.ExpiredOperation;
                var next = new State(state) { NativeFormat = true };
                if (next.Journal.Count >= maximumOperations)
                {
                    Operation oldest = default; bool found = false;
                    foreach (var entry in next.Journal)
                        if (entry.Value.Status != GoodsTransactionStatus.Reserved &&
                            (!found || CompareOperation(entry.Key, oldest) < 0)) { oldest = entry.Key; found = true; }
                    if (!found) return GoodsTransactionStatus.LimitReached;
                    next.Journal.Remove(oldest);
                }
                foreach (GoodsChange change in normalized)
                {
                    var account = new Account(change.Good, change.PlayerId);
                    next.Stock.TryGetValue(account, out var stock);
                    long quantity = (long)stock.Quantity + change.Delta;
                    if (quantity < stock.Reserved) return GoodsTransactionStatus.InsufficientStock;
                    if (quantity > catalog[change.Good]) return GoodsTransactionStatus.CapacityExceeded;
                    next.Stock[account] = new Stock((int)quantity, stock.Reserved);
                }
                next.Journal.Add(operation, new Receipt(false, GoodsTransactionStatus.Applied, normalized, intent));
                next.Watermarks[owner] = sequence;
                lease = new PreparedGoodsCommit(this, GoodsTransactionStatus.Applied, next, normalized, intent, epoch);
                nativeLeaseActive = retained = true; return lease.Status;
            }
            finally { if (!retained) Monitor.Exit(sync); }
        }

        private static bool SameIntent(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // Internal, thread-affine and single-use; not an arbitrary callback/transaction extension API.
        // Publish is deliberately a preallocated reference publication, with no calls or allocations.
        internal sealed class PreparedGoodsCommit : IDisposable
        {
            private CustomGoodsInventory inventory;
            private readonly State next;
            internal GoodsTransactionStatus Status { get; }
            private bool published;
            private readonly int threadId;
            internal bool IsPublished => published;
            internal bool IsPrepared => inventory != null && next != null && !published &&
                Thread.CurrentThread.ManagedThreadId == threadId && Monitor.IsEntered(inventory.sync);
            internal object Epoch { get; }
            internal IReadOnlyList<GoodsChange> OwnedChanges { get; }
            internal IReadOnlyList<byte> Intent { get; }
            internal PreparedGoodsCommit(CustomGoodsInventory inventory, GoodsTransactionStatus status, object prepared,
                GoodsChange[] changes, byte[] intent, object epoch)
            {
                this.inventory = inventory; Status = status; next = (State)prepared; threadId = Thread.CurrentThread.ManagedThreadId;
                OwnedChanges = Array.AsReadOnly(changes); Intent = Array.AsReadOnly(intent); Epoch = epoch;
            }
            internal void Publish()
            {
                if (published || next == null || inventory == null) return;
                inventory.state = next;
                published = true;
            }
            public void Dispose()
            {
                var owner = inventory;
                if (owner == null) return;
                if (Thread.CurrentThread.ManagedThreadId != threadId) throw new SynchronizationLockException("Native commit leases are thread-affine.");
                owner.nativeLeaseActive = false;
                inventory = null;
                Monitor.Exit(owner.sync);
            }
        }
    }
}
