using System;
using System.Collections.Generic;
using APIShared.Simulation;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    /// <summary>Immediate native ally-transfer mechanics. This service does not synchronize caller state or authorize control of a player.</summary>
    public static class AllyTransfers
    {
        /// <summary>Maximum amount per safe call: Vanilla's gift notification carries a signed Int16 quantity.</summary>
        public const int MaximumTransferAmount = short.MaxValue;
        private static readonly IReadOnlyList<eGoods> supported = Array.AsReadOnly(new[] {
            eGoods.STORED_WOOD_PLANKS, eGoods.STORED_RAW_HOPS, eGoods.STORED_STONE_BLOCKS,
            eGoods.STORED_IRON_INGOTS, eGoods.STORED_PITCH_RAW, eGoods.STORED_RAW_WHEAT,
            eGoods.STORED_FOOD_BREAD, eGoods.STORED_FOOD_CHEESE, eGoods.STORED_FOOD_MEAT,
            eGoods.STORED_FOOD_FRUIT, eGoods.STORED_FOOD_ALE, eGoods.STORED_FLOUR,
            eGoods.STORED_BOWS, eGoods.STORED_CROSSBOWS, eGoods.STORED_SPEARS, eGoods.STORED_PIKES,
            eGoods.STORED_MACES, eGoods.STORED_SWORDS, eGoods.STORED_LEATHER_ARMOUR,
            eGoods.STORED_METAL_ARMOUR, eGoods.STORED_GOLD });
        private static readonly AllyTransfersEngine engine = new AllyTransfersEngine(
            AllyTransfersNative.EngineBackend, () => SimulationMutationScope.IsActive);
        /// <summary>The goods offered by Vanilla's ally-send UI. Logs, hides and refined pitch are excluded.</summary>
        public static IReadOnlyList<eGoods> SupportedGoods => supported;
        /// <summary>Whether startup prepared the validated query and transfer owner. This does not imply a current mission or multiplayer agreement.</summary>
        public static bool IsAvailable => AllyTransfersNative.IsAvailable;
        /// <summary>Whether a good is supported by the safe service. Native observation events can report other raw values.</summary>
        public static bool IsSupportedGood(eGoods good) => good >= eGoods.STORED_WOOD_PLANKS && good <= eGoods.STORED_METAL_ARMOUR &&
            good != eGoods.STORED_COW_HIDES && good != eGoods.STORED_PITCH_REFINED;
        /// <summary>Reads current physical capacity inside a shared synchronous simulation callback. Off-context calls fail before any game read.</summary>
        /// <remarks>Call from SimulationTickEvents or an authenticated Chore handler. Marshal an immutable result to Unity for display. A successful query is not a reservation, and excludes pending incoming goods.</remarks>
        public static bool TryGetCapacity(int recipientId, eGoods good, out AllyTransferCapacity capacity, out string reason) =>
            engine.TryGetCapacity(recipientId, good, out capacity, out reason);
        /// <summary>Executes one bounded native transfer synchronously, without enqueueing or changing the selected viewpoint.</summary>
        /// <remarks>Call only from SimulationTickEvents or an authenticated Chore handler with identical deterministic requests on all peers. The caller must authorize control of the sender. Living allied players, current sender reserve, recipient ceiling, physical capacity and signed arithmetic are checked immediately. Pre may veto; external SE resource policies can still change native effects. True means a normally completed, positive, balanced net transfer; false retains veto/partial/unknown observations in result. No rollback, retry, acknowledgement or Steam fallback is performed. Calls from a transfer callback are rejected.</remarks>
        public static bool TryExecute(AllyTransferRequest request, out AllyTransferResult result, out string reason) =>
            engine.TryExecute(request, out result, out reason);
    }

    internal interface IAllyTransfersBackend
    {
        bool IsReady { get; }
        bool AreAlliedLiving(int sender, int recipient);
        bool TryReadStock(int player, eGoods good, out int amount);
        int GetFreeCapacity(int recipient, eGoods good);
        AllyTransferResult Execute(AllyTransferRequest request, int amount);
    }

    // The production arithmetic and pre-read gates are injected into game-independent fixtures.
    internal sealed class AllyTransfersEngine
    {
        private readonly IAllyTransfersBackend backend;
        private readonly Func<bool> allowed;
        internal AllyTransfersEngine(IAllyTransfersBackend backend, Func<bool> allowed)
        { this.backend = backend; this.allowed = allowed; }
        internal bool TryGetCapacity(int recipient, eGoods good, out AllyTransferCapacity capacity, out string reason)
        {
            capacity = null;
            if (!CanRead(out reason)) return false;
            if (!ValidPlayer(recipient) || !AllyTransfers.IsSupportedGood(good))
            { reason = "A valid recipient and supported good are required."; return false; }
            try { return ReadCapacity(recipient, good, out capacity, out reason); }
            catch (Exception error) { reason = "Capacity query failed: " + error.Message; return false; }
        }
        private bool CanRead(out string reason)
        {
            if (!allowed()) { reason = "A shared synchronous simulation context is required."; return false; }
            if (AllyTransferEvents.IsDispatching || AllyTransfersSeBridge.HasServiceFrame) { reason = "Nested transfer service calls are not permitted."; return false; }
            if (!backend.IsReady) { reason = "The native ally-transfer service is unavailable."; return false; }
            reason = string.Empty; return true;
        }
        private bool ReadCapacity(int recipient, eGoods good, out AllyTransferCapacity capacity, out string reason)
        {
            capacity = null;
            if (!backend.TryReadStock(recipient, good, out int stock) || stock < 0)
            { reason = "The recipient balance is unavailable or negative."; return false; }
            int free = good == eGoods.STORED_GOLD ? int.MaxValue - stock : backend.GetFreeCapacity(recipient, good);
            if (free < -1) { reason = "The native storage capacity is invalid."; return false; }
            capacity = new AllyTransferCapacity(recipient, good, stock, free);
            reason = string.Empty; return true;
        }
        internal bool TryExecute(AllyTransferRequest request, out AllyTransferResult result, out string reason)
        {
            result = Rejected(request);
            if (!CanRead(out reason)) return false;
            try
            {
                if (!TryPrepare(request, out int amount, out reason)) return false;
                result = backend.Execute(request, amount);
                if (result == null) { result = Rejected(request); reason = "The native operation returned no observation."; return false; }
                bool success = result.Status == AllyTransferStatus.Completed && result.TransferredAmount.GetValueOrDefault() > 0;
                reason = success ? string.Empty : "Transfer completion: " + result.Status + ".";
                return success;
            }
            catch (Exception error) { reason = "Transfer execution failed: " + error.Message; return false; }
        }
        // Called after SE Pre and APIShared Pre, immediately before SE's original delegate.
        // It deliberately does not reject the enclosing shared event's dispatch depth.
        internal bool IsStillAdmissible(AllyTransferRequest request, int amount)
        {
            if (!allowed() || !backend.IsReady) return false;
            try { return TryPrepare(request, out int current, out _) && current >= amount; }
            catch { return false; }
        }
        private bool TryPrepare(AllyTransferRequest request, out int amount, out string reason)
        {
            amount = 0;
            if (request == null || !ValidPlayer(request.SenderPlayerId) || !ValidPlayer(request.RecipientPlayerId) ||
                request.SenderPlayerId == request.RecipientPlayerId || !AllyTransfers.IsSupportedGood(request.Good) ||
                request.MaximumAmount <= 0 || request.MinimumSenderRemaining < 0 || request.MaximumRecipientAmount < 0)
            { reason = "Distinct valid players, a supported good and nonnegative bounds are required."; return false; }
                if (!backend.AreAlliedLiving(request.SenderPlayerId, request.RecipientPlayerId))
                { reason = "Both players must have living Lords and belong to the same team."; return false; }
                if (!backend.TryReadStock(request.SenderPlayerId, request.Good, out int sender) || sender < 0)
                { reason = "The sender balance is unavailable or negative."; return false; }
                if (!ReadCapacity(request.RecipientPlayerId, request.Good, out AllyTransferCapacity capacity, out reason)) return false;
                long permitted = Math.Min((long)request.MaximumAmount, AllyTransfers.MaximumTransferAmount);
                permitted = Math.Min(permitted, (long)sender - request.MinimumSenderRemaining);
                permitted = Math.Min(permitted, (long)request.MaximumRecipientAmount - capacity.AmountStored);
                permitted = Math.Min(permitted, capacity.FreeAmount);
                if (!capacity.HasStorage || permitted <= 0)
                { reason = capacity.HasStorage ? "The operation bounds permit no transfer." : "The recipient has no compatible storage."; return false; }
                amount = (int)permitted; reason = string.Empty; return true;
        }
        internal static bool ValidPlayer(int player) => player >= 1 && player <= 8;
        private static AllyTransferResult Rejected(AllyTransferRequest request) => new AllyTransferResult(
            request?.SenderPlayerId ?? 0, request?.RecipientPlayerId ?? 0, request?.Good ?? eGoods.STORED_NULL,
            request?.MaximumAmount ?? 0, false, AllyTransferStatus.Rejected, null, null, false);
    }
}
