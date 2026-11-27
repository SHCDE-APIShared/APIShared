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
        private static float navigationScaleX, navigationScaleY;
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
                ApplyScale(visual.Host, ref visual.ScaleX, ref visual.ScaleY, scaleX, scaleY);
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
                ApplyScale(navigation, ref navigationScaleX, ref navigationScaleY, scaleX, scaleY);
                PositionAtScreen(navigation, anchor.X, anchor.Y);
                if (nextPageButton.IsMouseOver) ShowNavigationRollover(nextPageButton, true);
                else if (previousPageButton.IsMouseOver) ShowNavigationRollover(previousPageButton, false);
            }
            else { buttonHover.Close(nextPageButton); buttonHover.Close(previousPageButton); }
        }

        // Only this HUD writes LayoutTransform on its hosts and navigation. Replacing an equal
        // transform every frame would invalidate Noesis layout without changing the result.
        // Scales are positive, so zero marks an element whose transform has not been set.
        private static void ApplyScale(FrameworkElement element, ref float appliedX, ref float appliedY, float x, float y)
        {
            if (appliedX == x && appliedY == y) return;
            element.LayoutTransform = new ScaleTransform(x, y);
            appliedX = x; appliedY = y;
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
            int player = LocalSelectionAPI.CurrentSelectionPlayerId();
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
            navigation = null; navigationScaleX = navigationScaleY = 0; nextPageButton = previousPageButton = null;
            buttonSelection = null; visualColour = visualPlayer = -1;
            pagingDecision = default;
            presentationRetry.Clear();
        }

        private static void ReportButtonFailure(string identity, Exception error)
        { InterceptionDiagnostics.Report("Troop actions " + identity, error); }
    }
}
