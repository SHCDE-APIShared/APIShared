using System;
using System.Collections.Generic;
using APIShared.Localization;
using Noesis;

namespace APIShared.Presentation
{
    public static partial class AllyGoodsPanelExtensions
    {
        private static readonly AllyGoodsSectionRegistry footerRegistry = new AllyGoodsSectionRegistry();
        private static readonly AllyGoodsFooterCache<FooterVisual> footerCache = new AllyGoodsFooterCache<FooterVisual>();
        private static readonly List<FooterVisual> footerVisuals = new List<FooterVisual>();
        private static Grid footerOwner, footerHost;
        private static StackPanel footerInline, footerOverflow;
        private static Button footerMore;
        private static Popup footerPopup;
        private static Border footerPopupSurface;
        private static ScaleTransform footerPopupScale;
        private static ScrollViewer footerScroll;
        private static FrameworkElement footerViewport;
        private static bool footerDirty = true, footerLayoutBusy, footerSend, footerOverflowing, footerProbe;

        /// <summary>Registers an initially visible/enabled compact footer control on the initialized Unity
        /// thread after APIShared and Script Extender readiness. Owner GUID and ID must be nonempty and
        /// unique together in the independent footer namespace. Factories run on Unity outside locks,
        /// return fresh unattached controls, and retain their own DataContext and resources. Controls sort
        /// by order, ordinal GUID and ID; successful controls survive selection/hide/overflow changes.
        /// Explicit content refresh or host replacement recreates them. Factory failures affect only that
        /// registration until content refresh or host replacement. The marked footer must remain attached
        /// and its lower strip unoccupied; foreign controls are never moved. This installs no hooks and
        /// grants no gameplay authority. Registration lasts until process exit.</summary>
        public static bool TryRegisterFooterControl(string ownerGuid, string registrationId,
            Func<AllyGoodsSectionContext, FrameworkElement> factory, out AllyGoodsFooterRegistration registration,
            out string reason, int order = 0)
        {
            registration = null;
            if (!EnsureInitialized(out reason)) return false;
            if (!footerRegistry.TryRegister(ownerGuid, registrationId, factory, order, out var entry, out reason)) return false;
            registration = new AllyGoodsFooterRegistration(entry, InvalidateFooter);
            footerDirty = true; return true;
        }
        private static bool HasFooterControls => footerRegistry.Entries.Length != 0;
        private static void InvalidateFooter() { footerDirty = true; }

        // Called by the existing GuiChecks publisher, even without visible sections.
        // Native layout owns measurement; LayoutUpdated observes only attached controls.
        private static void UpdateFooter(AllyGoodsPanelContext context)
        {
            footerSend = context.IsSendMode;
            if (!HasFooterControls || !footerSend) { HideFooter(); return; }
            if (!TryResolveMarkedLayout(out string reason) || footerAnchor == null)
            { HideFooter(); if (!string.IsNullOrEmpty(reason)) Report("footer-anchor", new InvalidOperationException(reason)); return; }
            if (!ReferenceEquals(footerOwner, footerAnchor))
            { DetachFooter(); CreateFooterHost(); footerSend = context.IsSendMode; }
            if (footerDirty) RebuildFooter(context);
            ApplyFooterLayout();
        }
        // Keep factory closures behind the dirty boundary, including in generated IL.
        private static void RebuildFooter(AllyGoodsPanelContext context)
        {
            footerDirty = false;
            var next = new List<FooterVisual>();
            foreach (var entry in footerRegistry.Entries)
            {
                if (!entry.Visible) continue;
                var visual = footerCache.GetOrCreate(entry, () => CreateFooterVisual(entry, context),
                    error => Report("footer-factory:" + entry.Owner + "/" + entry.Id, error), ReleaseFooterVisual);
                if (visual == null) continue;
                visual.Container.IsEnabled = entry.Enabled;
                next.Add(visual);
            }
            bool changed = next.Count != footerVisuals.Count;
            for (int index = 0; !changed && index < next.Count; index++)
                changed = !ReferenceEquals(next[index], footerVisuals[index]);
            footerVisuals.Clear(); footerVisuals.AddRange(next);
            // Only changed content needs an attached inline measurement pass.
            // Enabled-only changes preserve an open popup and unrelated controls' parents.
            if (changed) { footerPopup.IsOpen = false; footerProbe = true; SetFooterMode(false); }
            SynchronizeFooterChildren();
        }
        private static void CreateFooterHost()
        {
            footerOwner = footerAnchor;
            footerHost = new Grid { Height = AllyGoodsFooterPolicy.Height,
                Margin = new Thickness(AllyGoodsFooterPolicy.Inset, 0, AllyGoodsFooterPolicy.Inset, AllyGoodsFooterPolicy.Bottom),
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch,
                Visibility = Visibility.Hidden, ClipToBounds = true };
            footerInline = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            footerOverflow = new StackPanel { Orientation = Orientation.Vertical };
            footerScroll = new ScrollViewer { Content = footerOverflow, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            footerPopupSurface = new Border { Child = footerScroll, Padding = new Thickness(8),
                Background = new SolidColorBrush(Color.FromArgb(255, 29, 23, 16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 242, 212, 138)), BorderThickness = new Thickness(1) };
            footerPopupScale = new ScaleTransform(1, 1);
            footerPopupSurface.LayoutTransform = footerPopupScale;
            footerPopupSurface.Loaded += OnFooterPopupLoaded;
            footerPopupSurface.SizeChanged += OnFooterPopupSizeChanged;
            footerMore = new Button { Content = UiText.Get("AllyGoods.More"), FontSize = 12,
                Height = AllyGoodsFooterPolicy.Height, MinHeight = 0, MinWidth = 0,
                Padding = new Thickness(6, 0, 6, 0), HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed };
            AttachFooterMoreTooltip();
            footerMore.Click += OnFooterMore;
            footerPopup = new Popup { PlacementTarget = footerMore, Placement = PlacementMode.Top,
                StaysOpen = false, Child = footerPopupSurface };
            footerPopup.Opened += OnFooterPopupOpened;
            footerHost.Children.Add(footerInline); footerHost.Children.Add(footerMore); footerHost.Children.Add(footerPopup);
            footerOwner.Children.Add(footerHost);
            footerOwner.LayoutUpdated += OnFooterLayoutUpdated;
            footerViewport = footerOwner;
            while (footerViewport.Parent is FrameworkElement parent) footerViewport = parent;
            footerDirty = true; footerProbe = true; footerOverflowing = false;
        }
        private static FooterVisual CreateFooterVisual(AllyGoodsSectionEntry entry, AllyGoodsPanelContext context)
        {
            var element = entry.Factory(new AllyGoodsSectionContext(footerHost, context));
            if (element == null || element.Parent != null)
                throw new InvalidOperationException("Footer factories must return fresh unattached controls.");
            return new FooterVisual(element);
        }
        private static void ReleaseFooterVisual(FooterVisual visual)
        { if (visual.Container.Parent is Panel parent) parent.Children.Remove(visual.Container); }
        private static void SynchronizeFooterChildren()
        {
            var destination = footerOverflowing ? footerOverflow : footerInline;
            var other = footerOverflowing ? footerInline : footerOverflow;
            int index = 0;
            foreach (var visual in footerVisuals)
            {
                var control = visual.Container;
                control.Visibility = visual.Element.Visibility == Visibility.Collapsed ? Visibility.Collapsed : Visibility.Visible;
                if (index < destination.Children.Count && ReferenceEquals(destination.Children[index], control)) { index++; continue; }
                if (control.Parent is Panel previous) previous.Children.Remove(control);
                destination.Children.Insert(index++, control);
            }
            while (destination.Children.Count > index) destination.Children.RemoveAt(destination.Children.Count - 1);
            other.Children.Clear();
        }
        private static void SetFooterMode(bool overflow)
        {
            bool changed = footerOverflowing != overflow;
            footerOverflowing = overflow;
            if (changed) { footerPopup.IsOpen = false; SynchronizeFooterChildren(); }
            footerInline.Visibility = overflow ? Visibility.Collapsed : Visibility.Visible;
            footerMore.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        }
        private static void OnFooterLayoutUpdated(object sender, Noesis.EventArgs args)
        {
            if (footerLayoutBusy || footerHost == null || !footerSend) return;
            try { ApplyFooterLayout(); }
            catch (Exception error) { HideFooter(); Report("footer-layout", error); }
        }
        private static void HideFooter()
        {
            if (footerPopup != null) footerPopup.IsOpen = false;
            if (footerHost != null) footerHost.Visibility = Visibility.Hidden;
        }
        private static void OnFooterMore(object sender, RoutedEventArgs args)
        {
            if (!footerSend || footerHost.Visibility != Visibility.Visible || !footerOverflowing) return;
            footerPopup.IsOpen = !footerPopup.IsOpen;
        }
        private static void OnFooterPopupOpened(object sender, Noesis.EventArgs args) => ApplyFooterPopupScale();
        private static void OnFooterPopupLoaded(object sender, RoutedEventArgs args) => ApplyFooterPopupScale();
        private static void OnFooterPopupSizeChanged(object sender, SizeChangedEventArgs args) => ApplyFooterPopupScale();
        private static void ApplyFooterPopupScale()
        {
            if (footerPopup == null || !footerPopup.IsOpen || !footerPopupSurface.IsLoaded || !footerMore.IsLoaded) return;
            if (PopupPixelScale.TryGet(footerMore, footerPopupSurface, out float x, out float y, out _))
            {
                // The popup child belongs to the actual popup view. Its measured
                // transform already contains our scale; divide that contribution
                // out of the ratio before applying the new absolute scale.
                x *= footerPopupScale.ScaleX; y *= footerPopupScale.ScaleY;
                if (Math.Abs(footerPopupScale.ScaleX - x) > 0.001f) footerPopupScale.ScaleX = x;
                if (Math.Abs(footerPopupScale.ScaleY - y) > 0.001f) footerPopupScale.ScaleY = y;
            }
        }
        private static void AttachFooterMoreTooltip()
        {
            var style = GUI.ParseXaml(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ToolTip}'>
<Setter Property='Background' Value='#FF1D1710'/><Setter Property='Foreground' Value='White'/><Setter Property='BorderBrush' Value='#FFF2D48A'/><Setter Property='BorderThickness' Value='1'/><Setter Property='FontSize' Value='14'/><Setter Property='MaxWidth' Value='420'/>
<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ToolTip}'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}' MaxWidth='{TemplateBinding MaxWidth}'><TextBlock Text='{TemplateBinding Content}' Foreground='{TemplateBinding Foreground}' FontSize='{TemplateBinding FontSize}' TextWrapping='Wrap'/></Border></ControlTemplate></Setter.Value></Setter></Style>") as Style;
            var tip = new ToolTip { Style = style, Content = UiText.Get("AllyGoods.More"), IsHitTestVisible = false };
            ModSettings.SharedToolTipSizing.SetEnabled(tip, true);
            ModSettings.SharedToolTipSizing.SetReferenceElement(tip, footerMore);
            ModSettings.SharedToolTipSizing.SetReferenceFontSize(tip, 12);
            ToolTipService.SetToolTip(footerMore, tip); ToolTipService.SetShowDuration(footerMore, 60000);
        }
        private static void DetachFooter()
        {
            HideFooter();
            if (footerOwner != null) footerOwner.LayoutUpdated -= OnFooterLayoutUpdated;
            if (footerMore != null) footerMore.Click -= OnFooterMore;
            if (footerPopup != null) footerPopup.Opened -= OnFooterPopupOpened;
            if (footerPopupSurface != null)
            { footerPopupSurface.Loaded -= OnFooterPopupLoaded; footerPopupSurface.SizeChanged -= OnFooterPopupSizeChanged; }
            footerCache.Clear(ReleaseFooterVisual); footerVisuals.Clear();
            if (footerOwner != null && footerHost != null) footerOwner.Children.Remove(footerHost);
            footerOwner = footerHost = null; footerInline = footerOverflow = null; footerMore = null;
            footerPopup = null; footerPopupSurface = null; footerPopupScale = null; footerScroll = null; footerViewport = null;
            footerDirty = true; footerSend = footerOverflowing = footerProbe = false;
        }
        private sealed class FooterVisual
        {
            internal readonly FrameworkElement Element;
            internal readonly Border Container;
            internal float InlineWidth;
            internal FooterVisual(FrameworkElement element)
            { Element = element; Container = new Border { Child = element, Margin = new Thickness(4, 0, 4, 0),
                MaxHeight = AllyGoodsFooterPolicy.Height, ClipToBounds = true }; }
        }
    }
}
