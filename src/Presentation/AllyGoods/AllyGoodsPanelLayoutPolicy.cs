using System;

namespace APIShared.Presentation
{
    // Local XAML geometry, independent of Noesis allocation/measurement. Keeping
    // both anchors separate preserves legacy consumers in an enlarged foreign root.
    internal readonly struct AllyGoodsPanelLayoutPlan
    {
        internal readonly float GoodsTop, GoodsHeight, LegacyTop, LegacyHeight, RootHeight;
        internal AllyGoodsPanelLayoutPlan(float goodsTop, float goodsHeight, float legacyTop, float legacyHeight, float rootHeight)
        { GoodsTop = goodsTop; GoodsHeight = goodsHeight; LegacyTop = legacyTop; LegacyHeight = legacyHeight; RootHeight = rootHeight; }
    }
    internal static class AllyGoodsPanelLayoutPolicy
    {
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool OwnsHeight(float actual, float applied) => Finite(actual) && Finite(applied) && Math.Abs(actual - applied) <= 0.1f;
        internal static bool ValidMarkedBounds(float rootHeight, float frameX, float frameY, float frameWidth,
            float frameHeight, float footerX, float footerY, float footerWidth, float footerHeight)
        {
            return Finite(rootHeight) && rootHeight > 0 && Finite(frameX) && Finite(frameY) &&
                Finite(frameWidth) && Finite(frameHeight) && Finite(footerX) && Finite(footerY) &&
                Finite(footerWidth) && Finite(footerHeight) && frameWidth > 0 && frameHeight > 0 &&
                footerWidth > 0 && footerHeight > 0 && frameY >= 0 && frameY + frameHeight <= rootHeight + 0.1f &&
                footerX >= frameX - 0.1f && footerY >= frameY - 0.1f &&
                footerX + footerWidth <= frameX + frameWidth + 0.1f && footerY + footerHeight <= frameY + frameHeight + 0.1f;
        }
        internal static bool TryCompose(float rootHeight, float goodsBottom, float localViewportBottom,
            float goodsDesired, bool goodsVisible, float legacyDesired, bool legacyVisible, out AllyGoodsPanelLayoutPlan result)
            => TryCompose(rootHeight, goodsBottom, localViewportBottom, goodsDesired, goodsVisible,
                legacyDesired, legacyVisible, null, out result);

        internal static bool TryCompose(float rootHeight, float goodsBottom, float localViewportBottom,
            float goodsDesired, bool goodsVisible, float legacyDesired, bool legacyVisible,
            AllyGoodsFrameOptions frame, out AllyGoodsPanelLayoutPlan result)
        {
            result = default;
            if (!Finite(rootHeight) || rootHeight <= 0 || !Finite(localViewportBottom) ||
                goodsVisible && (!Finite(goodsBottom) || goodsBottom < 0 || goodsBottom > rootHeight + 0.1f)) return false;
            float margin = frame?.ViewportMargin ?? 8;
            float overflow = frame?.VisualOverflow.Bottom ?? 0;
            float maximum = frame?.MaximumExtensionHeight ?? 260;
            float desired = !Finite(goodsDesired) || goodsDesired <= 0 ? maximum : goodsDesired;
            float available = Math.Max(0, localViewportBottom - goodsBottom - overflow - margin);
            float goodsHeight = !goodsVisible ? 0 : frame == null ? AllyGoodsContextPolicy.ExtensionHeight(goodsDesired, available) :
                Math.Max(0, Math.Min(Math.Min(desired, maximum), available));
            float goodsEnd = goodsVisible && goodsHeight > 0 ? goodsBottom + goodsHeight + overflow : 0;
            float legacyTop = Math.Max(rootHeight, goodsEnd);
            float legacyHeight = legacyVisible ? AllyGoodsContextPolicy.ExtensionHeight(legacyDesired,
                Math.Max(0, localViewportBottom - legacyTop - 8)) : 0;
            result = new AllyGoodsPanelLayoutPlan(goodsBottom, goodsHeight, legacyTop, legacyHeight,
                Math.Max(rootHeight, Math.Max(goodsEnd, legacyVisible ? legacyTop + legacyHeight : 0)));
            return true;
        }
    }
}
