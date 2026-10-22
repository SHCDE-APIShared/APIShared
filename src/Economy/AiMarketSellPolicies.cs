using System;
using System.Collections.Generic;
using System.Threading;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    /// <summary>Final constraints requested by enabled owners. Constraints from all owners are combined with OR.</summary>
    [Flags]
    public enum AiMarketSellSafety
    {
        /// <summary>No final constraint.</summary>
        None = 0,
        /// <summary>Floor the final effective reserve at zero, after optional adjusters.</summary>
        NonNegativeReserve = 1,
        /// <summary>Limit the prepared quantity to signed, nonnegative total stock.</summary>
        LimitQuantityToStock = 2
    }

    /// <summary>Pre-decision data, after Vanilla's emergency, demand and reserve rules. No sale has occurred.</summary>
    public sealed class AiMarketSellReserveContext
    {
        private int reserve;
        private bool open = true;
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        internal AiMarketSellReserveContext(int player, int good, int stock, int original)
        { PlayerId = player; Good = good; Stock = stock; OriginalReserve = reserve = original; }
        /// <summary>Validated one-based native player ID (1..8).</summary>
        public int PlayerId { get; }
        /// <summary>Native eGoods index, excluding null and gold.</summary>
        public int Good { get; }
        /// <summary>Signed native total stock; this service does not repair corrupt storage.</summary>
        public int Stock { get; }
        /// <summary>Unmodified effective Vanilla reserve.</summary>
        public int OriginalReserve { get; }
        /// <summary>Proposed reserve. Writable only synchronously inside the current dispatch; final safety follows.</summary>
        public int Reserve { get => reserve; set { CheckOpen(); reserve = value; } }
        private void CheckOpen()
        { if (!open || thread != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("Sell context is no longer writable on this thread."); }
        internal void Close() => open = false;
    }

    /// <summary>Preparation before pricing and booking, reached only after Vanilla's negative-request rejection.</summary>
    public sealed class AiMarketSellQuantityContext
    {
        private int amount;
        private bool open = true;
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        internal AiMarketSellQuantityContext(int player, int good, int stock, int original)
        { PlayerId = player; Good = good; Stock = stock; OriginalAmount = amount = original; }
        /// <summary>Validated one-based native player ID (1..8).</summary>
        public int PlayerId { get; }
        /// <summary>Native eGoods index, excluding null and gold.</summary>
        public int Good { get; }
        /// <summary>Signed total stock read before optional adjusters.</summary>
        public int Stock { get; }
        /// <summary>Original nonnegative requested quantity. This is not a completed sale notification.</summary>
        public int OriginalAmount { get; }
        /// <summary>Proposed amount. Nonpositive results cancel all sale effects. Final owner constraints follow.</summary>
        public int Amount { get => amount; set { CheckOpen(); amount = value; } }
        private void CheckOpen()
        { if (!open || thread != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("Sell context is no longer writable on this thread."); }
        internal void Close() => open = false;
    }

    /// <summary>Process-held policy, initially disabled. There is no unregistration or native unpatch operation.</summary>
    public sealed class AiMarketSellRegistration
    {
        private readonly AiMarketSellRegistry registry;
        internal readonly AiMarketSellSafety Safety;
        internal readonly Action<AiMarketSellReserveContext> ReserveAdjuster;
        internal readonly Action<AiMarketSellQuantityContext> QuantityAdjuster;
        internal readonly int Order;
        internal bool Active;
        private int errors;
        internal AiMarketSellRegistration(AiMarketSellRegistry registry, string owner, string id, AiMarketSellSafety safety,
            Action<AiMarketSellReserveContext> reserve, Action<AiMarketSellQuantityContext> quantity, int order)
        { this.registry = registry; OwnerGuid = owner; RegistrationId = id; Safety = safety; ReserveAdjuster = reserve; QuantityAdjuster = quantity; Order = order; }
        /// <summary>Owner identity; ordinal ordering breaks equal-order ties.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable owner-local identity; duplicates are refused.</summary>
        public string RegistrationId { get; }
        /// <summary>Current logical activation, independent of other owners.</summary>
        public bool IsEnabled => registry.IsEnabled(this);
        /// <summary>Callback failures, saturating at one million. No per-callback logging is performed.</summary>
        public int CallbackErrorCount => Volatile.Read(ref errors);
        /// <summary>Change this owner's demand before a mission. All peers must apply the same synchronized policy.</summary>
        public void SetEnabled(bool enabled) => registry.SetEnabled(this, enabled);
        internal void Error()
        { int value; do { value = Volatile.Read(ref errors); if (value >= 1000000) return; } while (Interlocked.CompareExchange(ref errors, value + 1, value) != value); }
    }

    /// <summary>Single shared owner of the final AI reserve and pre-booking quantity sites. Dormant service preserves Vanilla.</summary>
    public static class AiMarketSellPolicies
    {
        private static readonly AiMarketSellRegistry Registry = new AiMarketSellRegistry(
            AiMarketSellNativeRuntime.EnsureInstalled, AiMarketSellNativeRuntime.PublishDemand);

        /// <summary>Register once after APIShared native initialization (LibraryLoaded/WhenReady). Identity is process-held,
        /// initially inactive. Returns false and a reason for invalid identity, duplicate identity or unavailable/occupied sites.
        /// Enabled callbacks run synchronously on the native caller thread, outside locks, ordered by order, then owner/id ordinal.
        /// A throwing callback's own mutation is rolled back; subsequent callbacks continue. Recursive dispatch bypasses callbacks
        /// but retains native safety. No UI, network sends or local-only simulation decisions are permitted here.
        /// No callback is required for safety-only clients: those paths remain entirely native.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, AiMarketSellSafety safety,
            out AiMarketSellRegistration registration, out string error,
            Action<AiMarketSellReserveContext> reserveAdjuster = null,
            Action<AiMarketSellQuantityContext> quantityAdjuster = null, int order = 0) =>
            Registry.TryRegister(ownerGuid, registrationId, safety, out registration, out error, reserveAdjuster, quantityAdjuster, order);

        internal static int Reserve(int slot, int good, int stock, int original) =>
            slot % 0x160F == 0 ? Registry.Reserve(slot / 0x160F, good, stock, original) : original;
        internal static int Quantity(int player, int good, int stock, int original) => Registry.Quantity(player, good, stock, original);
    }

    // Rare registration writes publish immutable ordered arrays. Readers never acquire the writer lock.
    internal sealed class AiMarketSellRegistry
    {
        private readonly object gate = new object();
        private readonly List<AiMarketSellRegistration> all = new List<AiMarketSellRegistration>();
        private AiMarketSellRegistration[] snapshot = Array.Empty<AiMarketSellRegistration>();
        private readonly Action install;
        private readonly Action<int> publish;
        [ThreadStatic] private static int depth;
        internal AiMarketSellRegistry(Action install, Action<int> publish) { this.install = install; this.publish = publish; }
        internal bool TryRegister(string owner, string id, AiMarketSellSafety safety, out AiMarketSellRegistration registration,
            out string error, Action<AiMarketSellReserveContext> reserve = null, Action<AiMarketSellQuantityContext> quantity = null, int order = 0)
        {
            registration = null; error = null;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || (safety & ~(AiMarketSellSafety)3) != 0)
            { error = "Invalid sell policy identity or safety flags."; return false; }
            lock (gate)
            {
                foreach (var existing in all)
                    if (existing.OwnerGuid == owner && existing.RegistrationId == id)
                    { error = "Duplicate sell policy: " + owner + "/" + id; return false; }
                try { install(); }
                catch (Exception failure) { error = failure.Message; return false; }
                registration = new AiMarketSellRegistration(this, owner, id, safety, reserve, quantity, order);
                all.Add(registration); return true;
            }
        }
        internal bool IsEnabled(AiMarketSellRegistration registration) { lock (gate) return registration.Active; }
        internal void SetEnabled(AiMarketSellRegistration registration, bool enabled)
        {
            lock (gate)
            {
                if (registration.Active == enabled) return;
                registration.Active = enabled;
                var active = all.FindAll(item => item.Active);
                active.Sort((x, y) => { int c = x.Order.CompareTo(y.Order); if (c != 0) return c;
                    c = StringComparer.Ordinal.Compare(x.OwnerGuid, y.OwnerGuid); return c != 0 ? c : StringComparer.Ordinal.Compare(x.RegistrationId, y.RegistrationId); });
                int demand = 0;
                foreach (var item in active)
                { demand |= (int)item.Safety; if (item.ReserveAdjuster != null) demand |= 4; if (item.QuantityAdjuster != null) demand |= 8; }
                Volatile.Write(ref snapshot, active.ToArray());
                publish(demand);
            }
        }
        private static bool Valid(int player, int good) => player >= 1 && player <= 8 &&
            good > (int)eGoods.STORED_NULL && good < (int)eGoods.Count && good != (int)eGoods.STORED_GOLD;
        internal int Reserve(int player, int good, int stock, int original)
        {
            var current = Volatile.Read(ref snapshot);
            if (depth != 0 || current.Length == 0 || !Valid(player, good)) return original;
            depth++;
            var context = new AiMarketSellReserveContext(player, good, stock, original);
            try
            {
                foreach (var item in current)
                { if (item.ReserveAdjuster == null) continue; int previous = context.Reserve;
                    try { item.ReserveAdjuster(context); } catch { context.Reserve = previous; item.Error(); } }
                return context.Reserve;
            }
            finally { context.Close(); depth--; }
        }
        internal int Quantity(int player, int good, int stock, int original)
        {
            var current = Volatile.Read(ref snapshot);
            if (depth != 0 || current.Length == 0 || !Valid(player, good)) return original;
            depth++;
            var context = new AiMarketSellQuantityContext(player, good, stock, original);
            try
            {
                foreach (var item in current)
                { if (item.QuantityAdjuster == null) continue; int previous = context.Amount;
                    try { item.QuantityAdjuster(context); } catch { context.Amount = previous; item.Error(); } }
                return context.Amount;
            }
            finally { context.Close(); depth--; }
        }
    }
}
