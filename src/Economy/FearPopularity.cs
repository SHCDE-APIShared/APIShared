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
        internal sealed class Selection
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
            => SetConfiguration(registration, multiplier, null, null);

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
            SetConfiguration(registration, null, levels, null);
        }

        internal static void SetTargeted(FearPopularityRegistration registration,IReadOnlyDictionary<int,PopularityOverrideValues> values)
        {
            if(!FearPopularityRuntime.TargetedAvailable) throw new InvalidOperationException("Player-aware fear selection is unavailable.");
            var copied=new Dictionary<int,PopularityOverrideValues>();
            if(values!=null) foreach(var pair in values) {
                if(pair.Key==0) throw new ArgumentException("Fear level zero must remain neutral.",nameof(values));
                var converted=new PopularityOverrideValues(PopularityOverrides.ToUnits(pair.Value.HumanPoints),PopularityOverrides.ToUnits(pair.Value.AIPoints));
                if(converted.HumanPoints.HasValue||converted.AIPoints.HasValue)copied.Add(pair.Key,converted);
            }
            SetConfiguration(registration,null,null,copied.Count==0?null:copied);
        }
        private static Selection Select(FearPopularityRegistration changed,decimal? multiplier,Dictionary<int,int> levels,
            Dictionary<int,PopularityOverrideValues> targeted,int kind)
        {
            decimal? coefficient=null; Dictionary<int,int> selectedLevels=null;
            foreach(var item in registrations) {
                bool same=ReferenceEquals(item,changed);
                var scalar=same?multiplier:item.Multiplier; var table=same?levels:item.Contributions; var roles=same?targeted:item.Targeted;
                if(scalar.HasValue) {coefficient=scalar;selectedLevels=null;}
                else if(table!=null) {if(selectedLevels==null)selectedLevels=new Dictionary<int,int>();foreach(var pair in table)selectedLevels[pair.Key]=pair.Value;}
                else if(roles!=null&&kind!=0) foreach(var pair in roles) {
                    int? value=kind==1?pair.Value.HumanPoints:pair.Value.AIPoints;
                    if(value.HasValue){if(selectedLevels==null)selectedLevels=new Dictionary<int,int>();selectedLevels[pair.Key]=value.Value;}
                }
            }
            return coefficient.HasValue||selectedLevels!=null?new Selection(coefficient,selectedLevels):null;
        }
        private static void SetConfiguration(FearPopularityRegistration registration,decimal? multiplier,Dictionary<int,int> levels,Dictionary<int,PopularityOverrideValues> targeted)
        {
            lock(sync) {
                var legacy=Select(registration,multiplier,levels,targeted,0);
                bool hasTargeted=targeted!=null||registrations.Exists(item=>!ReferenceEquals(item,registration)&&item.Targeted!=null);
                var human=hasTargeted?Select(registration,multiplier,levels,targeted,1):legacy;
                var ai=hasTargeted?Select(registration,multiplier,levels,targeted,2):legacy;
                registration.Multiplier=multiplier; registration.Contributions=levels; registration.Targeted=targeted;
                Volatile.Write(ref selected,legacy);
                PopularitySelection.SetFear(human,ai,legacy);
            }
        }
        /// <summary>Returns the rounded, signed fear contribution in report points; midpoint ties round away from zero.</summary>
        /// <remarks>Does not read game state or require initialization. Overflow throws; no coefficient is rounded before multiplication.</remarks>
        public static int CalculatePoints(int fearLevel, decimal multiplier)
            => checked((int)decimal.Round(fearLevel * multiplier, 0, MidpointRounding.AwayFromZero));
        internal static bool TryCalculate(int fearLevel, out int internalUnits)
        {
            return TryCalculate(Volatile.Read(ref selected),fearLevel,out internalUnits);
        }
        internal static bool TryCalculate(Selection selection,int fearLevel,out int internalUnits)
        {
            internalUnits = 0;
            if (selection == null) return false;
            if (selection.Levels != null && selection.Levels.TryGetValue(fearLevel, out internalUnits)) return true;
            if (!selection.Multiplier.HasValue) return false;
            internalUnits = checked(CalculatePoints(fearLevel, selection.Multiplier.Value) * 25); return true;
        }
        internal static int MultiplyTargeted(int originalCoefficient,int fearLevel,PopularitySelection selection)
        {
            try {if(TryCalculate(selection?.Fear,fearLevel,out int value)) {FearPopularityRuntime.ReportApplied(fearLevel,value);return value;}}
            catch(OverflowException){FearPopularityRuntime.ReportOverflow();}
            return unchecked(originalCoefficient*fearLevel);
        }
        internal static int MultiplySwappedTargeted(int fearLevel,int originalCoefficient,PopularitySelection selection) => MultiplyTargeted(originalCoefficient,fearLevel,selection);
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
        internal Dictionary<int,PopularityOverrideValues> Targeted;
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
        /// <summary>Replaces this registration with independent absolute whole points for Human and AI at each signed level.</summary>
        /// <remarks>Null/empty disables; null role values reveal earlier matching policies. Copies inputs, rejects zero keys
        /// and overflowing points*25 before publication, preserving the old configuration on failure. Requires player-aware
        /// adapter availability. Replaces both legacy scalar/table and targeted configurations; old setters do the same.
        /// Keys retain the complete signed Int32 domain except zero. Unknown players see only legacy global policies.</remarks>
        public void SetTargetedLevelContributions(IReadOnlyDictionary<int,PopularityOverrideValues> contributions) => FearPopularity.SetTargeted(this,contributions);
    }
}
