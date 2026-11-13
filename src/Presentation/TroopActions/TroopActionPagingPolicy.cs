using System;

namespace APIShared.Presentation
{
    // Capacities are supplied after Vanilla/foreign reservations. The first-page
    // capacity still includes a possible navigation position; later pages do not.
    internal static class TroopActionPagingPolicy
    {
        internal static TroopActionPagingDecision Decide(int actionCount, int freeFirstSlots, int freeLaterSlots)
        {
            int count = Math.Max(0, actionCount);
            int first = Math.Max(0, Math.Min(6, freeFirstSlots));
            int later = Math.Max(0, Math.Min(6, freeLaterSlots));
            bool navigation = count > first && later > 0;
            int direct = navigation ? Math.Max(0, first - 1) : Math.Min(count, first);
            return new TroopActionPagingDecision(count, direct, later, navigation, navigation && first == 0);
        }
    }

    internal readonly struct TroopActionPagingDecision
    {
        private readonly int actionCount;
        private readonly int laterCapacity;

        internal TroopActionPagingDecision(int actionCount, int directCount, int laterCapacity,
            bool hasNavigation, bool usesFallbackAnchor)
        {
            this.actionCount = actionCount;
            this.laterCapacity = laterCapacity;
            DirectCount = directCount;
            HasNavigation = hasNavigation;
            UsesFallbackAnchor = usesFallbackAnchor;
            PageCount = hasNavigation ? checked((int)(1L + ((long)actionCount - directCount + laterCapacity - 1) / laterCapacity)) : 1;
        }

        internal bool HasNavigation { get; }
        internal bool UsesFallbackAnchor { get; }
        internal int DirectCount { get; }
        internal int PageCount { get; }
        internal int ClampPage(int page) => Math.Max(0, Math.Min(PageCount - 1, page));

        internal int Move(int page, int delta)
        {
            long target = ((long)ClampPage(page) + delta) % PageCount;
            return (int)(target < 0 ? target + PageCount : target);
        }

        internal TroopActionPageSlice GetSlice(int page)
        {
            int selected = ClampPage(page);
            if (selected == 0) return new TroopActionPageSlice(0, DirectCount);
            int start = checked((int)(DirectCount + (long)(selected - 1) * laterCapacity));
            return new TroopActionPageSlice(start, Math.Min(laterCapacity, actionCount - start));
        }
    }

    internal readonly struct TroopActionPageSlice
    {
        internal TroopActionPageSlice(int start, int count) { Start = start; Count = count; }
        internal int Start { get; }
        internal int Count { get; }
    }
}
