using System;
using Noesis;

namespace APIShared.Presentation
{
    public static partial class AllyGoodsPanelExtensions
    {
        private static void ApplyFooterLayout()
        {
            if (footerLayoutBusy || footerHost == null || !footerSend) return;
            footerLayoutBusy = true;
            try
            {
                float width = footerOwner.ActualWidth, height = footerOwner.ActualHeight;
                if (!ReferenceEquals(footerOwner, footerAnchor) || footerOwner.Parent == null || !footerOwner.IsVisible ||
                    !TryGetGoodsBottom(out _, out _))
                { HideFooter(); return; }
                if (!AllyGoodsFooterPolicy.HasStrip(width, height) || footerVisuals.Count == 0)
                { HideFooter(); return; }
                for (int i = 0; i < footerOwner.Children.Count; i++)
                {
                    var child = footerOwner.Children[i];
                    if (ReferenceEquals(child, footerHost) || child.Visibility != Visibility.Visible ||
                        AllyGoodsFooterPolicy.IsMarkedBackground(child, footerBackground)) continue;
                    if (!(child is FrameworkElement element) || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                    { HideFooter(); return; }
                    var bounds = element.TransformToAncestor(footerOwner).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    if (AllyGoodsFooterPolicy.Overlaps(bounds.X, bounds.Y, bounds.Width, bounds.Height, width, height))
                    { HideFooter(); Report("footer-occupied", new InvalidOperationException("The goods-footer lower strip contains foreign content; footer controls are suspended.")); return; }
                }
                float available = width - AllyGoodsFooterPolicy.Inset * 2;
                bool measured = footerHost.ActualWidth > 0;
                float total = 0;
                bool any = false;
                foreach (var visual in footerVisuals)
                {
                    bool visible = visual.Element.Visibility != Visibility.Collapsed;
                    SetLayoutVisibility(visual.Container, visible ? Visibility.Visible : Visibility.Collapsed);
                    if (!visible) continue;
                    any = true;
                    if (!footerOverflowing)
                    {
                        float desired = visual.Container.DesiredSize.Width;
                        measured &= desired > 0 && AllyGoodsFooterPolicy.Finite(desired);
                        if (desired > 0) visual.InlineWidth = desired;
                    }
                    total += visual.InlineWidth;
                }
                if (!any) { HideFooter(); return; }
                if (footerProbe && !measured) { HideFooter(); return; }
                footerProbe = false;
                bool overflow = !AllyGoodsFooterPolicy.Fits(total, available);
                // Returning from overflow requires a normal attached inline layout pass.
                if (footerOverflowing && !overflow)
                { footerProbe = true; SetFooterMode(false); HideFooter(); return; }
                SetFooterMode(overflow);
                SetLayoutMaxWidth(footerScroll, Math.Min(360, available));
                float popupHeight = 240;
                if (footerViewport != null && footerViewport.ActualHeight > 0)
                {
                    var unit = footerOwner.TransformToAncestor(footerViewport).TransformBounds(new Rect(0, 0, 1, 1));
                    if (unit.Height > 0) popupHeight = Math.Max(18, Math.Min(240, footerViewport.ActualHeight / unit.Height - 32));
                }
                SetLayoutMaxHeight(footerScroll, popupHeight);
                SetLayoutVisibility(footerHost, Visibility.Visible);
                ApplyFooterPopupScale();
            }
            finally { footerLayoutBusy = false; }
        }
    }
}
