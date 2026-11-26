using System;
using APIShared.Internal;
using System.Collections.Generic;

namespace APIShared.Buildings
{
    /// <summary>Immutable self-demolition context shared by buildings and other supported construction kinds.</summary>
    public sealed class ConstructionRefundContext
    {
        internal ConstructionRefundContext(ConstructionCostTarget target, int player, int percentage)
        { Target = target; PlayerId = player; Percentage = percentage; }
        /// <summary>The semantic construction target, never a raw table index.</summary>
        public ConstructionCostTarget Target { get; }
        /// <summary>The authenticated human actor.</summary>
        public int PlayerId { get; }
        /// <summary>The current action percentage, before the per-good multiplier.</summary>
        public int Percentage { get; }
    }

    /// <summary>Process-held action-percentage policies for all supported construction refund adapters.
    /// Policies run synchronously in simulation order; return null to preserve the incoming percentage.
    /// Per-good SE multipliers are applied separately, exactly once. Registration is on initialized Unity;
    /// callbacks must be deterministic, side-effect free and must not access UI. No unregister.</summary>
    public static class ConstructionRefundPolicies
    {
        private sealed class Entry { internal string Key; internal Func<ConstructionRefundContext, int?> Policy; }
        private static readonly object gate = new object();
        private static Entry[] entries = Array.Empty<Entry>();
        /// <summary>Registers an owner-local stable policy. Duplicate identities are refused.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Func<ConstructionRefundContext, int?> policy, out string reason)
        {
            reason = string.Empty;
            if (!UnityMainThreadDispatch.IsMainThread || string.IsNullOrWhiteSpace(ownerGuid) ||
                string.IsNullOrWhiteSpace(registrationId) || policy == null)
            { reason = "Initialized Unity thread, owner, ID and policy required."; return false; }
            string key = ownerGuid + "/" + registrationId;
            lock (gate)
            {
                foreach (var e in entries) if (e.Key == key) { reason = "Duplicate refund policy."; return false; }
                var next = new List<Entry>(entries) { new Entry { Key = key, Policy = policy } };
                next.Sort((a,b) => StringComparer.Ordinal.Compare(a.Key,b.Key));
                System.Threading.Volatile.Write(ref entries, next.ToArray());
            }
            return true;
        }
        internal static int Evaluate(ConstructionCostTarget target, int player, int percentage)
        {
            int value = Math.Max(0, Math.Min(100, percentage));
            foreach (var e in System.Threading.Volatile.Read(ref entries))
            {
                try { int? result = e.Policy(new ConstructionRefundContext(target, player, value));
                    if (result.HasValue) value = Math.Max(0, Math.Min(100, result.Value)); }
                catch (Exception ex) { NativeApiLog.Error(ApiSharedRuntime.ProcessInstance.Logger,
                    "CONSTRUCTION_REFUND_POLICY: " + e.Key + ": " + ex.Message); }
            }
            return value;
        }
    }
}
