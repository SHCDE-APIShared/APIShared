using APIShared.Goods;

namespace APIShared.Economy
{
    /// <summary>Audited native pooled storage. Stockpiles use different per-good rules and
    /// are deliberately outside this contract.</summary>
    public enum NativeStoragePool
    {
        /// <summary>Fifty shared slots across native weapons and registered virtual goods.</summary>
        Armory = 1,
        /// <summary>Two hundred fifty shared slots across native food and registered virtual goods.</summary>
        Granary = 2,
    }

    /// <summary>Immutable momentary observation of an exact live storage building. A snapshot
    /// can be retained but cannot authorize a later write; every exchange revalidates it.</summary>
    public readonly struct NativeStorageSnapshot
    {
        internal readonly object World;
        internal readonly NativeStorageProbe Probe;
        internal NativeStorageSnapshot(object world, NativeStorageProbe probe, int occupied)
        { World = world; Probe = probe; VirtualQuantity = occupied; }
        /// <summary>One-based native building slot.</summary>
        public int BuildingId => Probe.Slot;
        /// <summary>Unsigned native instance identity. Slot alone is insufficient.</summary>
        public uint GlobalId => Probe.GlobalId;
        /// <summary>Current one-based owner, 1–8. Capture can change this without changing GlobalId.</summary>
        public int PlayerId => Probe.PlayerId;
        /// <summary>Native pooled storage role.</summary>
        public NativeStoragePool Pool => (NativeStoragePool)Probe.Kind;
        /// <summary>Audited native occupancy, excluding this service's separate virtual rows.</summary>
        public int NativeQuantity => Probe.NativeUsed;
        /// <summary>Combined virtual occupancy of every registered owner in this exact building.</summary>
        public int VirtualQuantity { get; }
        /// <summary>Native pool limit, fifty for Armories and 250 for Granaries.</summary>
        public int Capacity => Probe.Capacity;
        /// <summary>Nonnegative remaining pooled space at observation time.</summary>
        public int Available => (int)System.Math.Max(0L, (long)Capacity - NativeQuantity - VirtualQuantity);
    }

    /// <summary>Process-lived owner-local good stored separately while occupying real native
    /// pooled space. The service owns its private ledger, physical rows and save record together.
    /// This creates neither a native eGoods value nor production/art/food rules.</summary>
    public sealed class NativeStoredGood
    {
        internal readonly CustomGoodId Key;
        internal readonly StoragePool Kind;
        internal NativeStoredGood(CustomGoodId key, StoragePool kind) { Key = key; Kind = kind; }
        /// <summary>Exact registering plugin GUID.</summary>
        public string OwnerGuid => Key.OwnerGuid;
        /// <summary>Stable owner-local good ID.</summary>
        public string GoodId => Key.LocalId;
        /// <summary>Pool this good occupies.</summary>
        public NativeStoragePool Pool => (NativeStoragePool)Kind;
        /// <summary>Reads the current authoritative player balance while the caller holds the
        /// real engine monitor. Returns false/reason before native reads when unavailable.
        /// The private inventory is never exposed for writes that could bypass physical storage.</summary>
        public bool TryGetStock(int playerId, out GoodsBalance balance, out string reason) =>
            NativeStorageRuntime.Instance.TryGetStock(Key, playerId, out balance, out reason);
        /// <summary>Reads this owner's last accepted positive sequence for the current world.
        /// Requires the engine monitor. Automatic storage-loss reconciliation also consumes owner
        /// sequences; allocate and execute new commands in one authoritative simulation order.</summary>
        public bool TryGetLastSequence(out long sequence, out string reason) =>
            NativeStorageRuntime.Instance.TryGetSequence(OwnerGuid, out sequence, out reason);
        /// <summary>Atomically changes this good in one exact building and its owner's native
        /// gold. Positive quantity deposits; negative withdraws. Gold delta is signed (purchase
        /// negative, sale positive). Zero quantity is invalid. No partial lots or implicit pricing.</summary>
        /// <remarks>Call while already holding the real engine monitor, in a known offline/editor
        /// world or an authenticated Chore/native-tick multiplayer mutation boundary. Register the
        /// same goods and issue the same ordered commands on every peer; no transport or purchase
        /// authorization is supplied. New positive owner sequences must exceed the high-watermark;
        /// retained identical retries never repeat either effect, changed intents conflict, expired
        /// receipts reject without asserting success. Capacity, funds, identity and all allocations
        /// are checked before commit. Retain the original snapshot with the sequence and payload
        /// for an in-world retry; a fresh observation cannot identify an old destroyed store.
        /// A zero gold delta skips wallet lookup and writes. Commit performs only an optional
        /// raw GoldRaw store and publication of
        /// prebuilt ledger/physical state, under engine→inventory locking. No callbacks, native
        /// helper calls, accounting/statistics or UI/audio effects occur inside that aperture.
        /// Stock belonging to destroyed, reused or captured stores is reconciled as a loss before
        /// further commands. Native capacity admission is coordinated by this service's shared
        /// owners; consumers must not retain competing Medium-style capacity detours.</remarks>
        public bool TryExchange(NativeStorageSnapshot storage, long operationSequence, int quantityDelta, int goldDelta,
            out GoodsTransactionStatus status, out string reason) =>
            NativeStorageRuntime.Instance.TryExchange(this, storage, operationSequence, quantityDelta, goldDelta, out status, out reason);
    }

    /// <summary>Owner-bound startup registration and protected native-storage observations.
    /// Obtaining a client installs nothing. Registered services and hooks have process lifetime.</summary>
    public sealed partial class NativeStorageClient
    {
        private readonly string owner;
        internal NativeStorageClient(string ownerGuid) { owner = ownerGuid; }
        /// <summary>Registers an owner-local printable-ASCII good ID (1–64 characters) during
        /// initialized Unity startup before loading a world. Requires supported native image/layout
        /// and shared interception availability. Duplicate or conflicting registrations fail with
        /// reason. First registration initializes capacity coordination and versioned persistence;
        /// unused consumers retain their complete original paths.</summary>
        public bool TryRegisterGood(string goodId, NativeStoragePool pool, out NativeStoredGood good, out string reason) =>
            NativeStorageRuntime.Instance.TryRegister(owner, goodId, pool, out good, out reason);
        /// <summary>Whether the current published native world has an activated storage state.
        /// Nonmutating observation only; false also covers pending, invalid or unsupported loads.</summary>
        public bool IsCurrentWorldReady => NativeStorageRuntime.Instance.IsCurrentWorldReady;
        /// <summary>Reads an exact live supported pool while the caller holds the real engine
        /// monitor. Protected reads may finish matching staged restoration. A returned snapshot
        /// conveys neither native pointer ownership nor permission to mutate gameplay.</summary>
        public bool TryObserve(int buildingId, out NativeStorageSnapshot storage, out string reason) =>
            NativeStorageRuntime.Instance.TryObserve(buildingId, out storage, out reason);
    }
}
