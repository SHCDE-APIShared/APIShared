using Noesis;
using System.Runtime.CompilerServices;

namespace APIShared.ModSettings
{
    /// <summary>Opt-in shared popup sizing. Applies the established resolution presets on each opening,
    /// taking exclusive sizing ownership from the SE attached scaler for this tooltip only. UI thread only.</summary>
    public static class SharedToolTipSizing
    {
        private sealed class State {
            internal bool PreviousScaler, Applying;
            internal FrameworkElement Source, Viewport;
            internal ToolTip Tip;
            internal void Resize(object sender, SizeChangedEventArgs args) { if (Tip.IsOpen) Apply(Tip); }
            internal void Detach() { if (Source != null) Source.SizeChanged -= Resize; if (Viewport != null && Viewport != Source) Viewport.SizeChanged -= Resize; Source = Viewport = null; }
            internal void Attach(FrameworkElement source) {
                if (Source == source) return; Detach(); Source = source;
                if (source == null) return;
                Viewport = source; while (VisualTreeHelper.GetParent(Viewport) is FrameworkElement parent) Viewport = parent;
                Source.SizeChanged += Resize; if (Viewport != Source) Viewport.SizeChanged += Resize;
            }
        }
        private static readonly ConditionalWeakTable<ToolTip, State> states = new ConditionalWeakTable<ToolTip, State>();
        /// <summary>Attached opt-in; does not change unrelated or Vanilla tooltips.</summary>
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(SharedToolTipSizing), new PropertyMetadata(false, Changed));
        /// <summary>Reads the opt-in state.</summary>
        public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
        /// <summary>Changes sizing ownership for this popup.</summary>
        public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
        /// <summary>Optional source surface. With Enabled, matches ReferenceFontSize in source pixels instead
        /// of resolution presets. Its top-level viewport supplies the available width. UI thread only.</summary>
        public static readonly DependencyProperty ReferenceElementProperty = DependencyProperty.RegisterAttached(
            "ReferenceElement", typeof(FrameworkElement), typeof(SharedToolTipSizing), new PropertyMetadata(null, ReferenceChanged));
        /// <summary>Reads the optional source surface.</summary>
        public static FrameworkElement GetReferenceElement(DependencyObject value) => (FrameworkElement)value.GetValue(ReferenceElementProperty);
        /// <summary>Sets the source surface; null restores the existing preset behavior.</summary>
        public static void SetReferenceElement(DependencyObject value, FrameworkElement reference) => value.SetValue(ReferenceElementProperty, reference);
        /// <summary>Logical source font size, default 16; used only with ReferenceElement and Enabled.</summary>
        public static readonly DependencyProperty ReferenceFontSizeProperty = DependencyProperty.RegisterAttached(
            "ReferenceFontSize", typeof(float), typeof(SharedToolTipSizing), new PropertyMetadata(16f, ReferenceChanged));
        /// <summary>Reads the logical source font size.</summary>
        public static float GetReferenceFontSize(DependencyObject value) => (float)value.GetValue(ReferenceFontSizeProperty);
        /// <summary>Sets a positive finite logical source font size. Invalid values use 16.</summary>
        public static void SetReferenceFontSize(DependencyObject value, float size) => value.SetValue(ReferenceFontSizeProperty, size);
        private static void ReferenceChanged(DependencyObject value, DependencyPropertyChangedEventArgs args) {
            if (!(value is ToolTip tip) || !GetEnabled(tip)) return;
            if (states.TryGetValue(tip, out var state)) state.Detach(); Apply(tip);
        }
        private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
        {
            if (!(value is ToolTip tip)) return;
            tip.Opened -= Opened;
            tip.Loaded -= Opened; tip.Closed -= Closed; tip.SizeChanged -= Resized;
            if ((bool)args.NewValue)
            {
                var state = states.GetValue(tip, _ => new State());
                state.PreviousScaler = SHCDESE.UI.ToolTipResolutionScale.GetEnabled(tip);
                state.Tip = tip;
                SHCDESE.UI.ToolTipResolutionScale.SetEnabled(tip, false);
                tip.Opened += Opened;
                tip.Loaded += Opened; tip.Closed += Closed; tip.SizeChanged += Resized;
                Apply(tip);
            }
            else if (states.TryGetValue(tip, out var previous))
            {
                previous.Detach();
                SHCDESE.UI.ToolTipResolutionScale.SetEnabled(tip, previous.PreviousScaler);
                states.Remove(tip);
            }
        }
        private static void Closed(object sender, RoutedEventArgs args) { if (states.TryGetValue((ToolTip)sender, out var state)) state.Detach(); }
        private static void Resized(object sender, SizeChangedEventArgs args) { if (((ToolTip)sender).IsOpen) Apply((ToolTip)sender); }
        private static void Opened(object sender, RoutedEventArgs args)
        {
            var tip = (ToolTip)sender;
            // A style may have applied SE's attached value after our own. Removing its handler
            // here also prevents a later opening from restoring the smaller SE preset.
            SHCDESE.UI.ToolTipResolutionScale.SetEnabled(tip, false);
            Apply(tip);
        }
        internal static void Apply(ToolTip tip)
        {
            if (!states.TryGetValue(tip, out var state) || state.Applying) return;
            var reference = GetReferenceElement(tip);
            if (reference != null) {
                if (!tip.IsOpen) return;
                state.Attach(reference);
                if (!reference.IsLoaded || !tip.IsLoaded || !tip.IsVisible) return;
                if (!reference.IsVisible) { tip.IsOpen = false; return; }
                if (!Presentation.PopupPixelScale.TryGet(reference, tip, out float x, out float y, out float sourceX)) return;
                float size = GetReferenceFontSize(tip); if (!Presentation.PopupPixelScale.Positive(size)) size = 16;
                float available = 420;
                if (state.Viewport?.ActualWidth > 0) {
                    Point left = state.Viewport.PointToScreen(new Point(0, 0));
                    Point right = state.Viewport.PointToScreen(new Point(state.Viewport.ActualWidth, 0));
                    float width = (right.X - left.X) * 100 / sourceX - 20;
                    if (Presentation.PopupPixelScale.Positive(width)) available = System.Math.Min(available, width);
                }
                state.Applying = true;
                try {
                    float font = size * y, width = System.Math.Max(1, available * x);
                    if (System.Math.Abs(tip.FontSize - font) > .01f) tip.FontSize = font;
                    if (System.Math.Abs(tip.MaxWidth - width) > .01f) tip.MaxWidth = width;
                    var padding = new Thickness(10*x, 8*y, 10*x, 8*y); if (!tip.Padding.Equals(padding)) tip.Padding = padding;
                    var border = new Thickness(1.5f*x, 1.5f*y, 1.5f*x, 1.5f*y); if (!tip.BorderThickness.Equals(border)) tip.BorderThickness = border;
                } finally { state.Applying = false; }
                return;
            }
            tip.FontSize = ToolTipPresentation.AutomaticFontSize;
            tip.MaxWidth = ToolTipPresentation.AutomaticMaximumWidth;
            tip.Padding = Internal.ToolTipPresentation.AutomaticPadding;
            tip.BorderThickness = Internal.ToolTipPresentation.AutomaticBorderThickness;
        }
    }
}
