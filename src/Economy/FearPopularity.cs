using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Economy
{
    /// <summary>Process-wide fear-popularity contributions: rounded coefficients or absolute points per signed level.</summary>
    /// <remarks>Register on the Unity/library startup thread after native initialization. Handles live until process exit;
    /// use SetMultiplier(null) to become inactive. This changes popularity only, not fear level, productivity or combat.
    /// Active registrations are ordered by ascending order, ordinal owner GUID and local ID; the last applicable one wins per level.
    /// Configuration is thread-safe, but consumers own synchronized settings and mission activation.
    /// Without an active registration the complete SE multiplication is preserved, including foreign SE overrides.</remarks>
    public static class FearPopularity
    {
        private static readonly object sync = new object();
        private static readonly List<FearPopularityRegistration> registrations = new List<FearPopularityRegistration>();
        private static Selection selected;
        private sealed class Selection
        {
            internal readonly decimal? Multiplier;
            internal readonly Dictionary<int, int> Levels;
            internal Selection(decimal? multiplier, Dictionary<int, int> levels) { Multiplier = multiplier; Levels = levels; }
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
            => SetConfiguration(registration, multiplier, null);

        internal static void SetLevels(FearPopularityRegistration registration, IReadOnlyDictionary<int, int> contributions)
        {
            // Validate and detach before touching published state. These dictionaries are
            // immutable after publication; simulation/UI readers never take the write lock.
            Dictionary<int, int> levels = null;
            if (contributions != null)
            {
                levels = new Dictionary<int, int>();
                foreach (var entry in contributions)
                {
                    if (entry.Key == 0) throw new ArgumentException("Fear level zero must remain neutral.", nameof(contributions));
                    levels.Add(entry.Key, checked(entry.Value * 25));
                }
                if (levels.Count == 0) levels = null;
            }
            SetConfiguration(registration, null, levels);
        }

        private static void SetConfiguration(FearPopularityRegistration registration, decimal? multiplier, Dictionary<int, int> levels)
        {
            lock (sync)
            {
                decimal? selectedMultiplier = null;
                Dictionary<int, int> selectedLevels = null;
                foreach (var item in registrations)
                {
                    var coefficient = ReferenceEquals(item, registration) ? multiplier : item.Multiplier;
                    var table = ReferenceEquals(item, registration) ? levels : item.Contributions;
                    if (coefficient.HasValue)
                    {
                        selectedMultiplier = coefficient;
                        selectedLevels = null; // A later uniform coefficient supersedes every earlier table entry.
                    }
                    else if (table != null)
                    {
                        if (selectedLevels == null) selectedLevels = new Dictionary<int, int>();
                        foreach (var entry in table) selectedLevels[entry.Key] = entry.Value;
                    }
                }
                var next = selectedMultiplier.HasValue || selectedLevels != null ? new Selection(selectedMultiplier, selectedLevels) : null;
                registration.Multiplier = multiplier;
                registration.Contributions = levels;
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
            internalUnits = 0;
            if (selection == null) return false;
            if (selection.Levels != null && selection.Levels.TryGetValue(fearLevel, out internalUnits)) return true;
            if (!selection.Multiplier.HasValue) return false;
            internalUnits = checked(CalculatePoints(fearLevel, selection.Multiplier.Value) * 25); return true;
        }
        // The IL call has the same two-int input and int output as the replaced mul.
        // Returning SE's original product while dormant preserves its unchecked arithmetic.
        internal static int MultiplySwapped(int fearLevel, int originalCoefficient) => Multiply(originalCoefficient, fearLevel);
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
        internal Dictionary<int, int> Contributions;
        internal FearPopularityRegistration(string owner, string id, int order) { Owner=owner; Id=id; Order=order; }
        /// <summary>Sets an absolute coefficient in report points per signed fear level. Null disables this registration.</summary>
        /// <remarks>Thread-safe. Arbitrary decimal coefficients are accepted; multiplication/result overflow preserves SE simulation.
        /// Replaces this handle's complete configuration, including any level table. Consumers should choose meaningful bounds
        /// and change synchronized activation only at their supported mission boundaries.</remarks>
        public void SetMultiplier(decimal? multiplier) => FearPopularity.Set(this, multiplier);

        /// <summary>Sets absolute whole report points for exact signed fear levels. Null or an empty table disables this registration.</summary>
        /// <remarks>Thread-safe; copies the input and replaces this handle's entire configuration, including any multiplier.
        /// Level zero is rejected to keep it neutral. Other signed Int32 levels are accepted; Vanilla currently produces -5..5.
        /// Values may be negative or zero. An unrepresentable points*25 conversion throws OverflowException; level zero or
        /// duplicate entries throw ArgumentException. Validation failures preserve the previous configuration.
        /// Unspecified levels reveal earlier applicable registrations, then original SE multiplication including foreign overrides.
        /// Simulation and report correction share this selection. Consumers own synchronized settings and mission activation.</remarks>
        public void SetLevelContributions(IReadOnlyDictionary<int, int> contributions) => FearPopularity.SetLevels(this, contributions);
    }
}
