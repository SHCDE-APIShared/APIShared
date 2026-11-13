using System;
using System.Collections.Generic;
using APIShared.Localization;
using CrusaderDE;
using Noesis;
using SHCDESE.NoesisUtil;

namespace APIShared.Presentation
{
    public static partial class IngameMenuActions
    {
        private static readonly string[] vanillaNames = { "ButtonSave", "ButtonLoad", "ButtonWorkshop", "ButtonOptions",
            "ButtonHelp", "ButtonRestartMission", "ButtonQuitMission", "ButtonExitStronghold", "ButtonResume" };
        private static HUD_IngameMenu current;
        private static Grid layoutRoot, surface;
        private static StackPanel originalPanel, parking, mainPanel, additionalPanel, additionalPage;
        private static ScrollViewer mainScroll, additionalScroll;
        private static FrameworkElement viewport;
        private static Button additionalButton, backButton;
        private static bool additionalVisible;
        private static IngameMenuTooltips menuTooltips;
        // Vanilla's 31-high background has -1.5 margins: 28 layout units plus 10 between rows.
        private const float RowPitch = 38, HeaderHeight = 72, BottomSpace = 24;
        private static float vanillaMenuHeight;
        private static int vanillaRowCount, mainRowCount;
        private static readonly Button[] vanillaButtons = new Button[9];
        private static readonly Grid[] rows = new Grid[9];
        private static readonly Dictionary<IngameMenuRegistry.Entry, Button> buttons = new Dictionary<IngameMenuRegistry.Entry, Button>();
        private static readonly Dictionary<IngameMenuRegistry.Entry, Control> contents = new Dictionary<IngameMenuRegistry.Entry, Control>();
        private static readonly HashSet<IngameMenuRegistry.Entry> failedContents = new HashSet<IngameMenuRegistry.Entry>();
        private static readonly List<Grid> overflowRows = new List<Grid>();

        private static void ResolveView(HUD_IngameMenu menu)
        {
            if (menu == null) return;
            if (ReferenceEquals(current, menu) && surface != null) return;
            var root = menu.FindName("LayoutRoot") as Grid;
            var original = menu.FindName("LeftPanel") as StackPanel;
            var parent = original?.Parent as Panel;
            if (root == null || original == null || parent == null) throw new InvalidOperationException("ESC menu layout contract unavailable.");
            var originals = new Button[9];
            for (int i = 0; i < originals.Length; i++)
            {
                originals[i] = menu.FindName(vanillaNames[i]) as Button ?? throw new InvalidOperationException("Missing ESC action " + vanillaNames[i]);
                if (!ReferenceEquals(originals[i].Parent, original))
                    throw new InvalidOperationException("Foreign ESC layout moved " + vanillaNames[i] + "; keeping its layout unchanged.");
            }
            if (!(menu.TryFindResource("BTN_SH_GlowSDisable") is Style) ||
                !(menu.TryFindResource("APISharedMenuTooltipStyle") is Style))
                throw new InvalidOperationException("APIShared ESC menu XAML resources unavailable.");
            if (viewport != null) viewport.SizeChanged -= OnViewportSizeChanged;
            menuTooltips?.Detach();
            foreach (Button button in vanillaButtons) if (button != null) UnobserveLabel(button);
            foreach (Button button in buttons.Values) UnobserveLabel(button);
            if (additionalButton != null) UnobserveLabel(additionalButton);
            if (backButton != null) UnobserveLabel(backButton);
            current = menu; layoutRoot = root; originalPanel = original;
            vanillaMenuHeight = (float)root.Height;
            foreach (Control content in contents.Values)
            {
                content.Loaded -= OnContentLoaded; content.SizeChanged -= OnContentSizeChanged;
                Detach(content);
            }
            foreach (var entry in registry.Entries) entry.ContentUnavailable = false;
            buttons.Clear(); contents.Clear(); failedContents.Clear(); overflowRows.Clear();
            Array.Copy(originals, vanillaButtons, originals.Length);
            // Retain the original container and every foreign child in its existing order.
            mainPanel = originalPanel;
            parking = new StackPanel { Visibility = Visibility.Collapsed };
            additionalPanel = new StackPanel { Width = 367.5f, HorizontalAlignment = HorizontalAlignment.Center };
            mainScroll = Scroll(null);
            additionalScroll = Scroll(additionalPanel);
            additionalPage = new StackPanel { Visibility = Visibility.Collapsed };
            additionalPage.Children.Add(additionalScroll);
            surface = new Grid { Margin = new Thickness(0, HeaderHeight, 0, BottomSpace), HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top };
            surface.Children.Add(mainScroll); surface.Children.Add(additionalPage);
            surface.Children.Add(parking);
            additionalButton = NavigationButton(() => {
                if (!MenuVisible()) return;
                Refresh(true);
                if (registry.Resolve().Overflow.Count != 0) ShowAdditional(true);
            });
            backButton = NavigationButton(() => { if (MenuVisible()) { ShowAdditional(false); Refresh(true); } });
            additionalPage.Children.Add(backButton);
            for (int i = 0; i < rows.Length; i++)
                rows[i] = Row();
            parent.Children.Remove(originalPanel);
            originalPanel.Margin = new Thickness(0); // The surface owns the existing header offset.
            mainScroll.Content = mainPanel;
            parent.Children.Add(surface);
            for (int i = 0; i < rows.Length; i++)
            {
                int index = mainPanel.Children.IndexOf(vanillaButtons[i]);
                mainPanel.Children.Remove(vanillaButtons[i]);
                mainPanel.Children.Insert(index, rows[i]);
                parking.Children.Add(vanillaButtons[i]);
            }
            mainPanel.Children.Insert(mainPanel.Children.IndexOf(rows[(int)IngameMenuRow.Resume]), additionalButton);
            // Constructor-cached Vanilla button references, bindings and commands retain their exact identities.
            foreach (Button button in vanillaButtons) ObserveLabel(button);
            viewport = menu;
            FrameworkElement ancestor = menu;
            while (ancestor.Parent != null) { ancestor = ancestor.Parent; viewport = ancestor; }
            viewport.SizeChanged += OnViewportSizeChanged;
            menuTooltips = new IngameMenuTooltips(viewport, () => MenuVisible() &&
                ReferenceEquals(menu, current) && ReferenceEquals(menu, observedMain.HUDIngameMenu));
        }
        private static ScrollViewer Scroll(StackPanel content) => new ScrollViewer { Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
        private static Grid Row()
        {
            var row = new Grid { Width = 367.5f, Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(181.25f) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5f) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(181.25f) });
            return row;
        }
        private static Button NavigationButton(Action execute)
        {
            HUD_IngameMenu view = current;
            Button button = null;
            button = new Button { Width = 300, Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Style = current.TryFindResource("BTN_SH_GlowSDisable") as Style,
                Command = new RelayCommand(() => {
                    if (MenuVisible() && ReferenceEquals(view, current) &&
                        ReferenceEquals(view, observedMain.HUDIngameMenu) && button.IsVisible) execute();
                }) };
            button.MouseEnter += MainViewModel.Instance.CommonRedButtonEnter;
            ObserveLabel(button);
            PropEx.SetTextLeft(button, ""); PropEx.SetTextRight(button, "");
            return button;
        }
        private static void CaptureVanilla()
        {
            vanillaRowCount = 0;
            for (int i = 0; i < vanillaButtons.Length; i++)
            {
                Button button = vanillaButtons[i];
                if (button.Visibility == Visibility.Visible) vanillaRowCount++;
                registry.SetVanilla((IngameMenuRow)i, new IngameMenuActionState(PropEx.GetTextCentre(button)?.ToString(),
                    button.Visibility == Visibility.Visible, button.IsEnabled));
            }
        }
        private static Button GetButton(IngameMenuRegistry.Entry entry)
        {
            if (entry.Vanilla) return vanillaButtons[(int)entry.Options.Row.Value];
            if (!buttons.TryGetValue(entry, out Button button))
            {
                HUD_IngameMenu view = current;
                button = NavigationButton(() => Execute(entry, view, additionalVisible));
                button.Margin = new Thickness(0);
                buttons.Add(entry, button);
            }
            PropEx.SetTextCentre(button, entry.State.Text);
            button.IsEnabled = entry.State.Enabled;
            SetTip(button, entry.State.Tooltip);
            return button;
        }
        private static Control GetControl(IngameMenuRegistry.Entry entry)
        {
            if (entry.CreateContent == null) return GetButton(entry);
            if (failedContents.Contains(entry)) return null;
            if (!contents.TryGetValue(entry, out Control control))
            {
                try
                {
                    HUD_IngameMenu view = current;
                    var context = new IngameMenuContentContext(view, action => TryExecuteContent(entry, view, action));
                    control = entry.CreateContent(context);
                    if (control == null || control.Parent != null || contents.ContainsValue(control) || buttons.ContainsValue(control as Button))
                        throw new InvalidOperationException("Return a fresh unattached ESC control for each menu view.");
                    contents.Add(entry, control);
                    control.Loaded += OnContentLoaded;
                    control.SizeChanged += OnContentSizeChanged;
                }
                catch (Exception error)
                { entry.ContentUnavailable = true; failedContents.Add(entry); Report(entry, error); return null; }
            }
            control.IsEnabled = entry.State.Enabled;
            SetTip(control, entry.State.Tooltip);
            return control;
        }
        private static bool TryExecuteContent(IngameMenuRegistry.Entry entry, HUD_IngameMenu view, Action action)
        {
            try
            {
                RequireUnity();
                if (refreshing || !MenuVisible() || !ReferenceEquals(view, current) ||
                    !ReferenceEquals(view, observedMain.HUDIngameMenu) || !contents.TryGetValue(entry, out Control control) ||
                    !control.IsVisible || !control.IsEnabled || control.Parent == null) return false;
                bool page = additionalVisible;
                registry.Evaluate(true, Report); // Validate commands without disturbing a focused editor or rebuilding layout.
                if (!MenuVisible() || !ReferenceEquals(view, current) ||
                    !ReferenceEquals(view, observedMain.HUDIngameMenu) || page != additionalVisible ||
                    !control.IsVisible || !control.IsEnabled || control.Parent == null || !registry.CanExecute(entry, page) ||
                    !IsContentPresented(entry, control, page)) return false;
                action();
                return true;
            }
            catch (Exception error) { Report(entry, error); return false; }
        }
        private static bool IsContentPresented(IngameMenuRegistry.Entry entry, Control control, bool additional)
        {
            var layout = registry.Resolve();
            if (additional)
            {
                int index = layout.Overflow.IndexOf(entry);
                return index >= 0 && index / 2 < overflowRows.Count && ReferenceEquals(control.Parent, overflowRows[index / 2]);
            }
            for (int i = 0; i < rows.Length; i++)
                for (int side = 0; side < 2; side++)
                    if (ReferenceEquals(layout.Slots[i, side], entry)) return ReferenceEquals(control.Parent, rows[i]);
            return false;
        }
        private static void Detach(FrameworkElement element)
        { if (element.Parent is Panel parent) parent.Children.Remove(element); }
        private static void Place(Grid row, Control control, int side, bool pair)
        {
            // Preserve an unchanged parent: even detach/reattach of the same object loses keyboard focus in Noesis.
            if (!ReferenceEquals(control.Parent, row))
            { Detach(control); row.Children.Add(control); }
            control.Margin = new Thickness(0);
            control.Width = pair ? 181.25f : 300f;
            control.HorizontalAlignment = HorizontalAlignment.Center;
            control.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(control, pair ? side * 2 : 0);
            Grid.SetColumnSpan(control, pair ? 1 : 3);
            if (control is Button button) AdaptLabel(button);
        }
        private static void Render(IngameMenuRegistry.Layout layout)
        {
            // Materialize first so a broken consumer factory cannot abort other rows or leave an empty slot.
            var elements = new Dictionary<IngameMenuRegistry.Entry, Control>();
            foreach (var entry in registry.Entries)
                if (IngameMenuRegistry.Visible(entry))
                {
                    Control element = GetControl(entry);
                    if (element != null) elements.Add(entry, element);
                }
            layout = registry.Resolve(); // Failed content reveals the preceding valid claim, including Vanilla.
            for (int i = 0; i < rows.Length; i++)
                for (int side = 0; side < 2; side++)
                {
                    var entry = layout.Slots[i, side];
                    if (entry == null) continue;
                    if (entry.Vanilla) elements[entry] = GetControl(entry);
                    else if (!elements.ContainsKey(entry)) layout.Slots[i, side] = null;
                }
            layout.Overflow.RemoveAll(entry => !entry.Vanilla && !elements.ContainsKey(entry));
            foreach (var entry in layout.Overflow) if (entry.Vanilla) elements[entry] = GetControl(entry);
            int overflowCount = (layout.Overflow.Count + 1) / 2;
            while (overflowRows.Count < overflowCount)
            { Grid row = Row(); overflowRows.Add(row); additionalPanel.Children.Add(row); }
            var destinations = new Dictionary<Control, Grid>();
            for (int i = 0; i < rows.Length; i++)
                for (int side = 0; side < 2; side++)
                    if (layout.Slots[i, side] != null) destinations[elements[layout.Slots[i, side]]] = rows[i];
            for (int i = 0; i < layout.Overflow.Count; i++) destinations[elements[layout.Overflow[i]]] = overflowRows[i / 2];
            // Remove only elements that actually move or disappear. In particular do not clear stable rows.
            foreach (Button button in vanillaButtons)
                if (!destinations.TryGetValue(button, out Grid row) || !ReferenceEquals(button.Parent, row)) Detach(button);
            foreach (Button button in buttons.Values)
                if (!destinations.TryGetValue(button, out Grid row) || !ReferenceEquals(button.Parent, row)) Detach(button);
            foreach (Control content in contents.Values)
                if (!destinations.TryGetValue(content, out Grid row) || !ReferenceEquals(content.Parent, row)) Detach(content);
            while (overflowRows.Count > overflowCount)
            { int index = overflowRows.Count - 1; additionalPanel.Children.Remove(overflowRows[index]); overflowRows.RemoveAt(index); }
            mainRowCount = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                var left = layout.Slots[i, 0]; var right = layout.Slots[i, 1];
                bool pair = left != null && right != null;
                if (left != null) Place(rows[i], elements[left], 0, pair);
                if (right != null) Place(rows[i], elements[right], 1, pair);
                bool visible = left != null || right != null;
                rows[i].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible) mainRowCount++;
            }
            for (int i = 0; i < layout.Overflow.Count; i += 2)
            {
                bool pair = i + 1 < layout.Overflow.Count;
                Place(overflowRows[i / 2], elements[layout.Overflow[i]], 0, pair);
                if (pair) Place(overflowRows[i / 2], elements[layout.Overflow[i + 1]], 1, true);
            }
            // Keep hidden originals attached to retain inherited bindings and subsequent Vanilla Init writes.
            for (int i = 0; i < vanillaButtons.Length; i++)
                if (vanillaButtons[i].Parent == null) parking.Children.Add(vanillaButtons[i]);
            foreach (Button button in buttons.Values) if (button.Parent == null) SetTip(button, null);
            foreach (Control content in contents.Values) if (content.Parent == null) SetTip(content, null);
            string more = UiText.Get("IngameMenu.AdditionalActions"), back = UiText.Get("IngameMenu.Back");
            PropEx.SetTextCentre(additionalButton, more);
            PropEx.SetTextCentre(backButton, back);
            additionalButton.Visibility = layout.Overflow.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (layout.Overflow.Count != 0) mainRowCount++;
            AdaptLabel(additionalButton); AdaptLabel(backButton);
            if (layout.Overflow.Count == 0) ShowAdditional(false);
            UpdateViewport();
        }
        private static void SetTip(Control button, string text)
        {
            // Only our own mod buttons enter this path. Never touch Vanilla/foreign popup objects,
            // including when the original button is displayed on the additional page.
            var tip = ToolTipService.GetToolTip(button) as ToolTip;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (tip != null)
                {
                    menuTooltips.Remove(tip);
                    ToolTipService.SetToolTip(button, null);
                }
                return;
            }
            if (tip == null)
            {
                tip = new ToolTip { Style = current.TryFindResource("APISharedMenuTooltipStyle") as Style };
                ToolTipService.SetToolTip(button, tip);
                ToolTipService.SetShowDuration(button, 60000);
            }
            menuTooltips.Attach(tip, button);
            tip.Content = text;
        }
        private static void ShowAdditional(bool show)
        {
            if (show != additionalVisible || !MenuVisible()) menuTooltips?.CloseAll();
            additionalVisible = show;
            if (mainScroll == null || additionalPage == null) return;
            mainScroll.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            additionalPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            UpdateViewport();
        }
        private static void OnViewportSizeChanged(object sender, SizeChangedEventArgs args) => UpdateViewport();
        private static void OnContentLoaded(object sender, RoutedEventArgs args) => UpdateViewport();
        private static void OnContentSizeChanged(object sender, SizeChangedEventArgs args) => UpdateViewport();
        private static float ContentExtraHeight(Grid row)
        {
            float pitch = RowPitch;
            foreach (Control content in contents.Values)
            {
                if (!ReferenceEquals(content.Parent, row)) continue;
                float height = (float)content.Height;
                if (float.IsNaN(height) || float.IsInfinity(height) || height <= 0) height = content.ActualHeight;
                if (!float.IsNaN(height) && !float.IsInfinity(height) && height > 0)
                    pitch = Math.Max(pitch, height + 10);
            }
            return pitch - RowPitch;
        }
        private static void UpdateViewport()
        {
            if (layoutRoot == null || mainScroll == null) return;
            float desired = additionalVisible
                ? Math.Max(vanillaMenuHeight, HeaderHeight + (overflowRows.Count + 1) * RowPitch + BottomSpace)
                : vanillaMenuHeight + Math.Max(0, mainRowCount - vanillaRowCount) * RowPitch;
            if (additionalVisible)
            {
                float bodyHeight = (overflowRows.Count + 1) * RowPitch;
                foreach (Grid row in overflowRows) bodyHeight += ContentExtraHeight(row);
                desired = Math.Max(vanillaMenuHeight, HeaderHeight + bodyHeight + BottomSpace);
            }
            else
                foreach (Grid row in rows)
                    if (row.Visibility == Visibility.Visible) desired += ContentExtraHeight(row);
            float available = desired;
            if (viewport != null && viewport.ActualHeight > 0)
            {
                // The viewport can be outside Vanilla's UI scale. Convert its height to menu units;
                // physical Unity screen pixels must never be used as Noesis layout dimensions.
                float scale = layoutRoot.TransformToAncestor(viewport).TransformBounds(new Rect(0, 0, 1, 1)).Height;
                if (scale > 0) available = Math.Max(HeaderHeight + BottomSpace + 2 * RowPitch, viewport.ActualHeight / scale - 20);
            }
            float height = Math.Min(desired, available);
            if (layoutRoot.Height != height) layoutRoot.Height = height;
            float body = Math.Max(RowPitch, height - HeaderHeight - BottomSpace);
            if (mainScroll.MaxHeight != body) mainScroll.MaxHeight = body;
            if (additionalScroll.MaxHeight != body - RowPitch) additionalScroll.MaxHeight = body - RowPitch;
        }
        private static void ObserveLabel(Button button)
        {
            button.Loaded += OnButtonLoaded;
            button.SizeChanged += OnButtonSizeChanged;
        }
        private static void UnobserveLabel(Button button)
        {
            button.Loaded -= OnButtonLoaded;
            button.SizeChanged -= OnButtonSizeChanged;
        }
        private static void OnButtonLoaded(object sender, RoutedEventArgs args)
        { if (sender is Button button && ReferenceEquals(current, observedMain?.HUDIngameMenu)) AdaptLabel(button); }
        private static void OnButtonSizeChanged(object sender, SizeChangedEventArgs args)
        { if (sender is Button button && ReferenceEquals(current, observedMain?.HUDIngameMenu)) AdaptLabel(button); }
        private static void AdaptLabel(Button button)
        {
            if (!button.IsLoaded || button.Parent == parking) return;
            // Only bound text width/trimming changes. In particular, leave both texture grids,
            // the 24-high text, margins, template bindings, opacity and all style triggers intact.
            foreach (string name in new[] { "CenterText", "TextDisabled" })
                if (button.Template?.FindName(name, button) is TextBlock text)
                {
                    text.MaxWidth = Math.Max(1, button.Width - 24);
                    text.TextWrapping = TextWrapping.NoWrap;
                    text.TextTrimming = TextTrimming.CharacterEllipsis;
                }
        }
    }
}
