using System;
using System.Collections.Generic;
using APIShared.Modifiers;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    /// <summary>One provider's detached result from the last completed base-price reconciliation.</summary>
    public readonly struct MarketPriceContributionStatus
    {
        internal MarketPriceContributionStatus(string owner, string id, int order,
            ValueModifierContribution contribution, bool failed, bool superseded)
        { OwnerGuid = owner; RegistrationId = id; Order = order; Contribution = contribution; ProviderFailed = failed; SupersededByFixedValue = superseded; }
        /// <summary>Provider's ordinal owner GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Provider's ordinal owner-local registration ID.</summary>
        public string RegistrationId { get; }
        /// <summary>Registration priority, followed by owner GUID and registration ID.</summary>
        public int Order { get; }
        /// <summary>Exact returned value and stage; None also represents an isolated failed provider.</summary>
        public ValueModifierContribution Contribution { get; }
        /// <summary>True when this provider threw and supplied no contribution to this projection.</summary>
        public bool ProviderFailed { get; }
        /// <summary>True when a selected later fixed value or foreign absolute value replaces this
        /// Before factor or fixed value. After factors are never superseded by a fixed value.</summary>
        public bool SupersededByFixedValue { get; }
    }

    /// <summary>Immutable explanation of a reconciled market base price and a separately sampled shared table cell.</summary>
    /// <remarks>Prices are per native lot of five goods, not transaction totals. A cached AI projection
    /// describes the existing AI helper route, not an observed per-player table or a promised trade result.
    /// Later refreshes, registrations and missions cannot change this result. Legacy total vetoes and
    /// native quantity arithmetic remain outside this explanation.</remarks>
    public sealed class MarketPriceCompositionStatus
    {
        internal MarketPriceCompositionStatus(eGoods good, MarketPriceDirection direction, bool ai, int baseline,
            int composed, int tableComposed, int observed, int? foreign, bool bounded, int maximum,
            bool clamped, MarketPriceContributionStatus[] contributions)
        {
            Good = good; Direction = direction; IsAIPlayer = ai; OriginalBasePrice = baseline;
            ComposedBasePrice = composed; LastComposedTableBasePrice = tableComposed; ObservedTableBasePrice = observed;
            ForeignBasePrice = foreign; UsesSafetyBounds = bounded; MaximumBasePrice = maximum; WasClamped = clamped;
            Contributions = Array.AsReadOnly(contributions);
        }
        /// <summary>Native goods type of this cell.</summary>
        public eGoods Good { get; }
        /// <summary>Buy or sell column.</summary>
        public MarketPriceDirection Direction { get; }
        /// <summary>True for the cached AI helper projection; false for the shared current-table projection.</summary>
        public bool IsAIPlayer { get; }
        /// <summary>Saved/default baseline used by the last completed reconciliation.</summary>
        public int OriginalBasePrice { get; }
        /// <summary>Last completed composed result for the requested projection.</summary>
        public int ComposedBasePrice { get; }
        /// <summary>Shared-table result of that reconciliation, before any temporary AI helper substitution.</summary>
        public int LastComposedTableBasePrice { get; }
        /// <summary>Current shared table cell sampled under the engine monitor by the query. This may
        /// differ from the cached composition; querying does not reconcile or acquire ownership of it.
        /// A reentrant query inside a native AI helper may observe its temporary substitution.</summary>
        public int ObservedTableBasePrice { get; }
        /// <summary>Whether the observed table differs from the last composed shared result. A later
        /// direct writer may explain the difference; this query does not classify or adopt that write.</summary>
        public bool HasUnreconciledTableChange => ObservedTableBasePrice != LastComposedTableBasePrice;
        /// <summary>Previously reconciled foreign absolute input, if any. It occupies the final fixed
        /// stage after registered fixed values; only explicit After factors can modify it.</summary>
        public int? ForeignBasePrice { get; }
        /// <summary>True when the service used its final safety bounds. Unmodified baseline/foreign
        /// passthrough retains legacy behavior and does not use these bounds.</summary>
        public bool UsesSafetyBounds { get; }
        /// <summary>Inclusive composed lower bound, applied only when UsesSafetyBounds is true.</summary>
        public int MinimumBasePrice => 0;
        /// <summary>Inclusive direction-specific upper bound, applied only when UsesSafetyBounds is true.</summary>
        public int MaximumBasePrice { get; }
        /// <summary>True only when final rounding produced a value outside the stated safety bounds.
        /// Composition uses exact rational arithmetic and one midpoint-away-from-zero rounding.</summary>
        public bool WasClamped { get; }
        /// <summary>Detached provider results in order/ordinal owner GUID/registration ID order,
        /// including None and isolated failures. Stage determines arithmetic placement independently
        /// of registration position. No provider delegate or mutable registration is exposed.</summary>
        public IReadOnlyList<MarketPriceContributionStatus> Contributions { get; }
    }

    // One buffer per table cell and projection. Refresh overwrites values without allocating a new
    // trace or result list; capacity grows only when the enabled registration count grows. Readers
    // copy under the existing engine monitor, so externally retained results never alias this buffer.
    internal sealed class MarketPriceCompositionTrace
    {
        private struct Entry
        {
            internal MarketPriceModifierHandle Provider;
            internal ValueModifierContribution Value;
            internal bool Failed;
        }
        private Entry[] entries = Array.Empty<Entry>();
        private int count, baseline, maximum;
        private int? foreign;
        private bool bounded;
        internal bool Complete { get; private set; }
        internal int Price { get; private set; }
        internal void Begin(int capacity)
        {
            Complete = false; count = 0;
            if (entries.Length < capacity) entries = new Entry[capacity];
        }
        internal void Add(MarketPriceModifierHandle provider, ValueModifierContribution value, bool failed)
        { entries[count++] = new Entry { Provider = provider, Value = value, Failed = failed }; }
        internal void Finish(int baseline, int? foreign, MarketPriceResolution result, bool bounded, int maximum)
        {
            this.baseline = baseline; this.foreign = foreign; this.bounded = bounded; this.maximum = maximum;
            Price = result.Price; Complete = true;
        }
        internal MarketPriceCompositionStatus Snapshot(int good, bool buying, bool ai, int observed, int tablePrice)
        {
            var rows = new MarketPriceContributionStatus[count];
            var values = new List<ValueModifierContribution>(count + 1);
            int lastFixed = -1;
            for (int i = 0; i < count; i++)
                if (entries[i].Value.Kind == ValueModifierKind.FixedValue) lastFixed = i;
            for (int i = 0; i < count; i++)
            {
                var entry = entries[i]; var value = entry.Value;
                bool superseded = value.Kind == ValueModifierKind.FixedValue
                    ? foreign.HasValue || i != lastFixed
                    : value.Kind == ValueModifierKind.Factor && value.Stage == ValueModifierStage.BeforeFixedValue &&
                        (foreign.HasValue || lastFixed >= 0);
                rows[i] = new MarketPriceContributionStatus(entry.Provider.Owner, entry.Provider.Id,
                    entry.Provider.Order, value, entry.Failed, superseded);
                if (!entry.Failed && value.Kind != ValueModifierKind.None) values.Add(value);
            }
            if (foreign.HasValue) values.Add(ValueModifierContribution.FixedValue(foreign.Value));
            // This cold calculation uses only captured immutable values, never provider callbacks.
            // Decimal saturation still distinguishes either small service bound for arbitrarily
            // large exact results; no saturated number is exposed as the unclamped exact result.
            decimal rounded = bounded ? ValueModifierCalculator.Calculate(baseline, values, decimal.MinValue, decimal.MaxValue) : Price;
            return new MarketPriceCompositionStatus((eGoods)good,
                buying ? MarketPriceDirection.Buy : MarketPriceDirection.Sell, ai, baseline, Price, tablePrice,
                observed, foreign, bounded, maximum, bounded && (rounded < 0 || rounded > maximum), rows);
        }
    }
}
