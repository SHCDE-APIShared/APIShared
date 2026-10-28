using System;
using System.Collections.Generic;
using Noesis;

namespace APIShared.Presentation
{
    /// <summary>View-owned ESC popups. Matches button pixels across Noesis popup views,
    /// independently of resolution presets. Only Unity UI events reach this helper.</summary>
    internal sealed class IngameMenuTooltips
    {
        private readonly FrameworkElement viewport;
        private readonly Func<bool> available;
        private readonly Dictionary<ToolTip, Popup> popups = new Dictionary<ToolTip, Popup>();
        private bool attached = true;

        internal IngameMenuTooltips(FrameworkElement viewport, Func<bool> available)
        {
            this.viewport = viewport;
            this.available = available;
            viewport.SizeChanged += OnViewportChanged;
        }
        internal void Attach(ToolTip tip, Button button)
        {
            if (!attached || popups.ContainsKey(tip)) return;
            popups.Add(tip, new Popup(this, tip, button));
        }
        internal void CloseAll()
        {
            // Closing can synchronously dispatch UI events. Enumerate a detached snapshot.
            foreach (var popup in new List<Popup>(popups.Values)) popup.Close();
        }
        internal void Remove(ToolTip tip)
        {
            if (!popups.TryGetValue(tip, out Popup popup)) return;
            popups.Remove(tip);
            popup.Detach();
        }
        internal void Detach()
        {
            if (!attached) return;
            attached = false;
            viewport.SizeChanged -= OnViewportChanged;
            foreach (var popup in new List<Popup>(popups.Values)) popup.Detach();
            popups.Clear();
        }
        private void OnViewportChanged(object sender, SizeChangedEventArgs args)
        { foreach (var popup in new List<Popup>(popups.Values)) popup.Apply(); }
        private static bool Positive(float value) => value > 0 && !float.IsInfinity(value);

        private sealed class Popup
        {
            private readonly IngameMenuTooltips owner;
            private readonly ToolTip tip;
            private readonly Button button;
            private readonly bool previousScaler;
            internal Popup(IngameMenuTooltips owner, ToolTip tip, Button button)
            {
                this.owner = owner; this.tip = tip; this.button = button;
                previousScaler = SHCDESE.UI.ToolTipResolutionScale.GetEnabled(tip);
                SHCDESE.UI.ToolTipResolutionScale.SetEnabled(tip, false);
                tip.Opened += OnOpened;
                tip.Loaded += OnOpened;
                tip.SizeChanged += OnSizeChanged;
                button.SizeChanged += OnSizeChanged;
            }
            private void OnOpened(object sender, RoutedEventArgs args) => Apply();
            private void OnSizeChanged(object sender, SizeChangedEventArgs args) => Apply();
            internal void Close() { if (tip.IsOpen) tip.IsOpen = false; }
            internal void Detach()
            {
                tip.Opened -= OnOpened;
                tip.Loaded -= OnOpened;
                tip.SizeChanged -= OnSizeChanged;
                button.SizeChanged -= OnSizeChanged;
                Close();
                SHCDESE.UI.ToolTipResolutionScale.SetEnabled(tip, previousScaler);
            }
            internal void Apply()
            {
                if (!tip.IsOpen) return;
                if (!owner.attached || !owner.available() || !button.IsVisible) { Close(); return; }
                // The popup joins a separate view lazily; Loaded/SizeChanged retries after attachment.
                if (!tip.IsLoaded || !tip.IsVisible || !button.IsLoaded) return;
                SHCDESE.UI.ToolTipResolutionScale.SetEnabled(tip, false);
                Point origin = button.PointToScreen(new Point(0, 0));
                Point unit = button.PointToScreen(new Point(100, 100));
                Point popupOrigin = tip.PointToScreen(new Point(0, 0));
                Point popupUnit = tip.PointToScreen(new Point(100, 100));
                float menuX = unit.X - origin.X, menuY = unit.Y - origin.Y;
                float popupX = popupUnit.X - popupOrigin.X, popupY = popupUnit.Y - popupOrigin.Y;
                if (!Positive(menuX) || !Positive(menuY) || !Positive(popupX) || !Positive(popupY)) return;
                float x = menuX / popupX, y = menuY / popupY;
                if (!Positive(x) || !Positive(y)) return;
                float labelFont = (button.Template?.FindName("CenterText", button) as TextBlock)?.FontSize ?? button.FontSize;
                float font = labelFont * y;
                float menuWidth = 420;
                if (owner.viewport.ActualWidth > 0)
                {
                    Point left = owner.viewport.PointToScreen(new Point(0, 0));
                    Point right = owner.viewport.PointToScreen(new Point(owner.viewport.ActualWidth, 0));
                    float availableWidth = (right.X - left.X) * 100 / menuX - 20;
                    if (!float.IsNaN(availableWidth) && !float.IsInfinity(availableWidth))
                        menuWidth = Math.Max(1, Math.Min(menuWidth, availableWidth));
                }
                float width = Math.Max(1, menuWidth * x);
                if (!Positive(font) || !Positive(width)) return;
                // Absolute results from measured transforms, not accumulated multipliers: layout is idempotent.
                if (Math.Abs(tip.FontSize - font) > 0.01f) tip.FontSize = font;
                if (Math.Abs(tip.MaxWidth - width) > 0.01f) tip.MaxWidth = width;
                var padding = new Thickness(10 * x, 8 * y, 10 * x, 8 * y);
                if (!tip.Padding.Equals(padding)) tip.Padding = padding;
                var border = new Thickness(1.5f * x, 1.5f * y, 1.5f * x, 1.5f * y);
                if (!tip.BorderThickness.Equals(border)) tip.BorderThickness = border;
            }
        }
    }
}
