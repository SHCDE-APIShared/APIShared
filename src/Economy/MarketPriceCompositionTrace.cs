using System;
using System.Collections.Generic;
using APIShared.Modifiers;

namespace APIShared.Economy
{
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
        internal ValueModifierStatus Snapshot(int observed, int tablePrice)
        {
            var rows = new ValueModifierContributionStatus[count];
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
                rows[i] = new ValueModifierContributionStatus(entry.Provider.Owner, entry.Provider.Id,
                    entry.Provider.Order, value, entry.Failed, superseded);
                if (!entry.Failed && value.Kind != ValueModifierKind.None) values.Add(value);
            }
            if (foreign.HasValue) values.Add(ValueModifierContribution.FixedValue(foreign.Value));
            // This cold calculation uses only captured immutable values, never provider callbacks.
            // Decimal saturation still distinguishes either small service bound for arbitrarily
            // large exact results; no saturated number is exposed as the unclamped exact result.
            ValueModifierBounds? bounds = null;
            if (bounded)
            {
                decimal rounded = ValueModifierCalculator.Calculate(baseline, values, decimal.MinValue, decimal.MaxValue);
                bounds = new ValueModifierBounds(0, maximum, rounded < 0 || rounded > maximum);
            }
            return new ValueModifierStatus(baseline, Price, foreign, bounds,
                new ValueModifierObservation(observed, tablePrice), rows);
        }
    }
}
