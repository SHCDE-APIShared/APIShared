using System;

namespace APIShared.Presentation
{
    // Decisions apply after filtering inactive, hidden and failed entries and reserving
    // Vanilla/foreign controls. The established host contract exposes at most two slots.
    internal static class TroopActionOverflowPolicy
    {
        // A fallback opener is above the troop panel. Keep it reachable while the menu is open.
        internal static float MenuBottom(float panelTop, float openerTop) => Math.Min(panelTop, openerTop) - 4;

        internal static TroopActionOverflowDecision Decide(int actionCount, int freeSlots)
        {
            int count = Math.Max(0, actionCount);
            int slots = Math.Max(0, Math.Min(2, freeSlots));
            bool more = count > slots;
            int direct = more ? Math.Max(0, slots - 1) : count;
            return new TroopActionOverflowDecision(direct, count - direct, more, more && slots == 0);
        }
    }

    internal readonly struct TroopActionOverflowDecision
    {
        internal TroopActionOverflowDecision(int directCount, int overflowCount, bool hasMore, bool usesFallbackAnchor)
        {
            DirectCount = directCount;
            OverflowCount = overflowCount;
            HasMore = hasMore;
            UsesFallbackAnchor = usesFallbackAnchor;
        }

        internal int DirectCount { get; }
        internal int OverflowStart => DirectCount;
        internal int OverflowCount { get; }
        internal bool HasMore { get; }
        internal bool UsesFallbackAnchor { get; }
    }
}
