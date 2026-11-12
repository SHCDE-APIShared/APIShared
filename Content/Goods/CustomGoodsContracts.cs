using System;

namespace APIShared.Goods
{
    /// <summary>Stable virtual-good identity: exact, ordinal owner GUID and owner-local ID.
    /// This is not a native eGoods value. The default struct is invalid.</summary>
    public readonly struct CustomGoodId : IEquatable<CustomGoodId>
    {
        /// <summary>Creates an identity. Both IDs use printable ASCII without spaces; the owner
        /// allows 1–128 characters, the local ID 1–64. Invalid IDs throw ArgumentException.</summary>
        public CustomGoodId(string ownerGuid, string localId)
        {
            GoodsIds.Require(ownerGuid, 128, nameof(ownerGuid));
            GoodsIds.Require(localId, 64, nameof(localId));
            OwnerGuid = ownerGuid; LocalId = localId;
        }
        /// <summary>Exact BepInEx owner GUID, not a display name.</summary>
        public string OwnerGuid { get; }
        /// <summary>Owner-local stable good ID; localization is separate.</summary>
        public string LocalId { get; }
        /// <summary>Ordinal equality of both parts, including default values.</summary>
        public bool Equals(CustomGoodId other) => OwnerGuid == other.OwnerGuid && LocalId == other.LocalId;
        /// <summary>Ordinal equality of both parts.</summary>
        public override bool Equals(object obj) => obj is CustomGoodId other && Equals(other);
        /// <summary>Hash for in-process dictionaries; never persist this value.</summary>
        public override int GetHashCode() => unchecked((OwnerGuid?.GetHashCode() ?? 0) * 397 ^ (LocalId?.GetHashCode() ?? 0));
    }

    /// <summary>One signed virtual-stock change. Validation occurs when submitted; player IDs are
    /// 1–8, deltas must be nonzero. Duplicate account lines are summed before validation.</summary>
    public readonly struct GoodsChange
    {
        /// <summary>Creates a proposed change; it has no side effects.</summary>
        public GoodsChange(CustomGoodId good, int playerId, int delta)
        { Good = good; PlayerId = playerId; Delta = delta; }
        /// <summary>Registered owner-qualified good.</summary>
        public CustomGoodId Good { get; }
        /// <summary>One-based player ID, 1–8.</summary>
        public int PlayerId { get; }
        /// <summary>Positive deposit or negative withdrawal.</summary>
        public int Delta { get; }
    }

    /// <summary>Immutable momentary balance. A reservation holds stock but does not remove it.</summary>
    public readonly struct GoodsBalance
    {
        internal GoodsBalance(int quantity, int reserved, int capacity)
        { Quantity = quantity; Reserved = reserved; Capacity = capacity; }
        /// <summary>Total stock, including held stock.</summary>
        public int Quantity { get; }
        /// <summary>Stock held by active reservations.</summary>
        public int Reserved { get; }
        /// <summary>Quantity available for withdrawal or a new reservation.</summary>
        public int Available => Quantity - Reserved;
        /// <summary>Virtual per-good, per-player limit. It does not include native storage.</summary>
        public int Capacity { get; }
    }

    /// <summary>Transaction outcome; retry successes never change stock a second time.</summary>
    public enum GoodsTransactionStatus
    {
        /// <summary>A new atomic change was published.</summary>
        Applied = 0,
        /// <summary>This operation sequence already applied the same normalized change.</summary>
        AlreadyApplied = 1,
        /// <summary>A new stock hold was published.</summary>
        Reserved = 2,
        /// <summary>The same reservation is still active.</summary>
        AlreadyReserved = 3,
        /// <summary>A hold was consumed exactly once.</summary>
        Committed = 4,
        /// <summary>The reservation was already consumed; no additional debit.</summary>
        AlreadyCommitted = 5,
        /// <summary>A hold was released without changing total stock.</summary>
        Released = 6,
        /// <summary>The reservation was already released.</summary>
        AlreadyReleased = 7,
        /// <summary>Invalid IDs, player, count, delta, foreign owner or reservation credit.</summary>
        InvalidRequest = 8,
        /// <summary>A referenced good was not registered.</summary>
        UnknownGood = 9,
        /// <summary>A retained operation sequence was reused with another normalized payload or operation kind,
        /// or release followed commit / commit followed release.</summary>
        Conflict = 10,
        /// <summary>A debit would consume unavailable or held stock.</summary>
        InsufficientStock = 11,
        /// <summary>A resulting balance would exceed its virtual capacity.</summary>
        CapacityExceeded = 12,
        /// <summary>The bounded journal contains only active holds; none may be evicted.</summary>
        LimitReached = 13,
        /// <summary>This handle belongs to an earlier successful restore or reset.</summary>
        StaleReservation = 14,
        /// <summary>The sequence is at or below the owner's accepted high-watermark and its
        /// terminal receipt has expired, or a later sequence permanently closed this gap.
        /// It is never executed again, and no prior success is asserted.</summary>
        ExpiredOperation = 15,
    }

    /// <summary>Opaque, inventory-instance-bound reservation handle. Thread-safe; no native work.
    /// Retain it until completion. A successful restore/reset invalidates old handles; replay the
    /// original Reserve sequence and payload to obtain a handle to a restored reservation.</summary>
    public sealed class GoodsReservation
    {
        private readonly CustomGoodsInventory inventory;
        internal readonly object Epoch;
        internal readonly string Owner;
        internal readonly long Operation;
        internal GoodsReservation(CustomGoodsInventory inventory, object epoch, string owner, long operation)
        { this.inventory = inventory; Epoch = epoch; Owner = owner; Operation = operation; }
        /// <summary>Consumes this hold once. Duplicate commits return AlreadyCommitted;
        /// commit after release returns Conflict. An expired terminal receipt returns ExpiredOperation.
        /// Never pays gold, recruits or calls a callback.</summary>
        public GoodsTransactionStatus Commit() => inventory.Finish(this, true);
        /// <summary>Releases this hold once. Duplicate releases return AlreadyReleased;
        /// release after commit returns Conflict and never refunds consumed stock. An expired
        /// terminal receipt returns ExpiredOperation.</summary>
        public GoodsTransactionStatus Release() => inventory.Finish(this, false);
    }

    /// <summary>Cooperative owner binding for one inventory. This is namespace isolation, not
    /// a security boundary: callers must use their own stable GUID. Thread-safe; methods serialize
    /// on the inventory and run on the caller's thread without callbacks or thread dispatch.</summary>
    public sealed partial class CustomGoodsClient
    {
        private readonly CustomGoodsInventory inventory;
        internal CustomGoodsClient(CustomGoodsInventory inventory, string owner)
        { this.inventory = inventory; OwnerGuid = owner; }
        /// <summary>Exact owner whose goods and operation sequences this client may mutate.</summary>
        public string OwnerGuid { get; }
        /// <summary>Largest accepted positive operation sequence for this owner, or zero. This
        /// read does not allocate/reserve the next sequence; callers serialize sequence allocation
        /// in their authoritative simulation. Accepting a sequence permanently closes lower gaps.</summary>
        public long LastAcceptedSequence => inventory.LastSequence(OwnerGuid);
        /// <summary>Registers an immutable per-player virtual capacity (0–Int32.MaxValue).
        /// Repeating the same registration succeeds; a changed capacity or full catalog fails.
        /// Invalid local IDs throw ArgumentException. Register before restore.</summary>
        public bool TryRegister(string localId, int capacity, out CustomGoodId good, out string reason) =>
            inventory.Register(new CustomGoodId(OwnerGuid, localId), capacity, out good, out reason);
        /// <summary>Reads this owner's good, including zero stock. Returns false for an unknown
        /// good or invalid player; invalid local IDs throw ArgumentException.</summary>
        public bool TryGetBalance(string localId, int playerId, out GoodsBalance balance) =>
            inventory.Read(new CustomGoodId(OwnerGuid, localId), playerId, out balance);
        /// <summary>Atomically applies 1–32 lines for this owner. Account duplicates are netted
        /// with order-independent arithmetic; opposing lines may cancel. Bounds apply to final
        /// balances, not intermediate lines. New positive owner-local sequences must exceed
        /// LastAcceptedSequence. Retained identical retries return AlreadyApplied; changed payloads
        /// conflict. Expired receipts/lower gaps return ExpiredOperation and never execute again.
        /// Failed requests do not advance the high-watermark. Capture persists replay protection.
        /// The caller owns simulation authorization and deterministic ordering across peers.
        /// Input collection getters run before the inventory lock; their exceptions propagate.
        /// Do not modify the input collection concurrently with submission.</summary>
        public GoodsTransactionStatus Apply(long operationSequence, System.Collections.Generic.IReadOnlyList<GoodsChange> changes) =>
            inventory.Submit(OwnerGuid, operationSequence, changes, false, out _);
        /// <summary>Atomically holds strictly negative lines for this owner. Identical retries
        /// return a handle to a retained reservation and its AlreadyReserved/Committed/Released
        /// outcome. The handle may only debit or release these exact holds. Failed requests hold
        /// nothing. Holds remain part of Quantity and continue to occupy capacity.</summary>
        public GoodsTransactionStatus Reserve(long operationSequence, System.Collections.Generic.IReadOnlyList<GoodsChange> changes,
            out GoodsReservation reservation) => inventory.Submit(OwnerGuid, operationSequence, changes, true, out reservation);
    }

    internal static class GoodsIds
    {
        internal static bool Valid(string id, int maximum)
        {
            if (id == null || id.Length < 1 || id.Length > maximum) return false;
            for (int i = 0; i < id.Length; i++) if (id[i] < '!' || id[i] > '~') return false;
            return true;
        }
        internal static void Require(string id, int maximum, string name)
        { if (!Valid(id, maximum)) throw new ArgumentException("Use a stable printable ASCII ID without spaces (1–" + maximum + " characters).", name); }
    }
}
