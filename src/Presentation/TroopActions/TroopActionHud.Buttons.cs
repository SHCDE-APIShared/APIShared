using System;
using System.Collections.Generic;
using System.Windows.Input;
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
        private static readonly TroopActionPresentationRetry presentationRetry = new TroopActionPresentationRetry();
        private static IUiFrameWorkRegistration buttonWork;
        private static IDisposable buttonInput, buttonMissionEnd;
        private static HUD_Troops buttonPanel;
        private static MainHUD buttonRoot;
        private static IngameUIScreens inputRoot;
        private static Canvas overlay;
        private static Grid navigation;
        private static Button nextPageButton, previousPageButton;
        private static TroopActionPagingSurface pagingSurface;
        private static TroopActionPagingDecision pagingDecision;
        private static TroopActionButtonRegistry.State[] renderedStates;
        private static LocalSelectionSnapshot buttonSelection;
        private static int currentPage;
        private static long renderGeneration;
        private static long renderedLayoutRevision;
        private static int visualColour = -1, visualPlayer = -1;

        /// <summary>Registers a permanent owner-local action for Vanilla's free slots and additional HUD pages.
        /// Call on the validated Unity thread after managed APIShared initialization. Older providers do not expose this method.</summary>
        /// <remarks>Initially hidden. Legacy hosts retain their existing slot assignments and reserve their positions on every page.
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
            if (!presentationRetry.CanAttempt(main.HUDTroopPanel, main.HUDRoot, snapshot))
            { HideButtons(true); return; }
            try { EnsureButtonOverlay(main); }
            catch
            {
                presentationRetry.RecordFailure(main.HUDTroopPanel, main.HUDRoot, snapshot);
                throw;
            }
            if (!ReferenceEquals(buttonSelection, selection)) ResetPage();
            buttonSelection = selection;
            bool appearanceChanged = visualColour != context.PlayerColour || visualPlayer != context.PlayerId;
            visualColour = context.PlayerColour; visualPlayer = context.PlayerId;
            NotifyContexts(snapshot, true);
            // Context callbacks may publish new visibility. Use their latest immutable view.
            snapshot = buttons.Capture(out long layoutRevision);
            eligibleButtons.Clear();
            foreach (var state in snapshot)
            {
                ButtonVisual visual = GetButtonVisual(state, context, appearanceChanged);
                visual.Seen = false;
                if (state.Visible && !visual.Failed) eligibleButtons.Add(visual);
            }
            TroopActionPageSlots slots = pagingSurface.CaptureSlots();
            if (!slots.IsReady || slots.FirstSlots.Count == 0 &&
                (slots.FallbackNavigation.Width <= 0 || slots.FallbackNavigation.Height <= 0))
            { HideButtons(true); return; }
            Rect anchor = slots.FirstSlots.Count > 0 ? slots.FirstSlots[0] : slots.FallbackNavigation;
            // The navigation remains at the first-page anchor on all pages.
            var laterSlots = slots.LaterSlots;
            if (slots.FirstSlots.Count > 0)
                for (int i = laterSlots.Count - 1; i >= 0; i--)
                    if (SamePosition(laterSlots[i], anchor)) laterSlots.RemoveAt(i);
            pagingDecision = TroopActionPagingPolicy.Decide(eligibleButtons.Count, slots.FirstSlots.Count, laterSlots.Count);
            currentPage = pagingDecision.ClampPage(currentPage);
            var slice = pagingDecision.GetSlice(currentPage);
            pagingSurface.SetVanillaHidden(currentPage != 0);
            overlay.Visibility = Visibility.Visible;
            GetOverlayScale(out float scaleX, out float scaleY);
            var pageSlots = currentPage == 0 ? slots.FirstSlots : laterSlots;
            int offset = currentPage == 0 && pagingDecision.HasNavigation && slots.FirstSlots.Count > 0 ? 1 : 0;
            renderGeneration++;
            renderedStates = snapshot;
            renderedLayoutRevision = layoutRevision;
            for (int i = 0; i < eligibleButtons.Count; i++)
            {
                ButtonVisual visual = eligibleButtons[i];
                if (i < slice.Start || i >= slice.Start + slice.Count) { HideVisual(visual); continue; }
                int position = i - slice.Start + offset;
                PlaceIn(visual.Host, overlay);
                visual.Host.Margin = new Thickness(0);
                visual.Host.LayoutTransform = new ScaleTransform(scaleX, scaleY);
                PositionAtScreen(visual.Host, pageSlots[position].X, pageSlots[position].Y);
                visual.Seen = true; visual.Page = currentPage;
                visual.Host.Visibility = Visibility.Visible;
                // Some Vanilla styles animate the inner Button; the wrapper is authoritative.
                visual.Button.Visibility = Visibility.Visible;
                visual.Button.IsEnabled = visual.State.Enabled;
                visual.DisabledHover.Visibility = visual.State.Enabled ? Visibility.Collapsed : Visibility.Visible;
                if (visual.Hovered && visual.Entry.Definition.HoverChanged == null) ShowButtonRollover(visual, visual.State.Tooltip);
            }
            foreach (var pair in buttonVisuals) if (!pair.Value.Seen) HideVisual(pair.Value);
            navigation.Visibility = pagingDecision.HasNavigation ? Visibility.Visible : Visibility.Collapsed;
            if (pagingDecision.HasNavigation)
            {
                float width = pagingDecision.UsesFallbackAnchor ? 15 : 36;
                navigation.Width = width;
                navigation.LayoutTransform = new ScaleTransform(scaleX, scaleY);
                PositionAtScreen(navigation, anchor.X, anchor.Y);
                if (nextPageButton.IsMouseOver) ShowNavigationRollover(nextPageButton, true);
                else if (previousPageButton.IsMouseOver) ShowNavigationRollover(previousPageButton, false);
            }
            else { buttonHover.Close(nextPageButton); buttonHover.Close(previousPageButton); }
        }

        private static bool SamePosition(Rect left, Rect right) =>
            Math.Abs(left.X - right.X) < 0.5f && Math.Abs(left.Y - right.Y) < 0.5f;

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
            if (!InteractiveAncestors(controls) || !InteractiveAncestors(unitControls) || back?.Visibility == Visibility.Visible) return false;
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
            try
            {
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
                pagingSurface = TroopActionPagingSurface.Create(buttonPanel);
                navigation = new Grid { Width = 36, Height = 37, UseLayoutRounding = false,
                    Visibility = Visibility.Collapsed };
                navigation.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                navigation.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                nextPageButton = CreatePageButton(true);
                previousPageButton = CreatePageButton(false);
                Grid.SetRow(previousPageButton, 1);
                navigation.Children.Add(nextPageButton); navigation.Children.Add(previousPageButton);
                overlay.Children.Add(navigation);
                inputRoot.PreviewKeyDown += OnRootKeyDown;
            }
            catch
            {
                ResetButtons();
                throw;
            }
        }

        private static Button CreatePageButton(bool next)
        {
            var button = new Button {
                Style = buttonPanel.TryFindResource("APISharedTroopPageButtonStyle") as Style
                    ?? throw new InvalidOperationException("The troop page button style is unavailable."),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                // The template rotates the sprites while its hit surface stays axis-aligned.
                Tag = new RotateTransform(next ? 180 : 0)
            };
            PropEx.SetSprite1(button, buttonPanel.TryFindResource("UI-Buttons J028") as ImageSource);
            PropEx.SetSprite2(button, buttonPanel.TryFindResource("UI-Buttons J029") as ImageSource);
            PropEx.SetSprite3(button, buttonPanel.TryFindResource("UI-Buttons J030") as ImageSource);
            ToolTipService.SetIsEnabled(button, false);
            button.Click += (_, __) => ChangePage(next ? 1 : -1);
            button.MouseEnter += (_, __) => ShowNavigationRollover(button, next);
            button.MouseLeave += (_, __) => buttonHover.Close(button);
            return button;
        }

        private static void ShowNavigationRollover(Button button, bool next) =>
            ShowButtonRollover(button, UiText.Get(next ? "TroopActions.NextPage" : "TroopActions.PreviousPage") +
                " (" + (currentPage + 1) + "/" + pagingDecision.PageCount + ")");

        private static void ChangePage(int direction)
        {
            if (!TryButtonContext(out var main, out _, out var selected) ||
                !ReferenceEquals(selected, buttonSelection) || !ReferenceEquals(main.HUDRoot, buttonRoot) ||
                !ReferenceEquals(main.HUDTroopPanel, buttonPanel)) return;
            // Vanilla occupancy and consumer visibility may have changed since the last frame.
            RenderButtons();
            if (renderedStates == null || !pagingDecision.HasNavigation || !TryButtonContext(out _, out _, out selected) ||
                !ReferenceEquals(selected, buttonSelection)) return;
            currentPage = pagingDecision.Move(currentPage, direction);
            foreach (var pair in buttonVisuals) HideVisual(pair.Value);
            buttonHover.Close();
            RenderButtons();
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

        private static void ResetPage()
        {
            currentPage = 0; renderGeneration++; renderedStates = null;
            pagingSurface?.SetVanillaHidden(false);
            buttonHover.Close();
        }

        private static void HideButtons(bool reset)
        {
            if (reset) ResetPage();
            if (overlay != null) overlay.Visibility = Visibility.Collapsed;
            foreach (var pair in buttonVisuals) HideVisual(pair.Value);
        }

        private static void ResetButtons()
        {
            NotifyContexts(buttons.Capture(), false); HideButtons(true);
            if (inputRoot != null) inputRoot.PreviewKeyDown -= OnRootKeyDown;
            pagingSurface?.Restore(); pagingSurface = null;
            if (overlay?.Parent is Panel parent) parent.Children.Remove(overlay);
            buttonVisuals.Clear(); eligibleButtons.Clear(); overlay = null; buttonPanel = null; buttonRoot = null; inputRoot = null;
            navigation = null; nextPageButton = previousPageButton = null;
            buttonSelection = null; visualColour = visualPlayer = -1;
            pagingDecision = default;
            presentationRetry.Clear();
        }

        private static void ReturnToFirstPage()
        {
            if (currentPage == 0) return;
            HideButtons(true);
            buttonWork?.Request();
        }

        private static void OnRootKeyDown(object sender, KeyEventArgs args)
        { if (args.Key == Key.Escape) ReturnToFirstPage(); }
        private static void OnButtonInput(UnityInputEventArgs args)
        {
            if (args.Phase == EventHookPhase.Pre && args.Key == UnityEngine.KeyCode.Escape) ReturnToFirstPage();
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
            internal int Version = -1, Page = -1;
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
                long generation = renderGeneration;
                int page = currentPage;
                TroopActionCommandExecution.TryInvoke(visual.Entry.Definition.Command, expectedButton,
                    () => Authorized(generation, page), null,
                    error => ReportButtonFailure(visual.Entry.Owner + "/command", error));
            }
            private bool Authorized(long generation, int page) => generation == renderGeneration && page == currentPage &&
                visual.Seen && visual.Page == currentPage && renderedStates != null &&
                ReferenceEquals(visual.Button, expectedButton) && buttons.IsExecutable(visual.Entry, renderedLayoutRevision) &&
                !visual.Failed && visual.Host?.IsVisible == true &&
                buttonVisuals.TryGetValue(visual.Entry, out var live) && ReferenceEquals(live, visual) &&
                TryButtonContext(out var main, out _, out var selected) && ReferenceEquals(main.HUDRoot, buttonRoot) &&
                ReferenceEquals(main.HUDTroopPanel, buttonPanel) && ReferenceEquals(selected, buttonSelection);
        }
    }
}
