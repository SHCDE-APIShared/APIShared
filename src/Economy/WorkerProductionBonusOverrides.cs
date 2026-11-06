using System;
using System.Collections.Generic;

namespace APIShared.Economy
{
    /// <summary>Owner-bound overrides of Vanilla's fixed worker-production bonus, in whole percentage points.</summary>
    /// <remarks>Register after APIShared's native initialization. A registration is initially inactive and lives
    /// until process exit. Order is ascending priority, ordinal owner GUID, then ordinal registration ID; the
    /// last non-null value wins. No registration preserves Vanilla's 50 percent. The native mode and bonus-flag
    /// conditions, fear productivity, base amounts, signed fractional carry, and existing Extender events remain
    /// authoritative. Negative values reduce base output; they do not remove existing stock or refund ingredients.
    /// Fully installed guards also finish saved negative batches after their owner is disabled, using existing
    /// signed extra-output provenance; ordinary Vanilla-reachable production stays unchanged without an override.
    /// Configure identical values on every peer at synchronized mission boundaries. No per-worker managed
    /// callback, roster lookup, allocation or registration lock is added to the native calculation.</remarks>
    public static class WorkerProductionBonusOverrides
    {
        /// <summary>Unmodified fixed bonus, in percentage points of base output.</summary>
        public const int VanillaPercent = 50;
        /// <summary>Lowest admitted override; native productivity is floored at 100 before this contribution.</summary>
        public const int MinimumPercent = -100;
        /// <summary>Highest admitted override, audited for supported base quantities through 100.</summary>
        public const int MaximumPercent = 1000;

        private static readonly WorkerProductionBonusRegistry Registry = new WorkerProductionBonusRegistry(
            () => WorkerProductionBonusNativeRuntime.IsAvailable,
            () => WorkerProductionBonusNativeRuntime.Diagnostic,
            WorkerProductionBonusNativeRuntime.PublishPercent);

        /// <summary>True only after the calculation, zero-output, transport and display protections all published.</summary>
        public static bool IsAvailable => WorkerProductionBonusNativeRuntime.IsAvailable;
        /// <summary>Initialization/validation failure, or an empty string when the service is available.</summary>
        public static string Diagnostic => IsAvailable ? string.Empty : WorkerProductionBonusNativeRuntime.Diagnostic;
        /// <summary>Native-build and service-contract identity for peer admission; empty while unavailable.</summary>
        /// <remarks>Compare ordinally using an authenticated existing settings/readiness channel before multiplayer
        /// launch. Saved negative batches still need matching native protection at 50 or with a consumer disabled.
        /// This string is not a network transport or an assertion that peers are synchronized.</remarks>
        public static string CompatibilityKey => IsAvailable
            ? "worker-production-bonus/v1/" + WorkerProductionBonusNativeDefinition.ReferenceHash : string.Empty;

        /// <summary>Registers an initially inactive override. Invalid/duplicate identities and unavailable native
        /// protection return false with a diagnostic; no game values change on registration.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId,
            out WorkerProductionBonusOverrideRegistration registration, out string diagnostic, int order = 0) =>
            Registry.TryRegister(ownerGuid, registrationId, out registration, out diagnostic, order);
    }

    /// <summary>Process-held production-bonus contribution; disable with SetPercent(null), never by unpatching.</summary>
    public sealed class WorkerProductionBonusOverrideRegistration
    {
        private readonly WorkerProductionBonusRegistry registry;
        internal readonly int Order;
        internal int? Percent;
        internal WorkerProductionBonusOverrideRegistration(WorkerProductionBonusRegistry registry, string owner, string id, int order)
        { this.registry = registry; OwnerGuid = owner; RegistrationId = id; Order = order; }
        /// <summary>Stable mod GUID, compared ordinally for equal-priority ordering.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable owner-local identity. Duplicate owner/ID pairs are rejected.</summary>
        public string RegistrationId { get; }
        /// <summary>Replaces this owner's contribution with -100..1000 whole percentage points; null reveals earlier
        /// owners or Vanilla. Validation failure preserves the previous contribution. Thread-safe publication does
        /// not authorize an asynchronous gameplay change: use a synchronized mission/settings boundary.</summary>
        /// <remarks>50 is the original bonus; 0 removes only that bonus. Fear remains additive. Existing positive
        /// fractional credits can still yield a final item after changing to -100. Base quantities above 100 or
        /// foreign productivity outside Vanilla's range require a separate native arithmetic audit.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">A non-null value is outside -100..1000.</exception>
        public void SetPercent(int? percent) => registry.Set(this, percent);
    }

    // An injectable native-publication boundary lets tests execute the real ordering/validation contract.
    // All mutation is rare configuration work; native readers consume just the atomically published integer.
    internal sealed class WorkerProductionBonusRegistry
    {
        private readonly object sync = new object();
        private readonly List<WorkerProductionBonusOverrideRegistration> registrations = new List<WorkerProductionBonusOverrideRegistration>();
        private readonly Func<bool> available;
        private readonly Func<string> diagnostic;
        private readonly Action<int> publish;
        internal WorkerProductionBonusRegistry(Func<bool> available, Func<string> diagnostic, Action<int> publish)
        { this.available = available; this.diagnostic = diagnostic; this.publish = publish; }

        internal bool TryRegister(string owner, string id, out WorkerProductionBonusOverrideRegistration registration,
            out string reason, int order = 0)
        {
            registration = null;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id))
            { reason = "Owner GUID and registration ID are required."; return false; }
            lock (sync)
            {
                if (!available()) { reason = "Worker production bonus unavailable: " + diagnostic(); return false; }
                foreach (var item in registrations)
                    if (item.OwnerGuid == owner && item.RegistrationId == id)
                    { reason = "Duplicate worker-production owner/registration ID."; return false; }
                registration = new WorkerProductionBonusOverrideRegistration(this, owner, id, order);
                registrations.Add(registration);
                registrations.Sort((a, b) => {
                    int result = a.Order.CompareTo(b.Order);
                    if (result == 0) result = StringComparer.Ordinal.Compare(a.OwnerGuid, b.OwnerGuid);
                    return result != 0 ? result : StringComparer.Ordinal.Compare(a.RegistrationId, b.RegistrationId);
                });
            }
            reason = string.Empty;
            return true;
        }

        internal void Set(WorkerProductionBonusOverrideRegistration registration, int? percent)
        {
            if (percent.HasValue && (percent.Value < WorkerProductionBonusOverrides.MinimumPercent ||
                percent.Value > WorkerProductionBonusOverrides.MaximumPercent))
                throw new ArgumentOutOfRangeException(nameof(percent), "Worker production bonus must be -100..1000 percent.");
            lock (sync)
            {
                if (!available()) throw new InvalidOperationException("Worker production bonus unavailable: " + diagnostic());
                int effective = WorkerProductionBonusOverrides.VanillaPercent;
                foreach (var item in registrations)
                {
                    int? value = ReferenceEquals(item, registration) ? percent : item.Percent;
                    if (value.HasValue) effective = value.Value;
                }
                // Publication first: a rejected write must not commit an unobservable registry change.
                publish(effective);
                registration.Percent = percent;
            }
        }
    }
}
