using System;

namespace APIShared.Economy
{
    /// <summary>Compatibility facade for ordered absolute overrides of Vanilla's fixed worker-production percentage.</summary>
    /// <remarks>The public identities, unavailable-registration failure, synchronized publication and
    /// -100..1000 validation contract are retained. Contributions now share WorkerProductionModifiers composition:
    /// SetPercent(50) is an explicit FixedValue(50), while null removes this owner's contribution. New consumers
    /// should use desired WorkerProductionProfile registrations and the shared mission-admission status.</remarks>
    public static class WorkerProductionBonusOverrides
    {
        /// <summary>Unmodified fixed percentage.</summary>
        public const int VanillaPercent=50;
        /// <summary>Lowest admitted fixed percentage.</summary>
        public const int MinimumPercent=-100;
        /// <summary>Highest admitted fixed percentage.</summary>
        public const int MaximumPercent=1000;
        private static readonly WorkerProductionBonusRegistry Registry=new WorkerProductionBonusRegistry(WorkerProductionModifiers.Registry);
        /// <summary>True when the original fixed-bonus native cohort is ready, independently of the additional base-output cohort.</summary>
        public static bool IsAvailable => WorkerProductionBonusNativeRuntime.IsAvailable;
        /// <summary>Fixed-bonus initialization failure, or empty when available.</summary>
        public static string Diagnostic => IsAvailable ? string.Empty : WorkerProductionBonusNativeRuntime.Diagnostic;
        /// <summary>Original fixed-bonus compatibility identity; shared base-output admission uses the new service status.</summary>
        public static string CompatibilityKey => IsAvailable
            ? "worker-production-bonus/v1/"+WorkerProductionBonusNativeDefinition.ReferenceHash : string.Empty;
        /// <summary>Registers an initially inactive, process-held absolute contribution. Invalid/duplicate identities
        /// or unavailable fixed-bonus protection return false with a diagnostic and change no game values.</summary>
        public static bool TryRegister(string ownerGuid,string registrationId,
            out WorkerProductionBonusOverrideRegistration registration,out string diagnostic,int order=0) =>
            Registry.TryRegister(ownerGuid,registrationId,out registration,out diagnostic,order);
    }

    /// <summary>Process-held legacy registration; disable logically with SetPercent(null).</summary>
    public sealed class WorkerProductionBonusOverrideRegistration
    {
        private readonly WorkerProductionBonusRegistry registry;
        internal readonly WorkerProductionModifierRegistration Contribution;
        internal WorkerProductionBonusOverrideRegistration(WorkerProductionBonusRegistry registry,WorkerProductionModifierRegistration contribution)
        { this.registry=registry; Contribution=contribution; }
        /// <summary>Stable mod GUID, ordered ordinally after priority.</summary>
        public string OwnerGuid => Contribution.OwnerGuid;
        /// <summary>Stable owner-local identity.</summary>
        public string RegistrationId => Contribution.RegistrationId;
        /// <summary>Publishes an absolute -100..1000 percentage at a synchronized settings/mission boundary. Null
        /// reveals earlier contributions; 50 remains an explicit fixed value. Rejected publication preserves this
        /// registration's previous contribution. When mode-dependent profiles are staged before a real mission mode
        /// exists, publication waits for that mode's admitted bundle member. This method is thread-safe, but is not gameplay synchronization.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The non-null percentage is outside -100..1000.</exception>
        public void SetPercent(int? percent) => registry.Set(this,percent);
    }

    // Tests may inject only the native publication boundary; the ordering/composition registry stays shared.
    internal sealed class WorkerProductionBonusRegistry
    {
        private readonly WorkerProductionModifierRegistry registry;
        internal WorkerProductionBonusRegistry(WorkerProductionModifierRegistry registry) { this.registry=registry; }
        internal WorkerProductionBonusRegistry(Func<bool> available,Func<string> diagnostic,Action<int> publish)
            :this(new WorkerProductionModifierRegistry(available,diagnostic,rules=>publish(rules.FixedBonusPercent))) { }
        internal bool TryRegister(string owner,string id,out WorkerProductionBonusOverrideRegistration registration,out string reason,int order=0)
        {
            registration=null;
            if(string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id))
            { reason="Owner GUID and registration ID are required."; return false; }
            if(!registry.LegacyAvailable) { reason="Worker production bonus unavailable: "+registry.LegacyDiagnostic; return false; }
            if(!registry.TryRegister(owner,id,out var contribution,out reason,order)) return false;
            registration=new WorkerProductionBonusOverrideRegistration(this,contribution); return true;
        }
        internal void Set(WorkerProductionBonusOverrideRegistration registration,int? percent)
        {
            if(percent.HasValue && (percent.Value<WorkerProductionBonusOverrides.MinimumPercent || percent.Value>WorkerProductionBonusOverrides.MaximumPercent))
                throw new ArgumentOutOfRangeException(nameof(percent),"Worker production bonus must be -100..1000 percent.");
            registry.SetLegacy(registration.Contribution,percent);
        }
    }
}
