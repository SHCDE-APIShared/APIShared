using System;

namespace APIShared
{
    internal static class HudExtrasButtonLayout
    {
        internal const int Width = 36, Height = 34, PageSize = 5, ArrowHeight = 18;
        internal static int PageCount(int count) => count <= 0 ? 0 : 1 + (count - 1) / PageSize;
        internal static int ClampPage(int page, int count) => Math.Max(0, Math.Min(page, Math.Max(0, PageCount(count) - 1)));
        internal static int NextPage(int page, int count) => PageCount(count) <= 1 ? 0 : (ClampPage(page, count) + 1) % PageCount(count);
        internal static int BaseBottom(bool vanillaOccupied) => Height * (vanillaOccupied ? 2 : 1);
        internal static int Compare(int orderA, string ownerA, string idA, int orderB, string ownerB, string idB)
        {
            int result = orderA.CompareTo(orderB);
            if (result == 0) result = StringComparer.Ordinal.Compare(ownerA, ownerB);
            return result != 0 ? result : StringComparer.Ordinal.Compare(idA, idB);
        }
    }
}
