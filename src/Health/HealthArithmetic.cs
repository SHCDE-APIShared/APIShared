using System;

namespace APIShared.Health
{
    // Bounds are native-machine contracts: signed Int32 HP*100 and signed Int16 building HP.
    internal static class HealthArithmetic
    {
        internal const uint UnitMaximum = int.MaxValue / 100;
        internal const uint BuildingMaximum = (uint)short.MaxValue;
        internal static uint BuildingLimit(int wood, int stone) =>
            Math.Min(BuildingMaximum, (uint)(int.MaxValue / Math.Max(100, Math.Max(wood, stone))));
        internal static uint ScaleCurrent(uint current, uint oldMaximum, uint newMaximum)
        {
            if (current == 0 || newMaximum == 0) return 0;
            if (oldMaximum == 0) return newMaximum;
            return (uint)Math.Max(1UL, Math.Min((ulong)newMaximum,
                ((ulong)Math.Min(current, oldMaximum) * newMaximum + oldMaximum / 2UL) / oldMaximum));
        }
        internal static ushort Percentage(uint current, uint maximum) => maximum == 0 ? (ushort)0 :
            (ushort)Math.Min(100UL, ((ulong)Math.Min(current,maximum) * 100UL + maximum / 2UL) / maximum);
        internal static uint ProxyMaximum(uint unitTarget, uint unitBasis, uint proxyBasis) =>
            (uint)Math.Min(uint.MaxValue, Math.Max(1UL, ((ulong)proxyBasis * unitTarget + unitBasis / 2UL) / unitBasis));
        // Search the integer unit-HP grid, deriving the rounded proxy from exactly the same ratio.
        // This has a greatest representable result and needs no floating point square root.
        internal static uint SiegeTowerMaximum(uint requested, uint unitBasis, uint proxyBasis,
            uint proxyLimit, out uint proxyMaximum)
        {
            if (unitBasis == 0 || proxyBasis == 0 || proxyLimit == 0)
                throw new InvalidOperationException("A siege tower needs positive unit and proxy bases.");
            uint low = 1, high = Math.Min(requested, UnitMaximum), best = 0;
            while (low <= high)
            {
                uint middle = low + (high - low) / 2;
                uint proxy = ProxyMaximum(middle, unitBasis, proxyBasis);
                if (proxy <= proxyLimit && (ulong)middle * proxy <= int.MaxValue)
                { best = middle; low = middle + 1; }
                else high = middle - 1;
            }
            if (best == 0) throw new InvalidOperationException("No safe positive siege-tower HP pair exists.");
            proxyMaximum = ProxyMaximum(best,unitBasis,proxyBasis); return best;
        }
    }
}
