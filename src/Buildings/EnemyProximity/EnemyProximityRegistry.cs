using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Buildings
{
    // Ordered participant registry: immutable active snapshot, phase/order/owner/ID ordering, isolated failures.
    internal sealed class EnemyProximityRegistry
    {
        private const int MaximumRadius = 46340;
        private readonly object gate = new object();
        private readonly List<EnemyProximityRegistration> all = new List<EnemyProximityRegistration>();
        private EnemyProximityRegistration[] active = Array.Empty<EnemyProximityRegistration>();
        private EnemyProximityRegistration[] identities = Array.Empty<EnemyProximityRegistration>();
        [ThreadStatic] private static int depth;
        internal bool HasDemand => Volatile.Read(ref active).Length != 0;

        internal bool TryRegister(string owner, string id, EnemyProximityPhase phase,
            Func<EnemyProximityContext, EnemyProximityDecision> callback,
            out EnemyProximityRegistration registration, out string error, int order = 0)
        {
            registration = null; error = null;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || callback == null ||
                (phase != EnemyProximityPhase.Override && phase != EnemyProximityPhase.Limit))
            { error = "Invalid enemy proximity registration."; return false; }
            lock (gate)
            {
                foreach (var item in all)
                    if (item.OwnerGuid == owner && item.RegistrationId == id)
                    { error = "Duplicate enemy proximity registration: " + owner + "/" + id; return false; }
                registration = new EnemyProximityRegistration(this, owner, id, phase, callback, order);
                all.Add(registration);
                Volatile.Write(ref identities, all.ToArray());
                return true;
            }
        }
        internal bool HasRegistration(string owner, string id)
        {
            foreach (var item in Volatile.Read(ref identities))
                if (item.OwnerGuid == owner && item.RegistrationId == id) return true;
            return false;
        }
        internal bool IsEnabled(EnemyProximityRegistration item) { lock (gate) return item.Active; }
        internal void SetEnabled(EnemyProximityRegistration item, bool enabled)
        {
            lock (gate)
            {
                if (item.Active == enabled) return;
                item.Active = enabled;
                var enabledItems = all.FindAll(entry => entry.Active);
                enabledItems.Sort((x, y) => {
                    int c = x.Phase.CompareTo(y.Phase); if (c != 0) return c;
                    c = x.Order.CompareTo(y.Order); if (c != 0) return c;
                    c = StringComparer.Ordinal.Compare(x.OwnerGuid, y.OwnerGuid);
                    return c != 0 ? c : StringComparer.Ordinal.Compare(x.RegistrationId, y.RegistrationId);
                });
                Volatile.Write(ref active, enabledItems.ToArray());
            }
        }
        internal EnemyProximityDecision Evaluate(int player, int x, int y, int original, byte unknown)
        {
            var snapshot = Volatile.Read(ref active);
            if (depth != 0 || snapshot.Length == 0 || player < 1 || player > 8) return default;
            int civilian = original, military = original;
            bool blocked = false, radiusAllowed = ValidRadius(original);
            depth++;
            try
            {
                foreach (var item in snapshot)
                {
                    EnemyProximityDecision result;
                    try { result = item.Callback(new EnemyProximityContext(player, x, y, original, unknown, civilian, military)); }
                    catch { item.Error(); continue; }
                    if ((result.CivilianRadius.HasValue && !ValidRadius(result.CivilianRadius.Value)) ||
                        (result.MilitaryRadius.HasValue && !ValidRadius(result.MilitaryRadius.Value)))
                    { item.Error(); continue; }
                    blocked |= result.Block;
                    if (!radiusAllowed) continue;
                    if (result.CivilianRadius.HasValue)
                        civilian = item.Phase == EnemyProximityPhase.Override ? result.CivilianRadius.Value : Math.Min(civilian, result.CivilianRadius.Value);
                    if (result.MilitaryRadius.HasValue)
                        military = item.Phase == EnemyProximityPhase.Override ? result.MilitaryRadius.Value : Math.Min(military, result.MilitaryRadius.Value);
                }
                return new EnemyProximityDecision(civilian != original ? (int?)civilian : null,
                    military != original ? (int?)military : null, blocked);
            }
            finally { depth--; }
        }
        private static bool ValidRadius(int radius) => radius >= 0 && radius <= MaximumRadius;
    }
}
