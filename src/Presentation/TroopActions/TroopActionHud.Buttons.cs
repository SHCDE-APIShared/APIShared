using System;
using System.Collections.Generic;
using System.Windows.Input;
using BepInEx.Configuration;
using CrusaderDE;
using Noesis;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Input;
using APIShared.Localization;
using APIShared.Events;

namespace APIShared.Presentation
{
    public static partial class TroopActionHud
    {
        private static readonly TroopActionButtonRegistry buttons = new TroopActionButtonRegistry(ButtonsChanged);
        private static readonly Dictionary<TroopActionButtonRegistry.Entry, ButtonVisual> buttonVisuals =
            new Dictionary<TroopActionButtonRegistry.Entry, ButtonVisual>();
        private static readonly List<ButtonVisual> eligibleButtons = new List<ButtonVisual>();
        private static readonly UnitHudHoverTextPresenter buttonHover = new UnitHudHoverTextPresenter();
        private static IUiFrameWorkRegistration buttonWork;
        private static IDisposable buttonInput, buttonMissionEnd;
        private static TroopActionMenuPreference menuPreference;
        private static HUD_Troops buttonPanel;
        private static MainHUD buttonRoot;
        private static IngameUIScreens inputRoot;
        private static Canvas overlay;
        private static Border menuBorder;
        private static UniformGrid menuGrid;
        private static ScrollViewer menuScroll;
        private static CheckBox autoClose;
        private static TextBlock autoCloseLabel;
        private static Button moreButton;
        private static LocalSelectionSnapshot buttonSelection;
        private static LocalSelectionSnapshot openedSelection;
        private static bool menuOpen;
        private static string renderedLocale;
        private static int visualColour = -1, visualPlayer = -1;

        internal static void ConfigurePreference(ConfigFile config) =>
            menuPreference = new TroopActionMenuPreference(config, error => ReportButtonFailure("menu preference", error));

        /// <summary>Registers a permanent owner-local action for Vanilla's free slots and overflow menu.
        /// Call on the validated Unity thread after managed APIShared initialization. Older providers do not expose this method.</summary>
        /// <remarks>Initially hidden. Legacy hosts retain their existing slot assignments and are not moved into the menu.
        /// Factory/command/context/hover callbacks run on Unity outside registry locks; errors isolate the entry.
        /// Selection, disabled state and actual visibility are rechecked before invoking a command with its Button parameter.
        /// There is no implicit network authorization or command transport. No unregistration or hook teardown.</remarks>
        public static bool TryRegisterButton(string ownerGuid, TroopActionButtonDefinition definition,
            out ITroopActionButtonRegistration registration, out string reason)
        {
            registration = null;
            if (!Internal.UnityMainThreadDispatch.IsMainThread)
            { reason = "Troop-action registration requires the initialized Unity thread."; return false; }
            if (string.IsNullOrWhiteSpace(ownerGuid) || definition == null || string.IsNullOrWhiteSpace(definition.ButtonId) ||
                definition.Command == null || string.IsNullOrWhiteSpace(definition.Tooltip) || definition.ButtonFactory == null || definition.Priority < 0)
            { reason = "Owner, ID, command, tooltip, factory and nonnegative priority are required."; return false; }
            if (setupHook == null && !TryInstall(out reason)) return false;
            if (!EnsureButtonRuntime(out reason)) return false;
            return buttons.TryRegister(ownerGuid, definition, out registration, out reason);
        }

        private static bool EnsureButtonRuntime(out string reason)
        {
            if (buttonWork == null && !UiWorkRuntime.TryRegister(APISharedPlugin.PluginGuid, "internal/35-troop-actions",
                RenderButtons, 0, out buttonWork, out reason)) return false;
            if (buttonInput == null) buttonInput = InputR3EventHooks.OnKeyDown.Observable.Subscribe(OnButtonInput);
            if (buttonMissionEnd == null)
            {
                Internal.MissionEvents.SetOwner(APISharedPlugin.PluginGuid);
                buttonMissionEnd = Internal.MissionEvents.Ended.Subscribe(_ =>
                    Internal.UnityMainThreadDispatch.TryRunInlineOrEnqueue(ResetButtons));
            }
            reason = string.Empty; return true;
        }

        private static void ButtonsChanged()
        {
            // Workers only request. Publishing demand from competing captured snapshots could
            // let an older hidden state overwrite a newer visible state. Render owns continuous demand.
            buttonWork?.Request();
        }

        private static void RenderButtons()
        {
            try { RenderButtonsCore(); }
            catch (Exception error) { HideButtons(true); ReportButtonFailure("troop-action presentation", error); }
            finally
            {
                bool visible = false;
                foreach (var state in buttons.Capture()) if (state.Visible) { visible = true; break; }
                buttonWork?.SetContinuous(visible);
            }
        }

        private static void RenderButtonsCore()
        {
            var snapshot = buttons.Capture();
            bool any = false;
            foreach (var state in snapshot) if (state.Visible) { any = true; break; }
            if (!any) { NotifyContexts(snapshot, false); HideButtons(true); return; }
            if (!TryButtonContext(out MainViewModel main, out UnitHudActionButtonContext context, out LocalSelectionSnapshot selection))
            { NotifyContexts(snapshot, false); HideButtons(true); return; }
            EnsureButtonOverlay(main);
            if (!ReferenceEquals(buttonSelection, selection)) CloseMenu();
            buttonSelection = selection;
            bool appearanceChanged = visualColour != context.PlayerColour || visualPlayer != context.PlayerId;
            visualColour = context.PlayerColour; visualPlayer = context.PlayerId;
            NotifyContexts(snapshot, true);
            eligibleButtons.Clear();
            foreach (var state in snapshot)
            {
                ButtonVisual visual = GetButtonVisual(state, context, appearanceChanged);
                visual.Seen = false;
                if (state.Visible && !visual.Failed) eligibleButtons.Add(visual);
            }
            var slots = TroopActionHudLayout.AvailableButtonSlots(buttonPanel);
            var decision = TroopActionOverflowPolicy.Decide(eligibleButtons.Count, slots.Count);
            if (!decision.HasMore) CloseMenu();
            overlay.Visibility = Visibility.Visible;
            GetOverlayScale(out float scaleX, out float scaleY);
            for (int i = 0; i < eligibleButtons.Count; i++)
            {
                ButtonVisual visual = eligibleButtons[i];
                bool inMenu = i >= decision.DirectCount;
                if (inMenu && !menuOpen) { HideVisual(visual); continue; }
                Panel target = inMenu ? (Panel)menuGrid : overlay;
                PlaceIn(visual.Host, target);
                if (inMenu && target.Children.IndexOf(visual.Host) != i - decision.DirectCount)
                {
                    target.Children.Remove(visual.Host);
                    target.Children.Insert(i - decision.DirectCount, visual.Host);
                }
                visual.Host.Margin = inMenu ? new Thickness(2) : new Thickness(0);
                visual.Host.LayoutTransform = inMenu ? null : new ScaleTransform(scaleX, scaleY);
                if (!inMenu) PositionAtScreen(visual.Host, slots[i].X, slots[i].Y);
                visual.Seen = true;
                visual.Host.Visibility = Visibility.Visible;
                // Some Vanilla styles animate the inner Button; the wrapper is the authoritative gate.
                visual.Button.Visibility = Visibility.Visible;
                visual.Button.IsEnabled = visual.State.Enabled;
                visual.DisabledHover.Visibility = visual.State.Enabled ? Visibility.Collapsed : Visibility.Visible;
                if (visual.Hovered && visual.Entry.Definition.HoverChanged == null) ShowButtonRollover(visual, visual.State.Tooltip);
            }
            foreach (var pair in buttonVisuals) if (!pair.Value.Seen) HideVisual(pair.Value);
            moreButton.Visibility = decision.HasMore ? Visibility.Visible : Visibility.Collapsed;
            if (decision.HasMore)
            {
                moreButton.LayoutTransform = new ScaleTransform(scaleX, scaleY);
                if (decision.UsesFallbackAnchor)
                {
                    Point top = buttonPanel.PointToScreen(new Point(buttonPanel.ActualWidth - 36, -41));
                    PositionAtScreen(moreButton, top.X, top.Y);
                }
                else PositionAtScreen(moreButton, slots[decision.DirectCount].X, slots[decision.DirectCount].Y);
            }
            else buttonHover.Close(moreButton);
            menuBorder.Visibility = menuOpen && decision.HasMore ? Visibility.Visible : Visibility.Collapsed;
            if (menuOpen && decision.HasMore) LayoutMenu(decision.OverflowCount, scaleX, scaleY);
            UpdateMenuLocale();
        }

        private static bool TryButtonContext(out MainViewModel main, out UnitHudActionButtonContext context, out LocalSelectionSnapshot selection)
        {
            main = null; context = null; selection = null;
            if (!MainViewModel.viewModelLoaded) return false;
            main = MainViewModel.Instance;
            if (main?.HUDmain == null || main.HUDRoot == null || main.HUDTroopPanel == null || !main.Show_InGameUI ||
                !main.Show_HUD_Troops || FatControler.currentScene != Enums.SceneIDS.ActualMainGame) return false;
            if (main.Show_HUD_IngameMenu || main.Show_HUD_Options || main.Show_HUD_OptionsMP ||
                main.Show_HUD_WorkshopUploader || main.Show_HUD_MPInviteWarning || main.Show_HUD_MPConnectionIssue ||
                main.Show_HUD_MPResync || main.Show_HUD_FreebuildMenu ||
                main.Show_HUD_LoadSaveRequester || main.Show_HUD_LoadSaveRequesterMP ||
                main.Show_HUD_LoadSaveRequesterTrails || main.Show_HUD_Confirmation || main.Show_HUD_ConfirmationMP ||
                main.Show_HUD_ConfirmationSM || main.Show_HUD_ConfirmationSands || main.Show_HUD_MissionOver ||
                main.Show_HUD_Help || main.Show_HUD_Briefing) return false;
            var controls = main.HUDTroopPanel.FindName("TroopSelectionControls") as FrameworkElement;
            var unitControls = main.HUDTroopPanel.FindName("UnitControls") as FrameworkElement;
            var back = main.HUDTroopPanel.FindName("UnitBack") as FrameworkElement;
            if (!InteractiveAncestors(controls) || !InteractiveAncestors(unitControls) || back?.IsVisible == true) return false;
            int player = GameModes.GameModeHelper.IsMapEditor() ? EditorDirector.instance?.ActivePlayerID ?? -1 : GamePlayerManagerAPI.Instance?.GetLocalPlayerId() ?? -1;
            if (!LocalSelectionAPI.TryCapture(player, out selection) || selection.Count == 0) return false;
            int colour = UnitHudPresentationService.ResolveActionColour(SpriteMapping.RemapMPLoadedColour(player), SpriteMapping.remapColours);
            if (colour < 1) return false;
            context = new UnitHudActionButtonContext(main.HUDTroopPanel, player, colour); return true;
        }

        private static bool InteractiveAncestors(FrameworkElement element)
        {
            if (element == null) return false;
            for (DependencyObject item = element; item != null; item = VisualTreeHelper.GetParent(item))
                if (item is UIElement ui && (!ui.IsVisible || !ui.IsHitTestVisible || ui.Opacity <= 0)) return false;
            return true;
        }

        private static void EnsureButtonOverlay(MainViewModel main)
        {
            if (ReferenceEquals(buttonPanel, main.HUDTroopPanel) && ReferenceEquals(buttonRoot, main.HUDRoot) && overlay != null) return;
            ResetButtons();
            buttonPanel = main.HUDTroopPanel; buttonRoot = main.HUDRoot;
            // MainHUD is bottom-sized to its HUD contents, not to the viewport. The
            // enclosing IngameUIScreens grid has the audited full-screen UI dimensions.
            for (DependencyObject node = buttonRoot; node != null; node = VisualTreeHelper.GetParent(node))
                if (node is IngameUIScreens screens) { inputRoot = screens; break; }
            if (!(inputRoot?.Content is Grid root)) throw new InvalidOperationException("In-game screen root grid is unavailable.");
            overlay = new Canvas { Name = "APISharedTroopActionsOverlay", Visibility = Visibility.Collapsed, ClipToBounds = false };
            Grid.SetRowSpan(overlay, Math.Max(1, root.RowDefinitions.Count));
            Grid.SetColumnSpan(overlay, Math.Max(1, root.ColumnDefinitions.Count));
            Panel.SetZIndex(overlay, 1); root.Children.Add(overlay);
            moreButton = new Button { Width = 36, Height = 37, Style = buttonPanel.TryFindResource("BTN_Image") as Style,
                RenderTransformOrigin = new Point(0.5f, 0.5f), RenderTransform = new RotateTransform(90) };
            PropEx.SetSprite1(moreButton, buttonPanel.TryFindResource("UI-Buttons J028") as ImageSource);
            PropEx.SetSprite2(moreButton, buttonPanel.TryFindResource("UI-Buttons J029") as ImageSource);
            PropEx.SetSprite3(moreButton, buttonPanel.TryFindResource("UI-Buttons J030") as ImageSource);
            PropEx.SetSprite4(moreButton, buttonPanel.TryFindResource("UI-Buttons J028") as ImageSource);
            ToolTipService.SetIsEnabled(moreButton, false);
            moreButton.Click += (sender, args) => {
                if (!TryButtonContext(out _, out _, out var selected) || !ReferenceEquals(selected, buttonSelection)) return;
                if (menuOpen) CloseMenu(); else { menuOpen = true; openedSelection = selected; }
                buttonWork.Request();
            };
            moreButton.MouseEnter += (_, __) => ShowButtonRollover(moreButton, UiText.Get("TroopActions.More"));
            moreButton.MouseLeave += (_, __) => buttonHover.Close(moreButton);
            overlay.Children.Add(moreButton);
            menuGrid = new UniformGrid { Columns = 1, HorizontalAlignment = HorizontalAlignment.Left };
            menuScroll = new ScrollViewer { Content = menuGrid, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            autoCloseLabel = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(255, 239, 243, 198)) };
            autoClose = new CheckBox { Content = autoCloseLabel, Style = buttonPanel.TryFindResource("CheckBox_Template") as Style,
                Margin = new Thickness(2, 6, 2, 0), IsChecked = menuPreference?.Value ?? true, DataContext = main };
            ToolTipService.SetIsEnabled(autoClose, false);
            autoClose.Checked += (_, __) => SaveMenuPreference(true);
            autoClose.Unchecked += (_, __) => SaveMenuPreference(false);
            autoClose.MouseEnter += (_, __) => ShowButtonRollover(autoClose, UiText.Get("TroopActions.AutoCloseHelp"));
            autoClose.MouseLeave += (_, __) => buttonHover.Close(autoClose);
            var stack = new StackPanel(); stack.Children.Add(menuScroll); stack.Children.Add(autoClose);
            menuBorder = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                Background = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), Child = stack, Visibility = Visibility.Collapsed };
            Panel.SetZIndex(menuBorder, 1); overlay.Children.Add(menuBorder);
            inputRoot.PreviewMouseDown += OnRootMouseDown;
            inputRoot.PreviewKeyDown += OnRootKeyDown;
        }

        private static void SaveMenuPreference(bool value)
        { menuPreference?.Set(value); }

        private static void UpdateMenuLocale()
        {
            string locale = GameLocale.Current();
            if (renderedLocale == locale) return;
            renderedLocale = locale;
            autoCloseLabel.Text = UiText.Get("TroopActions.AutoClose");
        }

        private static void LayoutMenu(int count, float scaleX, float scaleY)
        {
            Point panelTop = overlay.PointFromScreen(buttonPanel.PointToScreen(new Point(0, 0)));
            Point anchor = overlay.PointFromScreen(moreButton.PointToScreen(new Point(36, 0)));
            float bottom = TroopActionOverflowPolicy.MenuBottom(panelTop.Y, Canvas.GetTop(moreButton));
            float availableWidth = Math.Max(36, (overlay.ActualWidth - 8) / scaleX - 18);
            int columns = Math.Max(1, Math.Min(Math.Min(4, count), (int)(availableWidth / 40)));
            menuGrid.Columns = columns;
            float width = Math.Min(availableWidth, Math.Max(columns * 40, 210));
            autoCloseLabel.MaxWidth = Math.Max(20, width - 28);
            menuScroll.Width = width;
            // Reserve the footer before limiting the grid; scrolling preserves access to all registered actions.
            autoClose.Measure(new Size(width, float.PositiveInfinity));
            float footer = autoClose.DesiredSize.Height + 6;
            float maxHeight = Math.Max(37, (bottom - 4) / scaleY - footer - 18);
            menuScroll.MaxHeight = maxHeight;
            menuGrid.Width = columns * 40;
            menuBorder.LayoutTransform = new ScaleTransform(scaleX, scaleY);
            menuBorder.Measure(new Size((width + 18) * scaleX, overlay.ActualHeight));
            float menuWidth = menuBorder.DesiredSize.Width, menuHeight = menuBorder.DesiredSize.Height;
            Canvas.SetLeft(menuBorder, Math.Max(4, Math.Min(anchor.X - menuWidth, overlay.ActualWidth - menuWidth - 4)));
            Canvas.SetTop(menuBorder, Math.Max(4, bottom - menuHeight));
        }

        private static void GetOverlayScale(out float x, out float y)
        {
            Point zero = overlay.PointFromScreen(buttonPanel.PointToScreen(new Point(0, 0)));
            Point one = overlay.PointFromScreen(buttonPanel.PointToScreen(new Point(1, 1)));
            x = one.X - zero.X; y = one.Y - zero.Y;
            if (!(x > 0) || !(y > 0) || float.IsInfinity(x) || float.IsInfinity(y))
                throw new InvalidOperationException("Troop HUD scale is unavailable.");
        }

        private static void PositionAtScreen(FrameworkElement element, float x, float y)
        { Point point = overlay.PointFromScreen(new Point(x, y)); Canvas.SetLeft(element, point.X); Canvas.SetTop(element, point.Y); }

        private static void PlaceIn(FrameworkElement element, Panel target)
        {
            if (ReferenceEquals(element.Parent, target)) return;
            if (element.Parent is Panel previous) previous.Children.Remove(element);
            target.Children.Add(element);
        }

        private static ButtonVisual GetButtonVisual(TroopActionButtonRegistry.State state, UnitHudActionButtonContext context, bool appearanceChanged)
        {
            if (!buttonVisuals.TryGetValue(state.Entry, out ButtonVisual visual))
            { visual = new ButtonVisual(state.Entry); buttonVisuals.Add(state.Entry, visual); }
            visual.State = state;
            if (visual.Version == state.Version && !appearanceChanged) return visual;
            HideVisual(visual);
            if (visual.Host?.Parent is Panel parent) parent.Children.Remove(visual.Host);
            visual.Version = state.Version; visual.Failed = true; visual.Host = null; visual.Button = null;
            if (!state.Visible) { visual.Version = -1; return visual; }
            try
            {
                Button button = state.Entry.Definition.ButtonFactory(context);
                if (button == null || button.Parent != null) throw new InvalidOperationException("Factory must return a fresh unattached Button.");
                button.Width = 36; button.Height = 37; button.Margin = new Thickness(0);
                button.Command = new TroopButtonCommand(visual, button);
                button.CommandParameter = button;
                ToolTipService.SetIsEnabled(button, false);
                var host = new Grid { Width = 36, Height = 37, Visibility = Visibility.Collapsed, Background = Brushes.Transparent };
                var disabled = new Border { Background = Brushes.Transparent, Visibility = Visibility.Collapsed };
                host.Children.Add(button); host.Children.Add(disabled);
                host.MouseEnter += (_, __) => SetButtonHover(visual, true);
                host.MouseLeave += (_, __) => SetButtonHover(visual, false);
                visual.Button = button; visual.Host = host; visual.DisabledHover = disabled; visual.Failed = false;
            }
            catch (Exception error) { ReportButtonFailure(state.Entry.Owner + "/" + state.Entry.Definition.ButtonId + " factory", error); }
            return visual;
        }

        private static void NotifyContexts(TroopActionButtonRegistry.State[] snapshot, bool available)
        {
            foreach (var state in snapshot)
            {
                if (!buttonVisuals.TryGetValue(state.Entry, out ButtonVisual visual))
                { visual = new ButtonVisual(state.Entry); buttonVisuals.Add(state.Entry, visual); }
                bool next = available && state.Visible;
                if (visual.ContextAvailable == next) continue;
                visual.ContextAvailable = next;
                try { state.Entry.Definition.ContextChanged?.Invoke(next); }
                catch (Exception error) { ReportButtonFailure(state.Entry.Owner + "/context", error); }
            }
        }

        private static void SetButtonHover(ButtonVisual visual, bool hovered)
        {
            if (hovered && (visual.Failed || visual.Host?.IsVisible != true || !visual.State.Visible)) return;
            if (visual.Hovered == hovered) return;
            visual.Hovered = hovered;
            try
            {
                var callback = visual.Entry.Definition.HoverChanged;
                if (callback != null) callback(hovered);
                else if (hovered) ShowButtonRollover(visual, visual.State.Tooltip);
                else buttonHover.Close(visual);
            }
            catch (Exception error) { ReportButtonFailure(visual.Entry.Owner + "/hover", error); }
        }

        private static void ShowButtonRollover(object origin, string text)
        {
            if (!TryButtonContext(out var main, out _, out _) || buttonPanel?.RefTroopsPanelRollover == null) return;
            HUD_Troops panel = buttonPanel;
            buttonHover.Show(panel, origin, text,
                () => ReferenceEquals(main.HUDTroopPanel, panel) ? main.TroopsPanelRollover : null,
                value => {
                    main.TroopsPanelRollover = value;
                    main.TroopsPanelRollover_AmountReq1 = main.TroopsPanelRollover_AmountGot1 = string.Empty;
                    main.TroopsPanelRollover_GoodsImage1 = null;
                    panel.RefTroopsPanelRollover.Visibility = Visibility.Visible;
                    if (panel.RefTroopsPanelRollover2 != null) panel.RefTroopsPanelRollover2.Visibility = Visibility.Hidden;
                }, () => {
                    panel.RefTroopsPanelRollover.Visibility = Visibility.Hidden;
                    if (panel.RefTroopsPanelRollover2 != null) panel.RefTroopsPanelRollover2.Visibility = Visibility.Hidden;
                });
        }

        private static void HideVisual(ButtonVisual visual)
        { if (visual.Host != null) visual.Host.Visibility = Visibility.Collapsed; SetButtonHover(visual, false); visual.Seen = false; }

        private static void CloseMenu()
        {
            menuOpen = false; openedSelection = null;
            if (menuBorder != null) menuBorder.Visibility = Visibility.Collapsed;
            foreach (var pair in buttonVisuals)
                if (pair.Value.Host != null && ReferenceEquals(pair.Value.Host.Parent, menuGrid)) HideVisual(pair.Value);
            buttonHover.Close();
        }

        private static void HideButtons(bool close)
        {
            if (close) CloseMenu();
            if (overlay != null) overlay.Visibility = Visibility.Collapsed;
            foreach (var pair in buttonVisuals) HideVisual(pair.Value);
        }

        private static void ResetButtons()
        {
            NotifyContexts(buttons.Capture(), false); HideButtons(true);
            if (inputRoot != null) { inputRoot.PreviewMouseDown -= OnRootMouseDown; inputRoot.PreviewKeyDown -= OnRootKeyDown; }
            if (overlay?.Parent is Panel parent) parent.Children.Remove(overlay);
            buttonVisuals.Clear(); eligibleButtons.Clear(); overlay = null; buttonPanel = null; buttonRoot = null; inputRoot = null;
            moreButton = null; menuBorder = null; menuGrid = null; menuScroll = null; autoClose = null;
            buttonSelection = null; visualColour = visualPlayer = -1; renderedLocale = null;
        }

        private static void OnRootMouseDown(object sender, MouseButtonEventArgs args)
        { if (menuOpen && !menuBorder.IsMouseOver && !moreButton.IsMouseOver) CloseMenu(); }
        private static void OnRootKeyDown(object sender, KeyEventArgs args)
        { if (menuOpen && args.Key == Key.Escape) CloseMenu(); }
        private static void OnButtonInput(UnityInputEventArgs args)
        {
            if (args.Phase != EventHookPhase.Pre || !menuOpen) return;
            if (args.Key == UnityEngine.KeyCode.Escape ||
                args.Key == UnityEngine.KeyCode.Mouse0 && !menuBorder.IsMouseOver && !moreButton.IsMouseOver) CloseMenu();
        }

        private static void ReportButtonFailure(string identity, Exception error)
        { InterceptionDiagnostics.Report("Troop actions " + identity, error); }

        private sealed class ButtonVisual
        {
            internal readonly TroopActionButtonRegistry.Entry Entry;
            internal TroopActionButtonRegistry.State State;
            internal Grid Host;
            internal Button Button;
            internal Border DisabledHover;
            internal int Version = -1;
            internal bool Failed = true, Hovered, Seen;
            internal bool? ContextAvailable;
            internal ButtonVisual(TroopActionButtonRegistry.Entry entry) { Entry = entry; }
        }

        private sealed class TroopButtonCommand : ICommand
        {
            private readonly ButtonVisual visual;
            private readonly Button expectedButton;
            internal TroopButtonCommand(ButtonVisual visual, Button expectedButton) { this.visual = visual; this.expectedButton = expectedButton; }
            public event System.EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter)
            {
                TroopActionCommandExecution.TryInvoke(visual.Entry.Definition.Command, expectedButton, Authorized,
                    Completed, error => ReportButtonFailure(visual.Entry.Owner + "/command", error));
            }
            private bool Authorized() => ReferenceEquals(visual.Button, expectedButton) && buttons.IsExecutable(visual.Entry) &&
                !visual.Failed && visual.Host?.IsVisible == true &&
                buttonVisuals.TryGetValue(visual.Entry, out var live) && ReferenceEquals(live, visual) &&
                TryButtonContext(out var main, out _, out var selected) && ReferenceEquals(main.HUDRoot, buttonRoot) &&
                ReferenceEquals(main.HUDTroopPanel, buttonPanel) && ReferenceEquals(selected, buttonSelection) &&
                (!ReferenceEquals(visual.Host.Parent, menuGrid) || menuOpen && ReferenceEquals(selected, openedSelection));
            private static void Completed() { if (menuPreference?.Value ?? true) CloseMenu(); }
        }
    }
}
