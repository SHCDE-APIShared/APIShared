using System;
using System.Collections.Generic;
using System.Numerics;

namespace APIShared.Modifiers
{
    /// <summary>Whether a factor applies before, or explicitly after, an absolute value override.</summary>
    public enum ValueModifierStage
    {
        /// <summary>Multiply the original value; an applicable fixed value replaces this result.</summary>
        BeforeFixedValue = 0,
        /// <summary>Multiply the selected fixed value, or the ordinary result when there is no fixed value.</summary>
        AfterFixedValue = 1
    }

    /// <summary>The arithmetic contribution represented by a value modifier.</summary>
    public enum ValueModifierKind
    {
        /// <summary>No contribution.</summary>
        None = 0,
        /// <summary>A nonnegative multiplicative factor.</summary>
        Factor = 1,
        /// <summary>An absolute value; the last applicable ordered fixed contribution wins.</summary>
        FixedValue = 2
    }

    /// <summary>An immutable arithmetic contribution, expressed in the receiving service's documented units.</summary>
    /// <remarks>Services validate their own domains and own registration order, synchronization and activation.
    /// A factor is nonnegative; signed fixed values are allowed here because popularity has signed values.</remarks>
    public readonly struct ValueModifierContribution
    {
        private ValueModifierContribution(ValueModifierKind kind, decimal value, ValueModifierStage stage)
        { Kind = kind; Value = value; Stage = stage; }
        /// <summary>Contribution operation.</summary>
        public ValueModifierKind Kind { get; }
        /// <summary>Factor or absolute value in the receiving domain's units.</summary>
        public decimal Value { get; }
        /// <summary>Factor stage; fixed values always occupy the intervening fixed stage.</summary>
        public ValueModifierStage Stage { get; }
        /// <summary>No contribution; equivalent to the default value.</summary>
        public static ValueModifierContribution None => default;
        /// <summary>Creates a nonnegative factor. Negative factors or undefined stages throw before publication.</summary>
        public static ValueModifierContribution Factor(decimal factor, ValueModifierStage stage = ValueModifierStage.BeforeFixedValue)
        {
            if (factor < 0) throw new ArgumentOutOfRangeException(nameof(factor));
            if (stage != ValueModifierStage.BeforeFixedValue && stage != ValueModifierStage.AfterFixedValue)
                throw new ArgumentOutOfRangeException(nameof(stage));
            return new ValueModifierContribution(ValueModifierKind.Factor, factor, stage);
        }
        /// <summary>Creates a fixed value. Later factors affect it only when explicitly marked AfterFixedValue.</summary>
        public static ValueModifierContribution FixedValue(decimal value) =>
            new ValueModifierContribution(ValueModifierKind.FixedValue, value, ValueModifierStage.BeforeFixedValue);
    }

    /// <summary>Pure deterministic composition shared by scalar gameplay services.</summary>
    public static class ValueModifierCalculator
    {
        /// <summary>Composes ordered contributions, rounds once away from zero at midpoint, then clamps to inclusive integer bounds.</summary>
        /// <remarks>All BeforeFixedValue factors multiply the baseline. The last supplied fixed value replaces that
        /// result; all AfterFixedValue factors follow it. Decimal factors are evaluated as exact rational numbers,
        /// including overflowing intermediate products followed by reciprocal factors. Null means no contributions.
        /// Bounds must be integral and ordered. No game state, registration or networking is accessed.</remarks>
        public static decimal Calculate(decimal baseline, IEnumerable<ValueModifierContribution> orderedContributions,
            decimal minimumValue, decimal maximumValue) =>
            ValueModifierPlan.Create(orderedContributions).Calculate(baseline, minimumValue, maximumValue);
    }

    // Registries compile immutable plans on rare configuration writes, rather than multiplying factors in hot readers.
    internal sealed class ValueModifierPlan
    {
        private readonly Rational multiplier;
        private readonly bool hasFixed;
        private readonly bool small;
        private readonly long numerator, denominator;
        internal readonly bool HasContributions;
        private ValueModifierPlan(Rational multiplier, bool hasFixed, bool active)
        {
            this.multiplier = multiplier; this.hasFixed = hasFixed; HasContributions = active;
            small = multiplier.Numerator >= long.MinValue && multiplier.Numerator <= long.MaxValue && multiplier.Denominator <= long.MaxValue;
            if (small) { numerator = (long)multiplier.Numerator; denominator = (long)multiplier.Denominator; }
        }
        internal static ValueModifierPlan Create(IEnumerable<ValueModifierContribution> contributions)
        {
            var before = Rational.One; var after = Rational.One; var fixedValue = Rational.One;
            bool hasFixed = false, active = false;
            if (contributions != null) foreach (var item in contributions)
            {
                if (item.Kind == ValueModifierKind.None) continue;
                active = true;
                if (item.Kind == ValueModifierKind.FixedValue) { fixedValue = Rational.FromDecimal(item.Value); hasFixed = true; }
                else if (item.Stage == ValueModifierStage.AfterFixedValue) after = after.Multiply(Rational.FromDecimal(item.Value));
                else before = before.Multiply(Rational.FromDecimal(item.Value));
            }
            return new ValueModifierPlan((hasFixed ? fixedValue : before).Multiply(after), hasFixed, active);
        }
        internal decimal Calculate(decimal baseline, decimal minimum, decimal maximum)
        {
            if (minimum > maximum || decimal.Truncate(minimum) != minimum || decimal.Truncate(maximum) != maximum)
                throw new ArgumentException("Inclusive modifier bounds must be ordered integers.");
            if (small && (hasFixed || (baseline >= long.MinValue && baseline <= long.MaxValue && decimal.Truncate(baseline) == baseline)))
            {
                try
                {
                    long product = hasFixed ? numerator : checked((long)baseline * numerator);
                    return RoundSmall(product, denominator, minimum, maximum);
                }
                catch (OverflowException) { } // Exact arbitrary-precision path; never saturate an intermediate.
            }
            Rational result = hasFixed ? multiplier : Rational.FromDecimal(baseline).Multiply(multiplier);
            return Round(result, minimum, maximum);
        }
        // Cold compilers need the reduced exact value before any receiving-domain rounding
        // or bounds check. Keep composition identical to Calculate; never round factors apart.
        internal void GetExactRatio(decimal baseline, out BigInteger resultNumerator, out BigInteger resultDenominator)
        {
            Rational result = hasFixed ? multiplier : Rational.FromDecimal(baseline).Multiply(multiplier);
            resultNumerator = result.Numerator;
            resultDenominator = result.Denominator;
        }
        // Popularity baselines use native integer units / 25. Keep that exact ratio allocation-free too.
        internal decimal CalculateRatio(long baselineNumerator, long baselineDenominator, decimal minimum, decimal maximum)
        {
            if (baselineDenominator <= 0) throw new ArgumentOutOfRangeException(nameof(baselineDenominator));
            if (minimum > maximum || decimal.Truncate(minimum) != minimum || decimal.Truncate(maximum) != maximum)
                throw new ArgumentException("Inclusive modifier bounds must be ordered integers.");
            if (small) try
            {
                return RoundSmall(hasFixed ? numerator : checked(baselineNumerator * numerator),
                    hasFixed ? denominator : checked(baselineDenominator * denominator), minimum, maximum);
            }
            catch (OverflowException) { }
            return Round(hasFixed ? multiplier : new Rational(baselineNumerator, baselineDenominator).Multiply(multiplier), minimum, maximum);
        }
        private static decimal RoundSmall(long product, long divisor, decimal minimum, decimal maximum)
        {
            long quotient = product / divisor, remainder = product % divisor;
            // Unsigned magnitude also handles Int64.MinValue without negation overflow.
            ulong magnitude = remainder < 0 ? (ulong)(-(remainder + 1)) + 1UL : (ulong)remainder;
            if (magnitude >= ((ulong)divisor + 1UL) / 2UL)
                quotient = checked(quotient + (product < 0 ? -1 : 1));
            return Math.Max(minimum, Math.Min(maximum, quotient));
        }
        private static decimal Round(Rational result, decimal minimum, decimal maximum)
        {
            BigInteger rounded = BigInteger.DivRem(result.Numerator, result.Denominator, out BigInteger rest);
            if (BigInteger.Abs(rest) * 2 >= result.Denominator) rounded += result.Numerator.Sign;
            if (rounded < new BigInteger(minimum)) return minimum;
            if (rounded > new BigInteger(maximum)) return maximum;
            return (decimal)rounded;
        }
        private readonly struct Rational
        {
            internal readonly BigInteger Numerator, Denominator;
            internal Rational(BigInteger numerator, BigInteger denominator)
            {
                BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
                Numerator = numerator / divisor; Denominator = denominator / divisor;
            }
            internal static Rational One => new Rational(BigInteger.One, BigInteger.One);
            internal static Rational FromDecimal(decimal value)
            {
                int[] bits = decimal.GetBits(value);
                BigInteger number = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
                if (bits[3] < 0) number = -number;
                return new Rational(number, BigInteger.Pow(10, (bits[3] >> 16) & 0xFF));
            }
            internal Rational Multiply(Rational other)
            {
                BigInteger left = BigInteger.GreatestCommonDivisor(BigInteger.Abs(Numerator), other.Denominator);
                BigInteger right = BigInteger.GreatestCommonDivisor(BigInteger.Abs(other.Numerator), Denominator);
                return new Rational((Numerator / left) * (other.Numerator / right), (Denominator / right) * (other.Denominator / left));
            }
        }
    }

    // A callback's legacy property writes form fixed contributions; typed factors are lazily tracked.
    // Old callbacks do not allocate a contribution list, and a failed callback rolls back its whole delta.
    internal struct StagedInt32Value
    {
        private readonly int original;
        private int current;
        private bool hasFixed;
        private List<ValueModifierContribution> contributions;
        internal StagedInt32Value(int value) { original = current = value; hasFixed = false; contributions = null; }
        internal int Current { get => current; set { current = value; hasFixed = true; contributions?.Add(ValueModifierContribution.FixedValue(value)); } }
        internal void Contribute(ValueModifierContribution value)
        {
            if (value.Kind == ValueModifierKind.None) return;
            if (contributions == null)
            {
                contributions = new List<ValueModifierContribution>();
                if (hasFixed) contributions.Add(ValueModifierContribution.FixedValue(current));
            }
            contributions.Add(value);
        }
        internal int Resolve() => contributions == null ? current : (int)ValueModifierCalculator.Calculate(original, contributions, int.MinValue, int.MaxValue);
        internal Checkpoint Capture() => new Checkpoint(current, hasFixed, contributions == null ? -1 : contributions.Count);
        internal void Restore(Checkpoint checkpoint)
        {
            current = checkpoint.Current; hasFixed = checkpoint.HasFixed;
            if (checkpoint.Count < 0) contributions = null;
            else if (contributions.Count > checkpoint.Count) contributions.RemoveRange(checkpoint.Count, contributions.Count - checkpoint.Count);
        }
        internal readonly struct Checkpoint
        {
            internal readonly int Current, Count; internal readonly bool HasFixed;
            internal Checkpoint(int current, bool hasFixed, int count) { Current = current; HasFixed = hasFixed; Count = count; }
        }
    }
}
