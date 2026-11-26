using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CrusaderDE;
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
            private ControlTemplate listTemplate;
            private object originalTemplate;
            private GridViewColumn vanillaColumn;
            private DataTemplate cellTemplate, originalCellTemplate;
            private StackPanel footer;
            private Style rowStyle, tooltipStyle;
            private Grid editor;
            private FrameworkElement secondaryCaption;
            private Button choose, apply, clear, cancel;
            private Row selected;
            private Dialog dialog;
            private object previousDataContext, previousApplyEnabled;
            private BindingBase previousContextBinding;
            private Visibility previousCaptionVisibility;
            private KeyboardNavigationMode previousTabNavigation;
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
                RestoreListTemplate();
            }

            private void OnLoaded(object sender, RoutedEventArgs args) => Observe();

            private void OnUnloaded(object sender, RoutedEventArgs args) => StopObserving();

            private void Observe()
            {
                if (observing) return;
                observing = true;
                ModKeybinds.Changed += QueueRefresh;
                QueueRefresh();
            }

            private void StopObserving()
            {
                if (!observing) return;
                observing = false;
                ModKeybinds.Changed -= QueueRefresh;
                CloseEditor(false);
            }

            private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
            {
                if (!host.IsVisible) CloseEditor(false);
                else if (observing) QueueRefresh();
            }

            private void QueueRefresh()
            {
                // Applying a template or refreshing rows inside a routed event can invalidate the route.
                if (refreshQueued) return;
                refreshQueued = true;
                if (!UnityMainThreadDispatch.TryEnqueue(RefreshQueued))
                    refreshQueued = false;
            }

            private void RefreshQueued()
            {
                refreshQueued = false;
                if (observing && host.IsVisible) Refresh();
            }

            private bool EnsureFooter()
            {
                if (footer != null) return ReferenceEquals(vanillaList.Template, listTemplate);
                for (int index = 0; index < host.Children.Count; index++)
                {
                    if (!(host.Children[index] is ListView candidate) || candidate.Name != "HotKeyList") continue;
                    vanillaList = candidate;
                    break;
                }

                listTemplate = host.TryFindResource("APISharedHotKeyListTemplate") as ControlTemplate;
                cellTemplate = host.TryFindResource("APISharedHotKeyCellTemplate") as DataTemplate;
                rowStyle = host.TryFindResource("APISharedModHotKeyRow") as Style;
                tooltipStyle = host.TryFindResource("APISharedModKeybindOptionsTooltip") as Style;
                if (!(vanillaList?.View is GridView view) || view.Columns.Count != 1 ||
                    listTemplate == null || cellTemplate == null || rowStyle == null ||
                    tooltipStyle == null || !FindEditor())
                    return ReportMissingAnchor();

                // The item source must continue to contain only the game's HotKeyRow objects.
                // The original negative vertical margin clips text in a physically scrolled panel.
                vanillaColumn = view.Columns[0];
                originalCellTemplate = vanillaColumn.CellTemplate;
                originalTemplate = vanillaList.ReadLocalValue(Control.TemplateProperty);
                try
                {
                    vanillaColumn.CellTemplate = cellTemplate;
                    vanillaList.Template = listTemplate;
                    vanillaList.ApplyTemplate();
                    footer = listTemplate.FindName("APISharedModKeybindsFooter", vanillaList) as StackPanel;
                    if (footer != null) return true;
                }
                catch (Exception error)
                {
                    RestoreListTemplate();
                    return ReportMissingAnchor(error);
                }

                RestoreListTemplate();
                return ReportMissingAnchor();
            }

            private bool ReportMissingAnchor(Exception error = null)
            {
                if (!missingAnchorReported)
                {
                    missingAnchorReported = true;
                    InterceptionDiagnostics.Report("mod keybind options", error ?? new InvalidOperationException(
                        "The game's keyboard settings list or the Mod Keybinds resources were not found."));
                }
                return false;
            }

            private void RestoreListTemplate()
            {
                if (vanillaColumn != null && ReferenceEquals(vanillaColumn.CellTemplate, cellTemplate))
                    vanillaColumn.CellTemplate = originalCellTemplate;
                if (vanillaList == null || listTemplate == null || !ReferenceEquals(vanillaList.Template, listTemplate)) return;
                if (originalTemplate is ControlTemplate template) vanillaList.Template = template;
                else vanillaList.ClearValue(Control.TemplateProperty);
            }

            private void Refresh()
            {
                var registrations = ModKeybinds.Registrations;
                if (registrations.Count == 0 || !EnsureFooter()) return;
                if (selected == null && rows.Count != registrations.Count)
                {
                    footer.Children.Clear();
                    var heading = Label(Text("Title"), 15);
                    heading.FontWeight = FontWeights.Bold;
                    heading.Margin = new Thickness(3, 4, 3, 3);
                    footer.Children.Add(heading);

                    foreach (var registration in registrations.OrderBy(item => item.ModName, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(item => item.OwnerGuid, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal))
                    {
                        if (!rows.TryGetValue(registration, out Row row))
                        {
                            row = CreateRow(registration);
                            rows.Add(registration, row);
                        }
                        footer.Children.Add(row.Button);
                    }
                }

                foreach (var row in rows.Values)
                {
                    row.Title.Text = registrations.Count(item => item.DisplayName == row.Registration.DisplayName) > 1
                        ? row.Registration.DisplayName + " (" + row.Registration.ModName + ")" : row.Registration.DisplayName;
                    row.Value.Text = Format(row.Registration.Current);
                    row.Button.IsEnabled = row.Registration.CanEdit;
                }
                dialog?.Refresh();
            }

            private Row CreateRow(ModKeybindRegistration registration)
            {
                var titleLabel = Label(registration.DisplayName, 14);
                titleLabel.Margin = new Thickness(0, 0, 8, 0);
                var valueLabel = Label(Format(registration.Current), 14);
                valueLabel.MaxWidth = 135;
                valueLabel.HorizontalAlignment = HorizontalAlignment.Right;
                valueLabel.TextAlignment = TextAlignment.Right;

                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(valueLabel, 1);
                line.Children.Add(titleLabel);
                line.Children.Add(valueLabel);
                var button = new Button { Style = rowStyle, Content = line };
                SetTooltip(button, registration.ModName);
                var row = new Row(registration, button, titleLabel, valueLabel);
                button.Click += (sender, args) => OpenEditor(row);
                return row;
            }

            private bool FindEditor()
            {
                DependencyObject ancestor = host;
                while (ancestor != null && !(ancestor is HUD_Options))
                    ancestor = VisualTreeHelper.GetParent(ancestor);
                if (!(ancestor is HUD_Options options)) return false;
                editor = options.FindName("OptionsHotKeyPanel") as Grid;
                choose = options.FindName("APISharedHotKeyChoose") as Button;
                apply = options.FindName("OptionsHotKeyNewKeyApply") as Button;
                clear = options.FindName("APISharedHotKeyClear") as Button;
                cancel = options.FindName("APISharedHotKeyCancel") as Button;
                secondaryCaption = options.FindName("APISharedHotKeySecondaryCaption") as FrameworkElement;
                return editor != null && choose != null && apply != null && clear != null &&
                    cancel != null && secondaryCaption != null;
            }

            private void OpenEditor(Row row)
            {
                if (!row.Registration.CanEdit || selected != null || editor.Visibility == Visibility.Visible) return;
                selected = row;
                dialog = new Dialog(this, row);
                previousDataContext = editor.ReadLocalValue(FrameworkElement.DataContextProperty);
                previousContextBinding = BindingOperations.GetBindingBase(editor, FrameworkElement.DataContextProperty);
                previousApplyEnabled = apply.ReadLocalValue(UIElement.IsEnabledProperty);
                previousCaptionVisibility = secondaryCaption.Visibility;
                previousTabNavigation = KeyboardNavigation.GetTabNavigation(editor);

                // The same controls keep their existing bindings. A separate data context prevents
                // HUD_Options.Update from overwriting mod input or sending commands to a Vanilla slot.
                editor.DataContext = dialog;
                secondaryCaption.Visibility = Visibility.Hidden;
                apply.IsEnabled = true;
                KeyboardNavigation.SetTabNavigation(editor, KeyboardNavigationMode.Cycle);
                editor.PreviewKeyDown += OnEditorKey;
                editor.PreviewMouseDown += OnEditorMouse;
                editor.IsVisibleChanged += OnEditorVisibility;
                editor.Visibility = Visibility.Visible;
                choose.Focus();
            }

            private void CloseEditor(bool restoreFocus)
            {
                Row row = selected;
                if (row == null) return;
                selected = null;
                var previousDialog = dialog;
                dialog = null;
                ModKeybinds.CancelCapture(row.Registration);
                editor.PreviewKeyDown -= OnEditorKey;
                editor.PreviewMouseDown -= OnEditorMouse;
                editor.IsVisibleChanged -= OnEditorVisibility;
                editor.Visibility = Visibility.Hidden;
                if (ReferenceEquals(editor.DataContext, previousDialog))
                {
                    if (previousContextBinding != null)
                        BindingOperations.SetBinding(editor, FrameworkElement.DataContextProperty, previousContextBinding);
                    else if (ReferenceEquals(previousDataContext, DependencyProperty.UnsetValue))
                        editor.ClearValue(FrameworkElement.DataContextProperty);
                    else
                        editor.DataContext = previousDataContext;
                }
                if (previousApplyEnabled is bool enabled) apply.IsEnabled = enabled;
                else apply.ClearValue(UIElement.IsEnabledProperty);
                secondaryCaption.Visibility = previousCaptionVisibility;
                KeyboardNavigation.SetTabNavigation(editor, previousTabNavigation);
                previousDataContext = previousApplyEnabled = null;
                previousContextBinding = null;
                if (restoreFocus && host.IsVisible && row.Button.IsEnabled) row.Button.Focus();
                QueueRefresh();
            }

            private void OnEditorVisibility(object sender, DependencyPropertyChangedEventArgs args)
            {
                if (!editor.IsVisible) CloseEditor(false);
            }

            private void OnEditorKey(object sender, KeyEventArgs args)
            {
                if (dialog?.Capturing == true && ModKeybinds.TryCapture(args)) args.Handled = true;
                else if (args.Key == Key.Escape)
                {
                    CloseEditor(true);
                    args.Handled = true;
                }
            }

            private bool OverCaptureButton() => apply.IsMouseOver || clear.IsMouseOver || cancel.IsMouseOver;

            private void OnEditorMouse(object sender, MouseButtonEventArgs args)
            {
                if (dialog?.Capturing == true &&
                    !(args.ChangedButton == MouseButton.Left && OverCaptureButton()) &&
                    ModKeybinds.TryCapture(args))
                    args.Handled = true;
            }

            private void SetTooltip(Button button, string text)
            {
                var tip = new ToolTip { Content = text, Style = tooltipStyle };
                SharedToolTipSizing.SetEnabled(tip, true);
                ToolTipService.SetToolTip(button, tip);
                ToolTipService.SetShowDuration(button, 60000);
            }

            private static TextBlock Label(string value, int size) => new TextBlock
            {
                Text = value,
                FontSize = size,
                Foreground = new SolidColorBrush(Colors.Black),
                TextWrapping = TextWrapping.Wrap
            };

            private static string Format(KeybindChord chord) =>
                chord.Key == UnityEngine.KeyCode.None ? Text("Unassigned") : ModKeybinds.Format(chord);

            private static string Text(string key) => UiText.Get("ModKeybinds." + key);

            private sealed class Dialog : INotifyPropertyChanged, ICommand
            {
                // Command parameters from the original HUD_Options XAML.
                private const int Close = -101, Choose = -102, Reset = -103,
                    Cancel = -104, Apply = -105, Clear = -106;
                private readonly View view;
                private readonly Row row;
                private KeybindChord? pending;

                internal Dialog(View view, Row row)
                {
                    this.view = view;
                    this.row = row;
                }

                internal bool Capturing { get; private set; }
                public string OptionsHotKeyTitle => row.Title.Text;
                public string OptionsHotKey1 => Format(row.Registration.Current);
                public string OptionsHotKey2 => Text("Reset");
                public string OptionsHotKeyCurrentKey => OptionsHotKey1;
                public string OptionsHotKeyNewKey => pending.HasValue ? Format(pending.Value) : string.Empty;
                public string OptionsHotKeyWarning => ModKeybinds.HasConflict(row.Registration,
                    pending ?? row.Registration.Current) ? Text("Conflict") : string.Empty;
                public Visibility OptionsHotKeySelectVis => Capturing ? Visibility.Hidden : Visibility.Visible;
                public Visibility OptionsHotKeyChangeVis => Capturing ? Visibility.Visible : Visibility.Hidden;
                public ICommand OptionsCommand => this;

                public event PropertyChangedEventHandler PropertyChanged;
                public event System.EventHandler CanExecuteChanged;

                internal void Refresh()
                {
                    foreach (string property in new[] { nameof(OptionsHotKey1), nameof(OptionsHotKeyCurrentKey),
                        nameof(OptionsHotKeyNewKey), nameof(OptionsHotKeyWarning), nameof(OptionsHotKeySelectVis),
                        nameof(OptionsHotKeyChangeVis) })
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
                    CanExecuteChanged?.Invoke(this, System.EventArgs.Empty);
                }

                public bool CanExecute(object parameter)
                {
                    if (!ReferenceEquals(view.dialog, this)) return false;
                    switch (CommandId(parameter))
                    {
                        case Close: return !Capturing;
                        case Choose:
                        case Reset: return !Capturing && row.Registration.CanEdit;
                        case Cancel: return Capturing;
                        case Apply: return Capturing && pending.HasValue && row.Registration.CanEdit;
                        case Clear: return Capturing && row.Registration.CanEdit && row.Registration.Accepts(default);
                        default: return false;
                    }
                }

                public void Execute(object parameter)
                {
                    if (!CanExecute(parameter)) return;
                    switch (CommandId(parameter))
                    {
                        case Close: view.CloseEditor(true); break;
                        case Choose: Begin(); break;
                        case Reset: row.Registration.TrySet(row.Registration.Default); Refresh(); break;
                        case Cancel: ModKeybinds.CancelCapture(row.Registration); break;
                        case Apply: Commit(pending.Value); break;
                        case Clear: Commit(default); break;
                    }
                }

                private void Begin()
                {
                    if (!ModKeybinds.BeginCapture(row.Registration,
                        () => ReferenceEquals(view.dialog, this) && view.editor.IsVisible,
                        Finish, Preview, view.OverCaptureButton)) return;
                    Capturing = true;
                    Refresh();
                    view.cancel.Focus();
                }

                private void Preview(KeybindChord chord)
                {
                    if (pending.HasValue && pending.Value.Equals(chord)) return;
                    pending = chord;
                    Refresh();
                }

                private void Commit(KeybindChord chord)
                {
                    if (row.Registration.TrySet(chord)) ModKeybinds.CancelCapture(row.Registration);
                }

                private void Finish()
                {
                    Capturing = false;
                    pending = null;
                    if (!ReferenceEquals(view.dialog, this)) return;
                    Refresh();
                    view.choose.Focus();
                }

                private static int CommandId(object parameter) =>
                    int.TryParse(Convert.ToString(parameter, CultureInfo.InvariantCulture), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int command) ? command : 0;
            }

            private sealed class Row
            {
                internal readonly ModKeybindRegistration Registration;
                internal readonly Button Button;
                internal readonly TextBlock Title, Value;

                internal Row(ModKeybindRegistration registration, Button button, TextBlock title, TextBlock value)
                {
                    Registration = registration;
                    Button = button;
                    Title = title;
                    Value = value;
                }
            }
        }
    }
}
