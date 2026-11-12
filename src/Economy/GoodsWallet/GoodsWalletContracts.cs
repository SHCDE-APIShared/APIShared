using System;
using System.Collections.Generic;
using APIShared.Goods;

namespace APIShared.Economy
{
    /// <summary>Outcome of one native-gold and virtual-goods exchange.</summary>
    public enum GoodsWalletStatus
    {
        /// <summary>Native gold and the prepared ledger state were published once.</summary>
        Applied,
        /// <summary>The exact exchange receipt already exists; native gold was not touched.</summary>
        AlreadyApplied,
        /// <summary>The owner, player, sequence or supplied lines are invalid.</summary>
        InvalidRequest,
        /// <summary>Native wallet layout/pointers are unavailable.</summary>
        Unavailable,
        /// <summary>The required engine monitor or deterministic multiplayer context is absent.</summary>
        InvalidContext,
        /// <summary>The ledger rejected the operation; inspect LedgerStatus.</summary>
        LedgerRejected,
        /// <summary>The resulting native gold balance would be negative.</summary>
        InsufficientGold,
        /// <summary>The resulting native gold balance would exceed Int32.MaxValue.</summary>
        GoldOverflow,
        /// <summary>The world changed or the ledger generation belongs to another world.</summary>
        WorldChanged
    }

    /// <summary>Immutable exchange result. Gold values describe this application, not a later live balance.</summary>
    public sealed class GoodsWalletResult
    {
        internal GoodsWalletResult(GoodsWalletStatus status, GoodsTransactionStatus ledger, string reason, int? before = null, int? after = null)
        { Status = status; LedgerStatus = ledger; Reason = reason; GoldBefore = before; GoldAfter = after; }
        /// <summary>Native/ledger completion state.</summary>
        public GoodsWalletStatus Status { get; }
        /// <summary>Ledger preparation/replay decision, or InvalidRequest if not reached. Applied means prepared;
        /// only an overall Applied Status establishes publication.</summary>
        public GoodsTransactionStatus LedgerStatus { get; }
        /// <summary>Diagnostic detail; not a stable protocol identifier.</summary>
        public string Reason { get; }
        /// <summary>Signed native gold before a new application; null for replay or rejection.</summary>
        public int? GoldBefore { get; }
        /// <summary>Native gold after a new application; null for replay or rejection.</summary>
        public int? GoldAfter { get; }
    }

    /// <summary>Owner-bound atomic native-gold and virtual-stock adapter. It grants no player-control authority.</summary>
    public sealed class GoodsWalletClient
    {
        private readonly string owner;
        internal GoodsWalletClient(string ownerGuid) { owner = ownerGuid; }
        /// <summary>Whether startup validated the supported native wallet. A current world/context is separate.</summary>
        public bool IsAvailable => GoodsWalletNative.Instance.IsReady;
        /// <summary>Reads raw signed gold in an engine-protected confirmed world. Zero is a valid balance.</summary>
        /// <remarks>Requires the already held engine monitor; real multiplayer additionally needs a shared native tick or
        /// authenticated Chore. No native pointer escapes. False is distinct from a valid zero/negative balance.</remarks>
        public bool TryReadGold(int playerId, out int gold, out string reason) => GoodsWalletNative.Engine.TryRead(playerId, out gold, out reason);
        /// <summary>Exchanges 1–32 owner/player-matching virtual changes and a signed native-gold delta, once per positive owner sequence.</summary>
        /// <remarks>Positive goldDelta credits gold; negative debits. Resulting gold must fit 0–Int32.MaxValue. The caller must already
        /// hold the actual engine monitor and authorize the player. Real multiplayer requires identical deterministic requests in native
        /// ticks or authenticated Chores. Input getters run before inventory locking and may throw; do not mutate the collection concurrently.
        /// The ledger generation is bound to this exact world; Reset/successful Restore is required before reuse in another world.
        /// Retained exact retries never touch gold; changed price/player/intent conflicts. No callback, native gold helper, rollback,
        /// transport, purchase policy or implicit save registration is introduced. Persist the ledger with the same native world.</remarks>
        public GoodsWalletResult Exchange(CustomGoodsClient goods, long operationSequence, IReadOnlyList<GoodsChange> changes, int playerId, int goldDelta) =>
            GoodsWalletNative.Engine.Exchange(owner, goods, operationSequence, changes, playerId, goldDelta);
    }
}
