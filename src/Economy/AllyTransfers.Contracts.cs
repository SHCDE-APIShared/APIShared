using SHCDESE.Interop;

namespace APIShared.Economy
{
    /// <summary>Bounds for one immediate transfer. Scheduling, authorization and selection of allies belong to the caller.</summary>
    public sealed class AllyTransferRequest
    {
        /// <summary>Creates immutable operation bounds. Amounts use the game's integer goods/gold units.</summary>
        public AllyTransferRequest(int senderId, int recipientId, eGoods good, int maximumAmount,
            int minimumSenderRemaining = 0, int maximumRecipientAmount = int.MaxValue)
        {
            SenderPlayerId = senderId; RecipientPlayerId = recipientId; Good = good;
            MaximumAmount = maximumAmount; MinimumSenderRemaining = minimumSenderRemaining;
            MaximumRecipientAmount = maximumRecipientAmount;
        }
        /// <summary>One-based sender ID, independent of the local report viewpoint.</summary>
        public int SenderPlayerId { get; }
        /// <summary>One-based recipient ID.</summary>
        public int RecipientPlayerId { get; }
        /// <summary>A good supported by Vanilla's ally-send UI, including gold.</summary>
        public eGoods Good { get; }
        /// <summary>Positive upper bound for this call; the service can send a smaller amount.</summary>
        public int MaximumAmount { get; }
        /// <summary>Nonnegative minimum to preserve in the sender's current TotalGoods balance.</summary>
        public int MinimumSenderRemaining { get; }
        /// <summary>Nonnegative upper bound for the recipient's current TotalGoods balance after this operation.</summary>
        public int MaximumRecipientAmount { get; }
    }

    /// <summary>One current capacity observation; it reserves neither resources nor storage.</summary>
    public sealed class AllyTransferCapacity
    {
        internal AllyTransferCapacity(int recipient, eGoods good, int stored, int nativeFree)
        {
            RecipientPlayerId = recipient; Good = good; AmountStored = stored;
            HasStorage = nativeFree >= 0;
            FreeAmount = HasStorage ? (int)System.Math.Min((long)nativeFree, (long)int.MaxValue - stored) : 0;
        }
        /// <summary>One-based recipient ID.</summary>
        public int RecipientPlayerId { get; }
        /// <summary>Queried goods type.</summary>
        public eGoods Good { get; }
        /// <summary>Current stored quantity; incoming delivery reserves are not included.</summary>
        public int AmountStored { get; }
        /// <summary>Whether a compatible storage exists. Gold needs no storage building.</summary>
        public bool HasStorage { get; }
        /// <summary>Nonnegative free physical capacity, limited by signed-balance headroom. Zero also describes a full existing storage.</summary>
        public int FreeAmount { get; }
    }

    /// <summary>Completion classification of one immediate native transfer attempt.</summary>
    public enum AllyTransferStatus
    {
        /// <summary>The service rejected the request before invoking native code.</summary>
        Rejected,
        /// <summary>A successful Pre callback vetoed the entire native transfer.</summary>
        Vetoed,
        /// <summary>The native call returned normally and both observed net deltas agree.</summary>
        Completed,
        /// <summary>The call returned, but readback, nesting or unequal net deltas prevents a confirmed amount.</summary>
        Uncertain,
        /// <summary>The native/managed call chain threw. Any partial mutation is retained and reported through available readbacks.</summary>
        Faulted
    }

    /// <summary>Immutable net observations of one invocation; normal native return alone is not a transfer acknowledgement.</summary>
    public sealed class AllyTransferResult
    {
        internal AllyTransferResult(int sender, int recipient, eGoods good, int requested, bool originalCalled,
            AllyTransferStatus status, long? senderDebit, long? recipientCredit, bool nested)
        {
            SenderPlayerId = sender; RecipientPlayerId = recipient; Good = good; RequestedAmount = requested;
            OriginalWasCalled = originalCalled; Status = status; SenderDebit = senderDebit;
            RecipientCredit = recipientCredit; HasNestedTransfer = nested;
        }
        /// <summary>One-based sender ID supplied to this invocation.</summary>
        public int SenderPlayerId { get; }
        /// <summary>One-based recipient ID supplied to this invocation.</summary>
        public int RecipientPlayerId { get; }
        /// <summary>Goods argument supplied to the native transfer.</summary>
        public eGoods Good { get; }
        /// <summary>Amount dispatched to this invocation, after the safe service's bounds when applicable.</summary>
        public int RequestedAmount { get; }
        /// <summary>Whether this invocation entered the original native transfer.</summary>
        public bool OriginalWasCalled { get; }
        /// <summary>Completion classification, independent of the sign or presence of the deltas.</summary>
        public AllyTransferStatus Status { get; }
        /// <summary>Whether Pre vetoed the entire native operation.</summary>
        public bool WasVetoed => Status == AllyTransferStatus.Vetoed;
        /// <summary>Sender balance before minus after the original call; null means an unavailable readback.</summary>
        public long? SenderDebit { get; }
        /// <summary>Recipient balance after minus before the original call; null means an unavailable readback.</summary>
        public long? RecipientCredit { get; }
        /// <summary>Whether a nested native transfer occurred inside this invocation. Its net effects cannot be attributed separately.</summary>
        public bool HasNestedTransfer { get; }
        /// <summary>Equal nonnegative observed net deltas after normal completion, or zero for a veto. Null means no confirmed quantity.</summary>
        public int? TransferredAmount => WasVetoed ? 0 : Status == AllyTransferStatus.Completed &&
            SenderDebit.HasValue && SenderDebit.Value >= 0 && SenderDebit.Value <= int.MaxValue &&
            SenderDebit == RecipientCredit ? (int?)SenderDebit.Value : null;
    }
}
