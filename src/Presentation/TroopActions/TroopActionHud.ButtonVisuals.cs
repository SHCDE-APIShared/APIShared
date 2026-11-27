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
        private static ButtonVisual GetButtonVisual(TroopActionButtonRegistry.State state, UnitHudActionButtonContext context, bool appearanceChanged)
        {
            if (!buttonVisuals.TryGetValue(state.Entry, out ButtonVisual visual))
            { visual = new ButtonVisual(state.Entry); buttonVisuals.Add(state.Entry, visual); }
            visual.State = state;
            if (visual.Version == state.Version && !appearanceChanged) return visual;
            HideVisual(visual);
            if (visual.Host?.Parent is Panel parent) parent.Children.Remove(visual.Host);
            visual.Version = state.Version; visual.Failed = true; visual.Host = null; visual.Button = null;
            visual.ScaleX = visual.ScaleY = 0;
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

        private sealed class ButtonVisual
        {
            internal readonly TroopActionButtonRegistry.Entry Entry;
            internal TroopActionButtonRegistry.State State;
            internal Grid Host;
            internal Button Button;
            internal Border DisabledHover;
            internal int Version = -1, Page = -1;
            internal float ScaleX, ScaleY;
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
