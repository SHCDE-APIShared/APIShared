using System.Collections.Generic;
using APIShared.Goods;

namespace APIShared.Economy
{
    /// <summary>One proposed signed change to a registered good in an exact observed store.
    /// Validation occurs on submission; this value creates no stock, price or authorization.</summary>
    public readonly struct NativeStorageChange
    {
        /// <summary>Creates an inert proposed change. Supply the exact startup-registered handle,
        /// a current-world storage observation and a nonzero signed quantity delta.</summary>
        public NativeStorageChange(NativeStoredGood good, NativeStorageSnapshot storage, int quantityDelta)
        { Good = good; Storage = storage; QuantityDelta = quantityDelta; }
        /// <summary>The exact registered good; it must belong to the submitting client.</summary>
        public NativeStoredGood Good { get; }
        /// <summary>The original exact building/world observation, retained for an in-world retry.</summary>
        public NativeStorageSnapshot Storage { get; }
        /// <summary>Positive deposit or negative withdrawal of this good in this store.</summary>
        public int QuantityDelta { get; }
    }

    public sealed partial class NativeStorageClient
    {
        /// <summary>Atomically changes 1–64 good/store rows and one common player's native gold.
        /// Every handle must be this client's exact registered output; every store must belong to
        /// the same player and exact native world. Duplicate physical rows and zero deltas reject.</summary>
        /// <remarks>Call while already holding the actual engine monitor in a known offline/editor
        /// world or a native-tick/authenticated-Chore multiplayer mutation boundary. Authorize the
        /// player and price in the consumer; this method supplies neither transport nor production,
        /// delivery, recruitment or native-success evidence. Gold is one signed aggregate: negative
        /// buys, positive sells. Zero gold skips wallet lookup and writes entirely.
        /// At most 32 nonzero per-good/player totals may remain after aggregation; an entirely
        /// net-zero batch, including a pure gold request, rejects. All physical changes, including
        /// individually net-zero goods, participate in replay and joint pooled-capacity validation.
        /// Nothing is published partially. New positive sequences share this owner's existing
        /// storage stream, including automatic loss maintenance; allocate at execution time.
        /// Retain the original rows, snapshots, sequence and gold for a retry in this world. Row
        /// order is immaterial; retained identical retries return AlreadyApplied even after a store
        /// vanished, without restoring later lost stock. Changed payload conflicts; expired receipts
        /// never execute or assert success. Replay uses a versioned canonical SHA-256 fingerprint.
        /// The existing global 1024-operation journal does not retain every owner's latest receipt
        /// forever. A load invalidates old world snapshots. Frozen saves, native add-goods chains,
        /// overlapping prepared storage/cargo commits and unavailable restoration refuse the call.
        /// After the optional raw gold store, only prebuilt private publication and known cleanup
        /// run; no consumer callbacks or native helpers participate. Process/native failure and
        /// unlocked foreign writes remain outside the in-process atomicity guarantee.</remarks>
        public bool TryExchangeBatch(long operationSequence, IReadOnlyList<NativeStorageChange> changes, int goldDelta,
            out GoodsTransactionStatus status, out string reason) =>
            NativeStorageRuntime.Instance.TryExchangeBatch(owner, operationSequence, changes, goldDelta, out status, out reason);
    }
}
