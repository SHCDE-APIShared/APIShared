using System;

namespace APIShared.Presentation
{
    internal static class TooltipLayout
    {
        // Preserve the published building-text presentation while general content inherits its target.
        internal static float RowFontSize(TooltipLine line, float inherited) => line.LegacyBuildingStyle ? 16 : inherited;
        internal static float RowWidthLimit(TooltipLine line) => line.LegacyBuildingStyle ? 440 : float.PositiveInfinity;

        // An overlay belongs to the original HUD control, not the outer screen/modal layer.
        // The generic boundary makes the ownership rule testable without initializing native Noesis.
        internal static T FindRoot<T>(T anchor, Func<T, T> parent, Func<T, bool> isPanel, Func<T, bool> isBoundary) where T : class
        {
            T root = null;
            for (T current = parent(anchor); current != null; current = parent(current))
            {
                if (isBoundary(current)) break;
                if (isPanel(current)) root = current;
            }
            return root;
        }
    }
}
