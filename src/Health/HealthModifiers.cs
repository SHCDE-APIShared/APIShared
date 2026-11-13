using System;
using System.Collections.Generic;
using System.Threading;
using APIShared.Events;
using APIShared.Modifiers;

namespace APIShared.Health
{
    /// <summary>Retained owner-bound health policy. It never restores or rewrites already initialized HP.</summary>
    public sealed class HealthModifierHandle
    {
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal readonly Func<HealthModifierContext, ValueModifierContribution> Provider;
        internal bool ReportedFailure;
        private int enabled;
        internal HealthModifierHandle(string owner, string id, int order,
            Func<HealthModifierContext, ValueModifierContribution> provider)
        { Owner = owner; Id = id; Order = order; Provider = provider; }
        /// <summary>Enables contributions to future initialization boundaries. Initially false.</summary>
        /// <remarks>Call deterministically on all multiplayer peers; this does not synchronize settings.
        /// A change never recalculates already initialized objects. Keep the handle for process lifetime.</remarks>
        public void SetEnabled(bool value) => Volatile.Write(ref enabled, value ? 1 : 0);
        /// <summary>Whether this retained registration contributes to future initializations.</summary>
        public bool IsEnabled => Volatile.Read(ref enabled) != 0;
    }

    /// <summary>Coordinates staged maximum-health policies once per completed native initialization.</summary>
    /// <remarks>Register during LibraryLoaded after APIShared native initialization. Callbacks run on
    /// the simulation thread, without registry locks. They must be deterministic, nonblocking and must
    /// not mutate native records or touch UI. Factors/fixed maxima compose in order/ordinal owner/ID;
    /// the final positive maximum is capped for the audited native arithmetic. Zero-HP/invulnerable and
    /// dead objects are untouched. Configuration changes affect future initializations only. Save load
    /// is an initialization, and preserves the retained original basis, not a reverse-calculated value.
    /// Registrations and the native observer remain rooted until process exit.</remarks>
    public static class HealthModifiers
    {
        private static readonly object Sync = new object();
        private static HealthModifierHandle[] policies = Array.Empty<HealthModifierHandle>();
        private static LegacyProvider[] legacy = Array.Empty<LegacyProvider>();
        private static readonly Func<HealthModifierHandle, RegistrationKey> HandleKey =
            handle => new RegistrationKey(handle.Owner, handle.Id, handle.Order);
        /// <summary>Whether native initialization, lifecycle, persistence and the observer are available.</summary>
        public static bool IsAvailable => HealthRuntime.IsAvailable;
        /// <summary>Failure reason, or an empty string when available.</summary>
        public static string UnavailableReason => HealthRuntime.UnavailableReason;

        /// <summary>Registers an initially disabled policy; duplicate owner/local IDs are rejected.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Func<HealthModifierContext, ValueModifierContribution> provider,
            out HealthModifierHandle handle, out string reason, int order = 0)
        {
            handle = null;
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || provider == null)
            { reason = "A nonempty owner, local ID and provider are required."; return false; }
            if (!IsAvailable) { reason = UnavailableReason; return false; }
            lock (Sync)
            {
                if (OrderedRegistrations.Contains(policies, HandleKey, ownerGuid, registrationId))
                { reason = "Duplicate health policy identity."; return false; }
                handle = new HealthModifierHandle(ownerGuid, registrationId, order, provider);
                Volatile.Write(ref policies, OrderedRegistrations.WithAdded(policies, handle, HandleKey));
            }
            reason = string.Empty; return true;
        }

        /// <summary>Registers a save migration reader. Null means absent; an empty array means a valid empty legacy archive.</summary>
        /// <remarks>Called only after completed loading and before HP application, when no shared archive
        /// exists. Providers must return verified original unit maxima, never divide current HP by factors.
        /// A provider exception is isolated; invalid identities are ignored. New categories stay disabled
        /// for that legacy mission, including later resaves. The provider must not write health.</remarks>
        public static bool TryRegisterLegacyBasisProvider(string ownerGuid, string registrationId,
            Func<HealthLegacyBasis[]> provider, out string reason, int order = 0)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || provider == null)
            { reason = "A nonempty owner, local ID and migration reader are required."; return false; }
            lock (Sync)
            {
                if (OrderedRegistrations.Contains(legacy, LegacyProvider.Key, ownerGuid, registrationId))
                { reason = "Duplicate health migration identity."; return false; }
                Volatile.Write(ref legacy, OrderedRegistrations.WithAdded(
                    legacy, new LegacyProvider(ownerGuid, registrationId, order, provider), LegacyProvider.Key));
            }
            reason = string.Empty; return true;
        }

        internal static bool HasDemand
        {
            get { foreach (var item in Volatile.Read(ref policies)) if (item.IsEnabled) return true; return false; }
        }
        internal static uint Evaluate(HealthModifierContext context, uint limit, out bool contributed)
        {
            contributed = false;
            // Most objects have no contribution; Calculate treats null like an empty list.
            List<ValueModifierContribution> values = null;
            foreach (var item in Volatile.Read(ref policies))
            {
                if (!item.IsEnabled) continue;
                try
                {
                    ValueModifierContribution value = item.Provider(context);
                    if (value.Kind == ValueModifierKind.FixedValue && value.Value <= 0)
                        throw new ArgumentOutOfRangeException("contribution", "An absolute live-object HP maximum must be positive.");
                    if (value.Kind != ValueModifierKind.None)
                    {
                        (values ?? (values = new List<ValueModifierContribution>())).Add(value);
                        contributed = true;
                    }
                }
                catch (Exception error)
                {
                    if (!item.ReportedFailure)
                    { item.ReportedFailure = true; HealthRuntime.Report("policy " + item.Owner + "/" + item.Id, error); }
                }
            }
            return (uint)ValueModifierCalculator.Calculate(context.OriginalMaximumHealth, values, 1, limit);
        }
        internal static LegacyProvider[] LegacyProviders => Volatile.Read(ref legacy);
        internal sealed class LegacyProvider
        {
            internal readonly string Owner, Id;
            internal readonly int Order;
            internal readonly Func<HealthLegacyBasis[]> Read;
            internal LegacyProvider(string owner,string id,int order,Func<HealthLegacyBasis[]> read)
            { Owner=owner; Id=id; Order=order; Read=read; }
            internal static readonly Func<LegacyProvider, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id, entry.Order);
        }
    }
}
