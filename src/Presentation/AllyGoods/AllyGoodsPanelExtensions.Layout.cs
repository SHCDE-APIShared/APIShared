using System;
using System.Collections.Generic;
using Noesis;

namespace APIShared.Presentation
{
    public static partial class AllyGoodsPanelExtensions
    {
        private static Grid anchoredHost, footerAnchor;
        private static FrameworkElement goodsFrame, footerBackground;
        private static StackPanel anchoredSections;
        private static Border anchoredSurface;
        private static bool markedResolved;
        private static string markedFailure;
        private static readonly Action<Exception> layoutObserverFailed = error => Report("layout-observer", error);

        private static void SetSectionAvailability(bool available, string reason)
        {
            foreach (var entry in registry.Entries) entry.SetLayoutAvailability(available, reason, layoutObserverFailed);
        }
        private static bool TryResolveMarkedLayout(out string reason)
        {
            reason = markedFailure;
            if (markedResolved) return reason == null;
            if (layout == null) { reason = "The goods root is unavailable."; return false; }
            markedResolved = true;
            int frames = 0, footers = 0, backgrounds = 0;
            FindMarkedAnchors(layout, ref frames, ref footers, ref backgrounds);
            if (frames != 1 || footers != 1 || goodsFrame == null || footerAnchor == null || backgrounds > 1 ||
                footerBackground != null && !ReferenceEquals(footerBackground.Parent, footerAnchor))
            {
                goodsFrame = footerBackground = null; footerAnchor = null;
                markedFailure = "The goods-frame and footer roles must each identify one compatible element.";
                Report("marked-layout", new InvalidOperationException(markedFailure));
            }
            reason = markedFailure; return reason == null;
        }
        private static void FindMarkedAnchors(DependencyObject element, ref int frames, ref int footers, ref int backgrounds)
        {
            var role = AllyGoodsPanelAnchors.GetRole(element);
            if (role == AllyGoodsPanelAnchorRole.GoodsFrame)
            { frames++; goodsFrame = element as FrameworkElement; }
            else if (role == AllyGoodsPanelAnchorRole.Footer)
            { footers++; footerAnchor = element as Grid; }
            else if (role == AllyGoodsPanelAnchorRole.FooterBackground)
            { backgrounds++; footerBackground = element as FrameworkElement; }
            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int index = 0; index < count; index++)
                FindMarkedAnchors(VisualTreeHelper.GetChild(element, index), ref frames, ref footers, ref backgrounds);
        }
        private static bool TryGetGoodsBottom(out float bottom, out string reason)
        {
            bottom = 0;
            if (!TryResolveMarkedLayout(out reason)) return false;
            if (!goodsFrame.IsVisible || !footerAnchor.IsVisible || goodsFrame.ActualWidth <= 0 ||
                goodsFrame.ActualHeight <= 0 || footerAnchor.ActualHeight <= 0)
            { reason = "The goods layout has not completed a visible layout pass."; return false; }
            var frame = goodsFrame.TransformToAncestor(layout).TransformBounds(new Rect(0, 0,
                (float)goodsFrame.ActualWidth, (float)goodsFrame.ActualHeight));
            var footer = footerAnchor.TransformToAncestor(layout).TransformBounds(new Rect(0, 0,
                (float)footerAnchor.ActualWidth, (float)footerAnchor.ActualHeight));
            bottom = (float)(frame.Y + frame.Height);
            if (!AllyGoodsPanelLayoutPolicy.ValidMarkedBounds(baseHeight, (float)frame.X, (float)frame.Y,
                (float)frame.Width, (float)frame.Height, (float)footer.X, (float)footer.Y, (float)footer.Width, (float)footer.Height))
            { bottom = 0; reason = "The marked goods frame/footer geometry is incompatible with the original root."; return false; }
            reason = string.Empty; return true;
        }
        private static void EnsureAnchoredSurface()
        {
            if (anchoredHost != null) return;
            anchoredSections = new StackPanel { Orientation = Orientation.Vertical };
            anchoredSurface = new Border { Background = new SolidColorBrush(Color.FromArgb(255, 29, 23, 16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 242, 212, 138)), BorderThickness = new Thickness(1),
                Padding = new Thickness(10), Child = new ScrollViewer { Content = anchoredSections,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            standardAnchoredBackground = anchoredSurface.Background; standardAnchoredBorder = anchoredSurface.BorderBrush;
            standardAnchoredPadding = anchoredSurface.Padding; standardAnchoredBorderThickness = anchoredSurface.BorderThickness;
            anchoredHost = new Grid { Height = 0, Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
            anchoredHost.Children.Add(anchoredSurface); layout.Children.Add(anchoredHost);
        }
        private static void RebuildSections(AllyGoodsPanelContext context)
        {
            dirty = false;
            bool anchored = false;
            foreach (var entry in registry.Entries)
                if (entry.Placement == AllyGoodsSectionPlacement.GoodsDialogBottom) { anchored = true; break; }
            if (anchored) EnsureAnchoredSurface();
            ReconcileSections(sections, AllyGoodsSectionPlacement.PanelRootBottom, context);
            if (anchoredSections != null) ReconcileSections(anchoredSections, AllyGoodsSectionPlacement.GoodsDialogBottom, context);
        }
        private static void ReconcileSections(StackPanel panel, AllyGoodsSectionPlacement placement, AllyGoodsPanelContext context)
        {
            var desired = new List<ContentControl>();
            foreach (var entry in registry.Entries)
            {
                if (!entry.Visible || entry.Placement != placement) continue;
                if (!visuals.TryGetValue(entry, out var visual) || visual.Revision != entry.Revision)
                {
                    if (visual?.Container?.Parent is StackPanel previous) previous.Children.Remove(visual.Container);
                    visual = new Visual(entry.Revision); visuals[entry] = visual;
                    if (TryCreate(entry, new AllyGoodsSectionContext(placement == AllyGoodsSectionPlacement.PanelRootBottom ? host : anchoredHost, context), out var element))
                        visual.Container = new ContentControl { Content = element };
                }
                if (visual.Container == null) continue;
                if (visual.Container.Parent != null && !ReferenceEquals(visual.Container.Parent, panel))
                { Report("section-parent:" + entry.Owner + "/" + entry.Id, new InvalidOperationException("Section control is already attached elsewhere.")); continue; }
                visual.Container.IsEnabled = entry.Enabled;
                float spacing = placement == AllyGoodsSectionPlacement.GoodsDialogBottom ? surfaceFrameOptions?.SectionSpacing ?? 8 : 8;
                visual.Container.Margin = new Thickness(0, desired.Count == 0 ? 0 : spacing, 0, 0);
                desired.Add(visual.Container);
            }
            // Preserve unchanged controls/focus. Hiding one owner never unloads every
            // unrelated provider merely to reconstruct the same ordered children.
            for (int index = panel.Children.Count - 1; index >= 0; index--)
                if (!(panel.Children[index] is ContentControl child) || !desired.Contains(child)) panel.Children.RemoveAt(index);
            for (int index = 0; index < desired.Count; index++)
            {
                var child = desired[index];
                if (index < panel.Children.Count && ReferenceEquals(panel.Children[index], child)) continue;
                if (ReferenceEquals(child.Parent, panel)) panel.Children.Remove(child);
                panel.Children.Insert(index, child);
            }
        }
        private static float LocalViewportBottom()
        {
            if (viewport == null || viewport.ActualHeight <= 0) return 0;
            var bounds = layout.TransformToAncestor(viewport).TransformBounds(new Rect(0, 0, 1, 1));
            if (!AllyGoodsPanelLayoutPolicy.Finite((float)bounds.Height) || bounds.Height <= 0) return 0;
            float value = ((float)viewport.ActualHeight - (float)bounds.Y) / (float)bounds.Height;
            return AllyGoodsPanelLayoutPolicy.Finite(value) ? value : 0;
        }
        private static float DesiredSectionHeight(StackPanel panel)
        {
            float value = (float)panel.DesiredSize.Height + 22;
            return value <= 22 ? 128 : value;
        }
        private static void LayoutSections()
        {
            if (!AllyGoodsPanelLayoutPolicy.OwnsHeight((float)layout.Height, appliedHeight))
            {
                Collapse(); blocked = true;
                SetSectionAvailability(false, "Another UI owner controls the goods root height.");
                Report("layout-owner", new InvalidOperationException("Another UI owner changed the root height; extension layout is suspended.")); return;
            }
            float viewportBottom = LocalViewportBottom();
            bool needsMarked = HasFooterControls;
            foreach (var entry in registry.Entries)
                if (entry.Placement == AllyGoodsSectionPlacement.GoodsDialogBottom) { needsMarked = true; break; }
            float goodsBottom = 0; string reason = "The goods-frame layout was not requested.";
            bool marked = needsMarked && TryGetGoodsBottom(out goodsBottom, out reason);
            bool showGoods = marked && anchoredSections != null && anchoredSections.Children.Count != 0;
            bool showLegacy = sections.Children.Count != 0;
            var presentation = PrepareFramePresentation(showGoods);
            SetFrameSurface(presentation?.Options);
            float goodsDesired = showGoods ? DesiredAnchoredHeight(presentation?.Options) : 0;
            if (!AllyGoodsPanelLayoutPolicy.TryCompose(baseHeight, goodsBottom, viewportBottom,
                goodsDesired, showGoods, showLegacy ? DesiredSectionHeight(sections) : 0, showLegacy,
                presentation?.Options, out var plan))
            { Collapse(); SetSectionAvailability(false, "The goods layout geometry is unavailable."); return; }
            if (!ApplyFramePresentation(presentation, plan))
            {
                RestoreFramePresentation(); frameBlocked = true; presentation = null; SetFrameSurface(null);
                if (!AllyGoodsPanelLayoutPolicy.TryCompose(baseHeight, goodsBottom, viewportBottom,
                    showGoods ? DesiredSectionHeight(anchoredSections) : 0, showGoods,
                    showLegacy ? DesiredSectionHeight(sections) : 0, showLegacy, out plan))
                { Collapse(); SetSectionAvailability(false, "The goods layout geometry is unavailable."); return; }
            }
            // Include hidden sections, but account for preceding anchored content
            // when reporting whether legacy sections have any remaining space.
            foreach (var entry in registry.Entries)
            {
                bool ownMarked = entry.Placement == AllyGoodsSectionPlacement.GoodsDialogBottom;
                float start = ownMarked ? goodsBottom : plan.LegacyTop;
                float reserve = ownMarked && presentation != null ? presentation.Options.VisualOverflow.Bottom + presentation.Options.ViewportMargin : 8;
                bool available = (!ownMarked || marked) && viewportBottom - start - reserve > 0;
                entry.SetLayoutAvailability(available, ownMarked && !marked ? reason : "There is no remaining space for this section.", layoutObserverFailed);
            }
            if (anchoredHost != null)
            {
                anchoredHost.Margin = new Thickness(0, plan.GoodsTop, 0, 0);
                anchoredHost.Height = plan.GoodsHeight;
                anchoredHost.Visibility = plan.GoodsHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            host.Margin = new Thickness(0, plan.LegacyTop, 0, 0);
            host.Height = plan.LegacyHeight; host.Visibility = plan.LegacyHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
            appliedHeight = plan.RootHeight; layout.Height = appliedHeight;
        }
        private static void CollapseAnchored()
        {
            RestoreFramePresentation(); SetFrameSurface(null);
            if (anchoredHost != null) { anchoredHost.Visibility = Visibility.Collapsed; anchoredHost.Height = 0; }
        }
        private static void DetachAnchored()
        {
            DetachFramePresentation();
            if (anchoredHost != null && layout != null) layout.Children.Remove(anchoredHost);
            anchoredHost = null; anchoredSections = null; anchoredSurface = null;
            goodsFrame = footerBackground = null; footerAnchor = null; markedResolved = false; markedFailure = null;
        }
        private static float DesiredAnchoredHeight(AllyGoodsFrameOptions options)
        {
            if (options == null) return DesiredSectionHeight(anchoredSections);
            return (float)anchoredSections.DesiredSize.Height + options.ContentPadding.Top + options.ContentPadding.Bottom;
        }
    }
}
