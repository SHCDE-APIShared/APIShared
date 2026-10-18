using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Economy
{
    /// <summary>Process-wide absolute fear-popularity coefficients, rounded after multiplication.</summary>
    /// <remarks>Register on the Unity/library startup thread after native initialization. Handles live until process exit;
    /// use SetMultiplier(null) to become inactive. This changes popularity only, not fear level, productivity or combat.
    /// Active registrations are ordered by ascending order, ordinal owner GUID and local ID; the last wins.
    /// Setting a coefficient is thread-safe, but consumers own synchronized settings and mission activation.
    /// Without an active registration the complete SE multiplication is preserved, including foreign SE overrides.</remarks>
    public static class FearPopularity
    {
        private static readonly object sync = new object();
        private static readonly List<FearPopularityRegistration> registrations = new List<FearPopularityRegistration>();
        private static Selection selected;
        private sealed class Selection
        {
            internal readonly decimal Multiplier;
            internal Selection(decimal multiplier) { Multiplier = multiplier; }
        }
        /// <summary>Registers an initially inactive coefficient. Duplicate owner/ID pairs are rejected; no unregister is needed.</summary>
        /// <remarks>Returns false with a diagnostic if the audited native/SE calculation is unavailable. Registration never changes game values.</remarks>
        public static bool TryRegister(string ownerGuid, string registrationId, out FearPopularityRegistration registration,
            out string diagnostic, int order = 0)
        {
            registration = null;
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId))
            { diagnostic = "Owner GUID and local registration ID are required."; return false; }
            if (!FearPopularityRuntime.Available)
            { diagnostic = "Rounded fear popularity is unavailable: " + FearPopularityRuntime.Diagnostic; return false; }
            lock (sync)
            {
                foreach (var existing in registrations)
                    if (existing.Owner == ownerGuid && existing.Id == registrationId)
                    { diagnostic = "Duplicate owner/registration ID."; return false; }
                registration = new FearPopularityRegistration(ownerGuid, registrationId, order);
                registrations.Add(registration);
                registrations.Sort((a,b) => {
                    int result = a.Order.CompareTo(b.Order);
                    if (result == 0) result = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
                    return result != 0 ? result : StringComparer.Ordinal.Compare(a.Id, b.Id);
                });
            }
            diagnostic = null; return true;
        }
        internal static void Set(FearPopularityRegistration registration, decimal? multiplier)
        {
            lock (sync)
            {
                registration.Multiplier = multiplier;
                Selection next = null;
                foreach (var item in registrations)
                    if (item.Multiplier.HasValue) next = new Selection(item.Multiplier.Value);
                Volatile.Write(ref selected, next);
            }
        }
        /// <summary>Returns the rounded, signed fear contribution in report points; midpoint ties round away from zero.</summary>
        /// <remarks>Does not read game state or require initialization. Overflow throws; no coefficient is rounded before multiplication.</remarks>
        public static int CalculatePoints(int fearLevel, decimal multiplier)
            => checked((int)decimal.Round(fearLevel * multiplier, 0, MidpointRounding.AwayFromZero));
        internal static bool HasActive => Volatile.Read(ref selected) != null;
        internal static bool TryCalculate(int fearLevel, out int internalUnits)
        {
            var selection = Volatile.Read(ref selected);
            if (selection == null) { internalUnits = 0; return false; }
            internalUnits = checked(CalculatePoints(fearLevel, selection.Multiplier) * 25); return true;
        }
        // The IL call has the same two-int input and int output as the replaced mul.
        // Returning SE's original product while dormant preserves its unchecked arithmetic.
        internal static int Multiply(int originalCoefficient, int fearLevel)
        {
            try { if (TryCalculate(fearLevel, out int value)) { FearPopularityRuntime.ReportApplied(fearLevel, value); return value; } }
            catch (OverflowException) { FearPopularityRuntime.ReportOverflow(); }
            return unchecked(originalCoefficient * fearLevel);
        }
    }

    /// <summary>A process-lifetime owner-bound fear-popularity contribution; retained by APIShared.</summary>
    public sealed class FearPopularityRegistration
    {
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal decimal? Multiplier;
        internal FearPopularityRegistration(string owner, string id, int order) { Owner=owner; Id=id; Order=order; }
        /// <summary>Sets an absolute coefficient in report points per signed fear level. Null disables this registration.</summary>
        /// <remarks>Thread-safe. Arbitrary decimal coefficients are accepted; multiplication/result overflow preserves SE simulation.
        /// Consumers should choose meaningful bounds and change synchronized activation only at their supported mission boundaries.</remarks>
        public void SetMultiplier(decimal? multiplier) => FearPopularity.Set(this, multiplier);
    }
}
