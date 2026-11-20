using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using APIShared.Events;
using APIShared.Internal;
using APIShared.Localization;
using APIShared.ModSettings;
using Noesis;

namespace APIShared.Input
{
    /// <summary>APIShared's attachment to the game's keyboard settings page, used by the options XAML patch.</summary>
    public static class ModKeybindOptions
    {
        private static readonly ConditionalWeakTable<Grid, View> Views = new ConditionalWeakTable<Grid, View>();

        /// <summary>Identifies a Vanilla keyboard settings grid. Set only on the Unity thread.</summary>
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(ModKeybindOptions), new PropertyMetadata(false, OnEnabledChanged));

        /// <summary>Reads the game options attachment's opt-in state.</summary>
        public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

        /// <summary>Enables the options attachment on the game's keyboard settings grid.</summary>
        public static void SetEnabled(DependencyObject element, bool enabled) => element.SetValue(EnabledProperty, enabled);

        private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
        {
            if (!(element is Grid host)) return;
            if ((bool)args.NewValue)
                Views.GetValue(host, grid => new View(grid)).Attach();
            else if (Views.TryGetValue(host, out View view))
            {
                view.Detach();
                Views.Remove(host);
            }
        }

        private sealed class View
        {
            private readonly Grid host;
            private readonly Dictionary<ModKeybindRegistration, Row> rows = new Dictionary<ModKeybindRegistration, Row>();
            private ListView vanillaList;
            private Thickness originalListMargin;
            private Button open, back;
            private Border page;
            private StackPanel rowsPanel;
            private TextBlock prompt;
            private ModKeybindRegistration capture;
            private Style tooltipStyle, buttonStyle;
            private bool observing, refreshQueued, missingAnchorReported;

            internal View(Grid host) { this.host = host; }

            internal void Attach()
            {
                host.Loaded += OnLoaded;
                host.Unloaded += OnUnloaded;
                host.IsVisibleChanged += OnVisibilityChanged;
                if (host.IsLoaded) Observe();
            }

            internal void Detach()
            {
                host.Loaded -= OnLoaded;
                host.Unloaded -= OnUnloaded;
                host.IsVisibleChanged -= OnVisibilityChanged;
                StopObserving();
                if (open != null) host.Children.Remove(open);
                if (page != null) host.Children.Remove(page);
            }

            private void OnLoaded(object sender, RoutedEventArgs args) => Observe();

            private void OnUnloaded(object sender, RoutedEventArgs args) => StopObserving();

            private void Observe()
            {
                if (observing) return;
                observing = true;
                ModKeybinds.Changed += OnRegistryChanged;
                Refresh();
            }

            private void StopObserving()
            {
                if (!observing) return;
                observing = false;
                ModKeybinds.Changed -= OnRegistryChanged;
                ClosePage();
                SetEntryVisible(false);
            }

            private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
            {
                if (!host.IsVisible) ClosePage();
                else if (observing) Refresh();
            }

            private void OnRegistryChanged()
            {
                // A routed button or keyboard event can arrive during capture. Defer structural changes
                // until the current Noesis route is finished, and update existing rows in place.
                if (refreshQueued) return;
                refreshQueued = true;
                if (!UnityMainThreadDispatch.TryEnqueue(RefreshQueued))
                    refreshQueued = false;
            }

            private void RefreshQueued()
            {
                refreshQueued = false;
                if (observing) Refresh();
            }

            private bool EnsureView()
            {
                if (open != null) return true;
                for (int index = 0; index < host.Children.Count; index++)
                {
                    if (!(host.Children[index] is ListView candidate) || candidate.Name != "HotKeyList") continue;
                    vanillaList = candidate;
                    break;
                }
                tooltipStyle = host.TryFindResource("APISharedModKeybindOptionsTooltip") as Style;
                buttonStyle = host.TryFindResource("APISharedModKeybindOptionsButton") as Style;
                if (vanillaList == null || tooltipStyle == null || buttonStyle == null)
                {
                    if (!missingAnchorReported)
                    {
                        missingAnchorReported = true;
                        InterceptionDiagnostics.Report("mod keybind options", new InvalidOperationException(
                            "The game's keyboard settings list or the Mod Keybinds resources were not found."));
                    }
                    return false;
                }
                originalListMargin = vanillaList.Margin;

                open = CreateButton("APISharedModKeybindsOpen", "Title", "OpenHelp", OpenPage);
                open.Width = 350;
                open.Height = 28;
                open.Margin = new Thickness(23, 0, 0, 114);
                open.HorizontalAlignment = HorizontalAlignment.Left;
                open.VerticalAlignment = VerticalAlignment.Bottom;
                open.Visibility = Visibility.Collapsed;

                rowsPanel = new StackPanel();
                var scroll = new ScrollViewer
                {
                    Content = rowsPanel,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Margin = new Thickness(0, 46, 0, 47)
                };
                var heading = Label(Text("Title"), 24);
                heading.HorizontalAlignment = HorizontalAlignment.Center;
                heading.VerticalAlignment = VerticalAlignment.Top;
                prompt = Label(string.Empty, 15);
                prompt.Margin = new Thickness(0, 0, 88, 0);
                prompt.VerticalAlignment = VerticalAlignment.Bottom;
                back = CreateButton("APISharedModKeybindsBack", "Back", "BackHelp", ClosePage);
                back.HorizontalAlignment = HorizontalAlignment.Right;
                back.VerticalAlignment = VerticalAlignment.Bottom;

                var content = new Grid();
                content.Children.Add(heading);
                content.Children.Add(scroll);
                content.Children.Add(prompt);
                content.Children.Add(back);
                page = new Border
                {
                    Name = "APISharedModKeybindsPage",
                    Background = new SolidColorBrush(Color.FromArgb(255, 29, 23, 16)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 242, 212, 138)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12),
                    Child = content,
                    Visibility = Visibility.Collapsed
                };
                KeyboardNavigation.SetTabNavigation(page, KeyboardNavigationMode.Cycle);
                page.PreviewKeyDown += CaptureKey;
                page.PreviewMouseDown += CaptureMouse;
                host.Children.Add(open);
                host.Children.Add(page);
                return true;
            }

            private void SetEntryVisible(bool value)
            {
                if (open == null) return;
                Visibility visibility = value ? Visibility.Visible : Visibility.Collapsed;
                if (open.Visibility == visibility) return;
                if (value)
                {
                    originalListMargin = vanillaList.Margin;
                    vanillaList.Margin = new Thickness(originalListMargin.Left, originalListMargin.Top,
                        originalListMargin.Right, originalListMargin.Bottom + 32);
                }
                else
                    vanillaList.Margin = originalListMargin;
                open.Visibility = visibility;
            }

            private void OpenPage()
            {
                page.Visibility = Visibility.Visible;
                Refresh();
                back.Focus();
            }

            private void ClosePage()
            {
                if (capture != null)
                    ModKeybinds.CancelCapture(capture);
                if (page == null || page.Visibility == Visibility.Collapsed) return;
                page.Visibility = Visibility.Collapsed;
                if (host.IsVisible && open.IsVisible) open.Focus();
            }

            private void Refresh()
            {
                var registrations = ModKeybinds.Registrations;
                if (registrations.Count == 0)
                {
                    ClosePage();
                    SetEntryVisible(false);
                    return;
                }
                if (!host.IsVisible || !EnsureView()) return;
                SetEntryVisible(true);
                if (page.Visibility != Visibility.Visible) return;

                if (rows.Count != registrations.Count)
                {
                    rowsPanel.Children.Clear();
                    rows.Clear();
                    foreach (var group in registrations.GroupBy(registration => registration.OwnerGuid)
                        .OrderBy(group => group.First().ModName, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(group => group.Key, StringComparer.Ordinal))
                    {
                        var groupHeading = Label(group.First().ModName, 19);
                        groupHeading.Margin = new Thickness(0, 12, 0, 5);
                        rowsPanel.Children.Add(groupHeading);
                        foreach (var registration in group.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                            .ThenBy(item => item.Id, StringComparer.Ordinal))
                            rows.Add(registration, CreateRow(registration));
                    }
                }

                foreach (var entry in rows)
                {
                    var registration = entry.Key;
                    var row = entry.Value;
                    bool canEdit = registration.CanEdit && capture == null;
                    KeybindChord chord = registration.Current;
                    row.Value.Text = chord.Key == UnityEngine.KeyCode.None ? Text("Unassigned") : ModKeybinds.Format(chord);
                    row.Conflict.Visibility = ModKeybinds.HasConflict(registration) ? Visibility.Visible : Visibility.Collapsed;
                    row.Assign.IsEnabled = canEdit;
                    row.Clear.IsEnabled = canEdit;
                    row.Reset.IsEnabled = canEdit;
                }
                prompt.Text = capture == null ? string.Empty : Text("Capture");
            }

            private Row CreateRow(ModKeybindRegistration registration)
            {
                var title = Label(registration.DisplayName, 16);
                var value = Label(string.Empty, 16);
                var conflict = Label(Text("Conflict"), 14);
                conflict.Visibility = Visibility.Collapsed;
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                var assign = CreateButton(null, "Assign", "AssignHelp", () => Assign(registration));
                var clear = CreateButton(null, "Clear", "ClearHelp", () => registration.TrySet(default));
                var reset = CreateButton(null, "Reset", "ResetHelp", () => registration.TrySet(registration.Default));
                buttons.Children.Add(assign);
                buttons.Children.Add(clear);
                buttons.Children.Add(reset);
                var body = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
                body.Children.Add(title);
                body.Children.Add(value);
                body.Children.Add(conflict);
                body.Children.Add(buttons);
                rowsPanel.Children.Add(body);
                return new Row(value, conflict, assign, clear, reset);
            }

            private void Assign(ModKeybindRegistration registration)
            {
                if (capture != null || !registration.CanEdit) return;
                if (!ModKeybinds.BeginCapture(registration, () => observing && page.IsVisible && registration.CanEdit, FinishCapture))
                    return;
                capture = registration;
                back.Focus();
                Refresh();
            }

            private void FinishCapture()
            {
                capture = null;
                OnRegistryChanged();
            }

            private void CaptureKey(object sender, KeyEventArgs args)
            {
                if (capture != null && ModKeybinds.TryCapture(args)) args.Handled = true;
                else if (args.Key == Key.Escape)
                {
                    ClosePage();
                    args.Handled = true;
                }
            }

            private void CaptureMouse(object sender, MouseButtonEventArgs args)
            {
                if (capture == null) return;
                if (back.IsMouseOver)
                    ClosePage();
                else if (ModKeybinds.TryCapture(args))
                    args.Handled = true;
            }

            private Button CreateButton(string name, string label, string help, Action click)
            {
                var button = new Button
                {
                    Content = Text(label),
                    Style = buttonStyle,
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 5, 0),
                    MinHeight = 25
                };
                if (name != null) button.Name = name;
                button.Click += (sender, args) => click();
                var tip = new ToolTip { Content = Text(help), Style = tooltipStyle };
                SharedToolTipSizing.SetEnabled(tip, true);
                ToolTipService.SetToolTip(button, tip);
                ToolTipService.SetShowDuration(button, 60000);
                return button;
            }

            private static TextBlock Label(string value, int size) => new TextBlock
            {
                Text = value,
                FontSize = size,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 246, 228, 187)),
                TextWrapping = TextWrapping.Wrap
            };

            private static string Text(string key) => UiText.Get("ModKeybinds." + key);

            private sealed class Row
            {
                internal readonly TextBlock Value, Conflict;
                internal readonly Button Assign, Clear, Reset;

                internal Row(TextBlock value, TextBlock conflict, Button assign, Button clear, Button reset)
                {
                    Value = value;
                    Conflict = conflict;
                    Assign = assign;
                    Clear = clear;
                    Reset = reset;
                }
            }
        }
    }
}
