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

        private static void ResetPage()
        {
            currentPage = 0; renderGeneration++; renderedStates = null;
            pagingSurface?.SetVanillaHidden(false);
            buttonHover.Close();
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
    }
}
