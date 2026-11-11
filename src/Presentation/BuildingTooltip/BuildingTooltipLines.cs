using System.Collections.Generic;

namespace APIShared.Presentation
{
    // Value comparison of rendered tooltip rows; lets presenters skip unchanged content.
    internal static class BuildingTooltipLines
    {
        internal static bool Same(IReadOnlyList<BuildingTooltipLine> a, IReadOnlyList<BuildingTooltipLine> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Segments.Count != b[i].Segments.Count) return false;
                for (int j = 0; j < a[i].Segments.Count; j++)
                    if (a[i].Segments[j].Text != b[i].Segments[j].Text || a[i].Segments[j].Foreground != b[i].Segments[j].Foreground) return false;
            }
            return true;
        }
    }
}
