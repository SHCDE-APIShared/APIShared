using System;
using System.Collections.Generic;
using APIShared.Modifiers;

namespace APIShared.Economy
{
    /// <summary>Independent Human and AI scalar contributions in whole popularity report points.</summary>
    public readonly struct PopularityModifierValues
    {
        /// <summary>Creates role-specific contributions. None leaves that role without this contribution.</summary>
        public PopularityModifierValues(ValueModifierContribution human, ValueModifierContribution ai) { Human = human; AI = ai; }
        /// <summary>Contribution for human controllers.</summary>
        public ValueModifierContribution Human { get; }
        /// <summary>Contribution for AI controllers, including allied AI.</summary>
        public ValueModifierContribution AI { get; }
    }

    internal static class PopularityModifierMath
    {
        // Multiples of 25 remain representable in the native signed Int32 contribution.
        internal const int MinimumPoints = int.MinValue / 25, MaximumPoints = int.MaxValue / 25;
        internal static int Resolve(ValueModifierPlan plan, int originalUnits) =>
            checked((int)plan.CalculateRatio(originalUnits, 25, MinimumPoints, MaximumPoints) * 25);
    }

    // Detached configuration used only when an opt-in staged fear policy exists.
    internal sealed class FearModifierRule
    {
        internal decimal? Coefficient, Factor;
        internal ValueModifierStage Stage;
        internal Dictionary<int, int> Levels;
        internal Dictionary<int, PopularityOverrideValues> Targeted;
        internal Dictionary<int, PopularityModifierValues> Modifiers;
    }

    internal sealed class FearModifierSelection
    {
        private readonly FearModifierRule[] rules;
        private readonly int kind;
        private readonly Dictionary<int, ValueModifierPlan> common = new Dictionary<int, ValueModifierPlan>();
        internal FearModifierSelection(FearModifierRule[] rules, int kind)
        {
            this.rules = rules; this.kind = kind;
            var keys = new HashSet<int>();
            for (int level = -5; level <= 5; level++) if (level != 0) keys.Add(level);
            foreach (var rule in rules)
            {
                if (rule.Levels != null) foreach (var key in rule.Levels.Keys) keys.Add(key);
                if (rule.Targeted != null) foreach (var key in rule.Targeted.Keys) keys.Add(key);
                if (rule.Modifiers != null) foreach (var key in rule.Modifiers.Keys) keys.Add(key);
            }
            foreach (int key in keys) common.Add(key, Build(key));
        }
        private ValueModifierPlan Build(int level)
        {
            var values = new List<ValueModifierContribution>(); bool staged = false;
            decimal? coefficient = null, fixedPoints = null;
            try
            {
                foreach (var rule in rules)
                {
                    if (rule.Coefficient.HasValue) { coefficient = rule.Coefficient; fixedPoints = null; }
                    else if (rule.Levels != null && rule.Levels.TryGetValue(level, out int points))
                    { fixedPoints = points / 25m; coefficient = null; }
                    else if (rule.Targeted != null && kind != 0 && rule.Targeted.TryGetValue(level, out var roles))
                    {
                        int? value = kind == 1 ? roles.HumanPoints : roles.AIPoints;
                        if (value.HasValue) { fixedPoints = value.Value / 25m; coefficient = null; }
                    }
                    if (rule.Factor.HasValue) { values.Add(ValueModifierContribution.Factor(rule.Factor.Value, rule.Stage)); staged = true; }
                    if (rule.Modifiers != null && kind != 0 && rule.Modifiers.TryGetValue(level, out var typed))
                    {
                        var value = kind == 1 ? typed.Human : typed.AI;
                        if (value.Kind == ValueModifierKind.FixedValue) { fixedPoints = value.Value; coefficient = null; staged = true; }
                        else if (value.Kind != ValueModifierKind.None) { values.Add(value); staged = true; }
                    }
                }
                // An earlier coefficient may overflow but be completely superseded by a later fixed rule.
                if (coefficient.HasValue) fixedPoints = FearPopularity.CalculatePoints(level, coefficient.Value);
                if (fixedPoints.HasValue) values.Add(ValueModifierContribution.FixedValue(fixedPoints.Value));
            }
            catch (OverflowException) { return null; } // Preserve the legacy coefficient's runtime fallback.
            return staged ? ValueModifierPlan.Create(values) : null;
        }
        internal bool TryCalculate(int level, int coefficient, out int units)
        {
            units = 0;
            if (level == 0) return true;
            if (!common.TryGetValue(level, out var plan)) plan = Build(level);
            if (plan == null) return false;
            units = checked((int)plan.CalculateRatio((long)coefficient * level, 25, PopularityModifierMath.MinimumPoints, PopularityModifierMath.MaximumPoints) * 25);
            return true;
        }
    }
}
