using APIShared.Goods;

namespace APIShared.Economy
{
    /// <summary>Detached checkpoint of framework-owned, undelivered production cargo. Persist
    /// CreationSequence in the consumer's actor state; reacquire this snapshot after loading.
    /// Copies do not own quantity and cannot authorize another delivery after their checkpoint
    /// was consumed. All quantity belongs to the mission's shared storage owner.</summary>
    public readonly struct NativeCargoSnapshot
    {
        internal readonly object World;
        internal readonly NativeStorageState.CargoLot Value;
        internal NativeCargoSnapshot(object world, NativeStorageState.CargoLot value) { World = world; Value = value; }
        /// <summary>Registering plugin GUID; ordinal namespace ownership, not sender authentication.</summary>
        public string OwnerGuid => Value.Good.OwnerGuid;
        /// <summary>Stable owner-local output good.</summary>
        public string GoodId => Value.Good.LocalId;
        /// <summary>Positive owner-local creation sequence, independent of storage command sequences.</summary>
        public long CreationSequence => Value.Sequence;
        /// <summary>Player whose production this is. Delivery requires that same destination owner.</summary>
        public int PlayerId => Value.Player;
        /// <summary>Audited native pool used by the registered output.</summary>
        public NativeStoragePool Pool => (NativeStoragePool)Value.Kind;
        /// <summary>Whole quantity originally issued, unchanged by delivery or discard.</summary>
        public int IssuedQuantity => Value.Issued;
        /// <summary>Undelivered quantity at observation time. Read a new snapshot to observe later changes.</summary>
        public int Remaining => Value.Remaining;
    }

    public sealed partial class NativeStorageClient
    {
        /// <summary>Reads the cargo creation high-watermark for this owner. This reserves no number.
        /// Every cargo method requires the caller-held real engine monitor, a confirmed SP/editor
        /// world, no native add-goods chain and no frozen critical save. Real multiplayer refuses.</summary>
        public bool TryGetLastCargoSequence(out long sequence, out string reason) =>
            NativeStorageRuntime.Instance.TryGetLastCargoSequence(owner, out sequence, out reason);
        /// <summary>Creates an immutable-identity mission lot for verified external production.
        /// The exact registered good must belong to this client; player is 1–8 and quantity positive.
        /// This adds no stored stock, gold effect, actor or arrival detection. Supply creation proof
        /// and player authorization in the consumer. New positive sequences exceed this owner's
        /// cargo high-watermark; identical retained retries return CURRENT remaining quantity,
        /// changed good/player/issued quantity conflicts, and evicted IDs never recreate.
        /// At most 1024 active/terminal lots are retained; only terminal lots can be evicted.
        /// Hold the engine monitor through storing the returned sequence in actor save state.</summary>
        public bool TryCreateCargo(long creationSequence, NativeStoredGood good, int playerId, int quantity,
            out NativeCargoSnapshot cargo, out GoodsTransactionStatus status, out string reason) =>
            NativeStorageRuntime.Instance.TryCreateCargo(owner, creationSequence, good, playerId, quantity, out cargo, out status, out reason);
        /// <summary>Reacquires this owner's retained lot in the current world, including retained
        /// completed lots. After a load, use this instead of a snapshot from the previous world.
        /// A missing/expired receipt does not authorize recreating cargo from a cached quantity.</summary>
        public bool TryGetCargo(long creationSequence, out NativeCargoSnapshot cargo, out string reason) =>
            NativeStorageRuntime.Instance.TryGetCargo(owner, creationSequence, out cargo, out reason);
        /// <summary>Atomically moves a positive whole quantity from this cargo checkpoint into an
        /// exact live storage building of the lot's pool and player. No gold is read or written.
        /// The consumer proves carrier custody and real arrival. Retain the ORIGINAL cargo and
        /// destination snapshots when retrying; the latest identical checkpoint retry returns
        /// AlreadyApplied without either effect, even after the destination vanished. A changed
        /// latest intent conflicts; older unretained checkpoints return ExpiredOperation.
        /// On success use the returned snapshot; never subtract a second cached Cargo integer.</summary>
        public bool TryDepositCargo(NativeCargoSnapshot expectedCargo, NativeStorageSnapshot destination, int quantity,
            out NativeCargoSnapshot updatedCargo, out GoodsTransactionStatus status, out string reason) =>
            NativeStorageRuntime.Instance.TryDepositCargo(owner, expectedCargo, destination, quantity, out updatedCargo, out status, out reason);
        /// <summary>Records explicit loss of ALL undelivered quantity at this checkpoint. It does
        /// not refund deposited stock. Latest identical retries are idempotent; a stale checkpoint
        /// cannot discard a later quantity. Destruction/cancellation policy stays with the consumer;
        /// no Dispose, GC, timeout or actor event implicitly discards cargo.</summary>
        public bool TryDiscardCargo(NativeCargoSnapshot expectedCargo, out NativeCargoSnapshot updatedCargo,
            out GoodsTransactionStatus status, out string reason) =>
            NativeStorageRuntime.Instance.TryDiscardCargo(owner, expectedCargo, out updatedCargo, out status, out reason);
    }
}
