using System;
using System.Collections.Generic;

namespace APIShared.Goods
{
    /// <summary>Caller-owned, bounded virtual inventory and idempotent transaction journal.
    /// Create one per authoritative mission and share that instance with participating consumers.
    /// No static registry, native IDs, hooks, wallet, storage, save registration or networking.</summary>
    /// <remarks>All operations are serialized, run on the caller's thread and invoke no callbacks.
    /// Successful writes allocate and validate replacement state before one publication; rejected
    /// operations change nothing. Read results can be retained. The caller owns mission lifetime,
    /// native-state locking/verification and peer authority. Thread safety alone is not multiplayer
    /// determinism. This service does not make a native payment atomic with a virtual change.</remarks>
    public sealed partial class CustomGoodsInventory
    {
        private readonly object sync = new object();
        private readonly int maximumGoods, maximumOperations;
        private readonly SortedDictionary<CustomGoodId, int> catalog =
            new SortedDictionary<CustomGoodId, int>(GoodComparer.Instance);
        private State state = new State();
        private object epoch = new object();

        /// <summary>Creates an empty inventory. Catalog bound: 1–128 goods, journal bound:
        /// 1–1024 retained operations (active holds and recent terminal receipts). Terminal receipts
        /// may expire; persisted owner high-watermarks reject their retries. Invalid bounds throw
        /// ArgumentOutOfRangeException. Bounds ensure a
        /// canonical snapshot fits one VersionedSaveData entry (at most 1 MiB).</summary>
        public CustomGoodsInventory(int maximumGoods = 128, int maximumOperations = 1024)
        {
            if (maximumGoods < 1 || maximumGoods > 128) throw new ArgumentOutOfRangeException(nameof(maximumGoods));
            if (maximumOperations < 1 || maximumOperations > 1024) throw new ArgumentOutOfRangeException(nameof(maximumOperations));
            this.maximumGoods = maximumGoods; this.maximumOperations = maximumOperations;
        }

        /// <summary>Binds an owner without registering goods or mutating stock. Invalid owner IDs
        /// throw ArgumentException; use the same stable GUID and exact case on every peer/load.</summary>
        public CustomGoodsClient ForOwner(string ownerGuid)
        { GoodsIds.Require(ownerGuid, 128, nameof(ownerGuid)); return new CustomGoodsClient(this, ownerGuid); }

        internal long LastSequence(string owner)
        { lock (sync) return state.Watermarks.TryGetValue(owner, out long sequence) ? sequence : 0; }

        /// <summary>Clears stock, holds and receipts, retaining registered goods/capacities.
        /// Invalidates every prior reservation handle. The caller invokes this at mission boundaries;
        /// a new inventory is preferable when changing the catalog. Not an automatic unload hook.</summary>
        public void Reset()
        { lock (sync) { var next = new State(); var nextEpoch = new object(); state = next; epoch = nextEpoch; } }

        internal bool Register(CustomGoodId key, int capacity, out CustomGoodId good, out string reason)
        {
            good = default;
            lock (sync)
            {
                if (capacity < 0) { reason = "Capacity must be nonnegative."; return false; }
                if (catalog.TryGetValue(key, out int prior))
                {
                    if (prior != capacity) { reason = "This good already has another capacity."; return false; }
                }
                else
                {
                    if (catalog.Count >= maximumGoods) { reason = "The good catalog is full."; return false; }
                    catalog.Add(key, capacity);
                }
                good = key; reason = string.Empty; return true;
            }
        }

        internal bool Read(CustomGoodId good, int player, out GoodsBalance balance)
        {
            lock (sync)
            {
                balance = default;
                if (player < 1 || player > 8 || !catalog.TryGetValue(good, out int capacity)) return false;
                state.Stock.TryGetValue(new Account(good, player), out Stock stock);
                balance = new GoodsBalance(stock.Quantity, stock.Reserved, capacity); return true;
            }
        }

        // Journal entries and stock records are immutable after publication. Normalize first, check
        // the existing receipt before current affordability, then build a replacement. No consumer
        // callback/native payment may run inside this boundary: it cannot be rolled back by us.
        internal GoodsTransactionStatus Submit(string owner, long operation, IReadOnlyList<GoodsChange> changes,
            bool reserve, out GoodsReservation handle)
        {
            handle = null;
            if (operation <= 0) return GoodsTransactionStatus.InvalidRequest;
            // IReadOnlyList can be a consumer implementation. Never invoke its Count/indexer
            // under our monitor, and never trust a changing Count as a loop bound.
            if (changes == null) return GoodsTransactionStatus.InvalidRequest;
            int count = changes.Count;
            if (count < 1 || count > 32) return GoodsTransactionStatus.InvalidRequest;
            var owned = new GoodsChange[count];
            for (int i = 0; i < count; i++) owned[i] = changes[i];
            lock (sync)
            {
                GoodsTransactionStatus result = Normalize(owner, owned, reserve, out GoodsChange[] normalized);
                if (result != GoodsTransactionStatus.Applied) return result;
                var key = new Operation(owner, operation);
                if (state.Journal.TryGetValue(key, out Receipt prior))
                {
                    if (prior.Reservation != reserve || !Same(prior.Changes, normalized)) return GoodsTransactionStatus.Conflict;
                    if (reserve) handle = new GoodsReservation(this, epoch, owner, operation);
                    return Retry(prior.Status);
                }
                if (state.Watermarks.TryGetValue(owner, out long watermark) && operation <= watermark)
                    return GoodsTransactionStatus.ExpiredOperation;
                var next = new State(state);
                if (next.Journal.Count >= maximumOperations)
                {
                    // Evict only a terminal receipt. Its owner's persisted watermark prevents
                    // any expired retry executing again. Ordinal choice is deterministic.
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
                    next.Stock.TryGetValue(account, out Stock stock);
                    long quantity = (long)stock.Quantity + (reserve ? 0 : change.Delta);
                    long reserved = (long)stock.Reserved - (reserve ? change.Delta : 0);
                    if (quantity < reserved) return GoodsTransactionStatus.InsufficientStock;
                    if (quantity > catalog[change.Good]) return GoodsTransactionStatus.CapacityExceeded;
                    next.Stock[account] = new Stock((int)quantity, (int)reserved);
                }
                result = reserve ? GoodsTransactionStatus.Reserved : GoodsTransactionStatus.Applied;
                next.Journal.Add(key, new Receipt(reserve, result, normalized));
                next.Watermarks[owner] = operation;
                if (reserve) handle = new GoodsReservation(this, epoch, owner, operation);
                state = next; return result;
            }
        }

        internal GoodsTransactionStatus Finish(GoodsReservation handle, bool commit)
        {
            lock (sync)
            {
                if (!ReferenceEquals(epoch, handle.Epoch)) return GoodsTransactionStatus.StaleReservation;
                var key = new Operation(handle.Owner, handle.Operation);
                if (!state.Journal.TryGetValue(key, out Receipt prior) || !prior.Reservation)
                    return GoodsTransactionStatus.ExpiredOperation;
                if (prior.Status == GoodsTransactionStatus.Committed)
                    return commit ? GoodsTransactionStatus.AlreadyCommitted : GoodsTransactionStatus.Conflict;
                if (prior.Status == GoodsTransactionStatus.Released)
                    return commit ? GoodsTransactionStatus.Conflict : GoodsTransactionStatus.AlreadyReleased;
                var next = new State(state);
                foreach (GoodsChange change in prior.Changes)
                {
                    var account = new Account(change.Good, change.PlayerId);
                    Stock stock = next.Stock[account];
                    next.Stock[account] = new Stock(stock.Quantity + (commit ? change.Delta : 0), stock.Reserved + change.Delta);
                }
                var result = commit ? GoodsTransactionStatus.Committed : GoodsTransactionStatus.Released;
                next.Journal[key] = new Receipt(true, result, prior.Changes);
                state = next; return result;
            }
        }

        private GoodsTransactionStatus Normalize(string owner, GoodsChange[] changes, bool reserve,
            out GoodsChange[] normalized)
        {
            normalized = null;
            var sums = new SortedDictionary<Account, long>(AccountComparer.Instance);
            // At most 32 Int32 deltas: Int64 accumulation cannot overflow, including permutations
            // that exceed Int32 temporarily and later cancel. Copy every accepted value.
            for (int i = 0; i < changes.Length; i++)
            {
                GoodsChange change = changes[i];
                if (change.Good.OwnerGuid != owner || change.PlayerId < 1 || change.PlayerId > 8 ||
                    change.Delta == 0 || (reserve && change.Delta > 0)) return GoodsTransactionStatus.InvalidRequest;
                if (!catalog.ContainsKey(change.Good)) return GoodsTransactionStatus.UnknownGood;
                var account = new Account(change.Good, change.PlayerId);
                sums.TryGetValue(account, out long sum); sums[account] = sum + change.Delta;
            }
            var lines = new List<GoodsChange>(sums.Count);
            foreach (var entry in sums)
            {
                if (entry.Value < int.MinValue || entry.Value > int.MaxValue) return GoodsTransactionStatus.InvalidRequest;
                if (entry.Value != 0) lines.Add(new GoodsChange(entry.Key.Good, entry.Key.Player, (int)entry.Value));
            }
            normalized = lines.ToArray(); return GoodsTransactionStatus.Applied;
        }

        private static bool Same(GoodsChange[] left, GoodsChange[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (!left[i].Good.Equals(right[i].Good) || left[i].PlayerId != right[i].PlayerId || left[i].Delta != right[i].Delta) return false;
            return true;
        }
        private static GoodsTransactionStatus Retry(GoodsTransactionStatus status)
        {
            switch (status)
            {
                case GoodsTransactionStatus.Applied: return GoodsTransactionStatus.AlreadyApplied;
                case GoodsTransactionStatus.Reserved: return GoodsTransactionStatus.AlreadyReserved;
                case GoodsTransactionStatus.Committed: return GoodsTransactionStatus.AlreadyCommitted;
                default: return GoodsTransactionStatus.AlreadyReleased;
            }
        }
        private readonly struct Account : IEquatable<Account>
        {
            internal readonly CustomGoodId Good; internal readonly int Player;
            internal Account(CustomGoodId good, int player) { Good = good; Player = player; }
            public bool Equals(Account other) => Good.Equals(other.Good) && Player == other.Player;
            public override bool Equals(object obj) => obj is Account other && Equals(other);
            public override int GetHashCode() => unchecked(Good.GetHashCode() * 397 ^ Player);
        }
        private readonly struct Operation : IEquatable<Operation>
        {
            internal readonly string Owner;
            internal readonly long Id;
            internal Operation(string owner, long id) { Owner = owner; Id = id; }
            public bool Equals(Operation other) => Owner == other.Owner && Id == other.Id;
            public override bool Equals(object obj) => obj is Operation other && Equals(other);
            public override int GetHashCode() => unchecked(Owner.GetHashCode() * 397 ^ Id.GetHashCode());
        }
        private readonly struct Stock
        {
            internal readonly int Quantity, Reserved;
            internal Stock(int quantity, int reserved) { Quantity = quantity; Reserved = reserved; }
        }
        private sealed class Receipt
        {
            internal readonly bool Reservation;
            internal readonly GoodsTransactionStatus Status;
            internal readonly GoodsChange[] Changes;
            internal Receipt(bool reservation, GoodsTransactionStatus status, GoodsChange[] changes)
            { Reservation = reservation; Status = status; Changes = changes; }
        }
        private sealed class State
        {
            internal readonly Dictionary<Account, Stock> Stock;
            internal readonly Dictionary<Operation, Receipt> Journal;
            internal readonly Dictionary<string, long> Watermarks;
            internal State() { Stock = new Dictionary<Account, Stock>(); Journal = new Dictionary<Operation, Receipt>(); Watermarks = new Dictionary<string, long>(StringComparer.Ordinal); }
            internal State(State source) { Stock = new Dictionary<Account, Stock>(source.Stock); Journal = new Dictionary<Operation, Receipt>(source.Journal); Watermarks = new Dictionary<string, long>(source.Watermarks, StringComparer.Ordinal); }
        }
        private sealed class GoodComparer : IComparer<CustomGoodId>
        {
            internal static readonly GoodComparer Instance = new GoodComparer();
            public int Compare(CustomGoodId x, CustomGoodId y)
            { int order = StringComparer.Ordinal.Compare(x.OwnerGuid, y.OwnerGuid); return order != 0 ? order : StringComparer.Ordinal.Compare(x.LocalId, y.LocalId); }
        }
        private sealed class AccountComparer : IComparer<Account>
        {
            internal static readonly AccountComparer Instance = new AccountComparer();
            public int Compare(Account x, Account y)
            { int order = GoodComparer.Instance.Compare(x.Good, y.Good); return order != 0 ? order : x.Player.CompareTo(y.Player); }
        }
    }
}
