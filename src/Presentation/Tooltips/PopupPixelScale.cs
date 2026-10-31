using Noesis;

namespace APIShared.Presentation
{
    // Source and popup can belong to different Noesis views. Always derive absolute ratios;
    // never multiply previously applied sizes, including after viewport/layout changes.
    internal static class PopupPixelScale
    {
        internal static bool Positive(float value) => value > 0 && !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool TryGet(FrameworkElement source, FrameworkElement popup, out float x, out float y, out float sourceX)
        {
            x = y = sourceX = 0;
            Point origin = source.PointToScreen(new Point(0, 0)), unit = source.PointToScreen(new Point(100, 100));
            Point popupOrigin = popup.PointToScreen(new Point(0, 0)), popupUnit = popup.PointToScreen(new Point(100, 100));
            sourceX = unit.X - origin.X; float sourceY = unit.Y - origin.Y;
            float popupX = popupUnit.X - popupOrigin.X, popupY = popupUnit.Y - popupOrigin.Y;
            if (!Positive(sourceX) || !Positive(sourceY) || !Positive(popupX) || !Positive(popupY)) return false;
            x = sourceX / popupX; y = sourceY / popupY;
            return Positive(x) && Positive(y);
        }
    }
}
