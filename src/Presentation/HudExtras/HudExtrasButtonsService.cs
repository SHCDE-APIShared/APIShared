using BepInEx.Logging;
using CrusaderDE;
using Noesis;
using SHCDESE.API;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace APIShared
{
    // Owns permanent registrations and rebuildable visuals, not gameplay command policy.
    // Application.onBeforeRender survives plugin cleanup; HUD property changes mark layout dirty.
    // Registry snapshots release the lock before invoking consumer factories/commands.
    internal sealed class HudExtrasButtonsService
    {
        internal const string HostName = "APISharedHudExtrasButtonsHost";
        private readonly object sync = new object();
        private readonly List<Registration> registrations = new List<Registration>();
        private readonly Dictionary<string, bool> ownerActivation = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<Registration, Visual> visuals = new Dictionary<Registration, Visual>();
        private readonly HashSet<string> loggedFailures = new HashSet<string>(StringComparer.Ordinal);
        private readonly ManualLogSource log;
        private volatile bool hasRegistrations, dirty = true;
        private MainViewModel observedMain;
        private Grid host;
        private Button next;
        private int page;
        private bool frameObserved;

        internal HudExtrasButtonsService(ManualLogSource logger) { log = logger; }
        // Published only once, rooted by ProcessInstance and the static Unity publisher.
        internal void Start() { UnityEngine.Application.onBeforeRender += OnBeforeRender; }
        internal IHudExtrasButtonsCapability Bind(string owner) => new Binding(this, owner);
        private static NativeCapabilityDiagnostic Diagnostic(NativeCapabilityState state, string reason) =>
            new NativeCapabilityDiagnostic(NativeCapabilityIds.HudExtrasButtons, state, string.Empty, reason);

        private bool Register(string owner, HudExtrasButtonDefinition definition,
            out IHudExtrasButtonRegistration registration, out NativeCapabilityDiagnostic diagnostic)
        {
            registration = null;
            if (definition == null || string.IsNullOrWhiteSpace(definition.ButtonId) || definition.Command == null || definition.ButtonFactory == null)
            { diagnostic = Diagnostic(NativeCapabilityState.ValidationFailed, "A button ID, command and factory are required."); return false; }
            lock (sync)
            {
                if (registrations.Any(x => x.Owner == owner && x.Definition.ButtonId == definition.ButtonId))
                { diagnostic = Diagnostic(NativeCapabilityState.ValidationFailed, "The owner already registered this button ID."); return false; }
                var item = new Registration(this, owner, definition);
                registrations.Add(item);
                registrations.Sort((a, b) => HudExtrasButtonLayout.Compare(a.Definition.Order, a.Owner, a.Definition.ButtonId,
                    b.Definition.Order, b.Owner, b.Definition.ButtonId));
                registration = item; hasRegistrations = true; dirty = true;
            }
            diagnostic = Diagnostic(NativeCapabilityState.Available, "Side-HUD button registered for the process lifetime."); return true;
        }

        private bool OwnerActive(string owner) => !ownerActivation.TryGetValue(owner, out bool active) || active;
        internal State[] CaptureStates()
        { lock (sync) return registrations.Select(x => new State(x, OwnerActive(x.Owner))).ToArray(); }

        private void OnBeforeRender()
        {
            if (!hasRegistrations) return;
            try
            {
                if (!frameObserved)
                { frameObserved = true; NativeApiLog.Debug(log, "Persistent side-HUD button callback is active after startup cleanup."); }
                MainViewModel main = MainViewModel.viewModelLoaded ? MainViewModel.Instance : null;
                if (!ReferenceEquals(observedMain, main))
                {
                    if (observedMain != null) observedMain.PropertyChanged -= OnHudPropertyChanged;
                    observedMain = main;
                    if (main != null) main.PropertyChanged += OnHudPropertyChanged;
                    ResetHost(); dirty = true;
                }
                Grid current = main == null ? null : GameXAMLManagerAPI.Instance.FindGlobalElement(HostName) as Grid;
                if (!ReferenceEquals(host, current)) { ResetHost(); host = current; dirty = true; }
                if (host == null) return;
                if (!main.Show_HUD_Extras) { Hide(); dirty = true; return; }
                if (dirty) Render(main);
            }
            catch (Exception ex) { Hide(); dirty = true; LogFailure("presentation", ex); }
        }

        private void OnHudPropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(MainViewModel.Show_HUD_Extras) ||
                args.PropertyName == nameof(MainViewModel.Show_HUD_Extras_Button_Objectves) ||
                args.PropertyName == nameof(MainViewModel.Show_HUD_Extras_Button_Freebuild)) dirty = true;
        }

        private void ResetHost()
        {
            Hide();
            if (host != null) host.Children.Clear();
            visuals.Clear(); host = null; next = null; page = 0;
        }
        private void Hide() { if (host != null) host.Visibility = Visibility.Collapsed; }

        private void Render(MainViewModel main)
        {
            State[] states;
            lock (sync) { dirty = false; states = registrations.Select(x => new State(x, OwnerActive(x.Owner))).ToArray(); }
            var context = new HudExtrasButtonContext(host);
            var available = new List<State>();
            foreach (State state in states)
            {
                if (visuals.TryGetValue(state.Item, out Visual old)) old.Container.Visibility = Visibility.Collapsed;
                if (!state.Visible) continue;
                Visual visual = GetVisual(state, context);
                if (!visual.Failed) available.Add(state);
            }
            page = HudExtrasButtonLayout.ClampPage(page, available.Count);
            bool overflow = available.Count > HudExtrasButtonLayout.PageSize;
            State[] shown = available.Skip(page * HudExtrasButtonLayout.PageSize).Take(HudExtrasButtonLayout.PageSize).ToArray();
            host.Margin = new Thickness(0, 0, 0, HudExtrasButtonLayout.BaseBottom(
                main.Show_HUD_Extras_Button_Objectves || main.Show_HUD_Extras_Button_Freebuild));
            host.Height = shown.Length * HudExtrasButtonLayout.Height + (overflow ? HudExtrasButtonLayout.ArrowHeight : 0);
            for (int index = 0; index < shown.Length; index++)
            {
                State state = shown[index]; Visual visual = visuals[state.Item];
                visual.Container.Margin = new Thickness(0, 0, 0, index * HudExtrasButtonLayout.Height);
                visual.Button.IsEnabled = state.Enabled; visual.Container.Visibility = Visibility.Visible;
            }
            if (overflow)
            {
                if (next == null)
                {
                    next = new Button { Width = HudExtrasButtonLayout.Width, Height = HudExtrasButtonLayout.ArrowHeight,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                        Style = RequireStyle("APISharedHudExtrasPageButtonStyle"), Content = new TextBlock { Text = "↑", FontSize = 15,
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false } };
                    ToolTipService.SetIsEnabled(next, false);
                    next.Click += (_, __) => {
                        if (!HostAvailable()) return;
                        page = HudExtrasButtonLayout.NextPage(page, CaptureStates().Count(x => x.Visible &&
                            visuals.TryGetValue(x.Item, out Visual v) && !v.Failed)); dirty = true;
                    };
                    host.Children.Add(next);
                }
                next.Margin = new Thickness(0, 0, 0, shown.Length * HudExtrasButtonLayout.Height);
                next.Visibility = Visibility.Visible;
            }
            else if (next != null) next.Visibility = Visibility.Collapsed;
            host.Visibility = shown.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private Style RequireStyle(string name) => host.TryFindResource(name) as Style ??
            throw new InvalidOperationException("Required side-HUD style is unavailable: " + name);

        private Visual GetVisual(State state, HudExtrasButtonContext context)
        {
            if (!visuals.TryGetValue(state.Item, out Visual visual) || visual.Version != state.Version)
            {
                if (visual != null) host.Children.Remove(visual.Container);
                bool created = TryCreateButton(state.Item.Definition.ButtonFactory, context, state.Item.Key, out Button button);
                if (!created)
                    button = new Button();
                visual = new Visual(button, state.Version) { Failed = !created };
                visuals[state.Item] = visual;
                if (visual.Failed) return visual;
                button.Width = HudExtrasButtonLayout.Width; button.Height = HudExtrasButtonLayout.Height;
                button.HorizontalAlignment = HorizontalAlignment.Left; button.VerticalAlignment = VerticalAlignment.Bottom;
                button.Style = button.Style ?? RequireStyle("BTN_Building");
                button.Command = new GuardedCommand(this, state.Item, button); button.CommandParameter = button;
                button.Margin = new Thickness(0); visual.Container.Children.Add(button); host.Children.Add(visual.Container);
            }
            if (visual.Failed || visual.TooltipVersion == state.TooltipVersion) return visual;
            try
            {
                ToolTipService.SetToolTip(visual.Button, null);
                if (!string.IsNullOrWhiteSpace(state.Tooltip))
                {
                    ToolTip tip = state.Item.Definition.TooltipFactory == null
                        ? new ToolTip { Style = RequireStyle("APISharedHudExtrasTooltipStyle") }
                        : state.Item.Definition.TooltipFactory(context);
                    if (tip == null || tip.Parent != null || (tip.Style == null && tip.Template == null))
                        throw new InvalidOperationException("Tooltip factory must return a fresh tooltip with an explicit Style or Template.");
                    tip.Content = state.Tooltip;
                    ToolTipService.SetToolTip(visual.Button, tip);
                }
                ToolTipService.SetShowDuration(visual.Button, 60000);
                visual.TooltipVersion = state.TooltipVersion;
            }
            catch (Exception ex) { visual.Failed = true; LogFailure("tooltip " + state.Item.Key, ex); }
            return visual;
        }

        internal bool TryCreateButton(Func<HudExtrasButtonContext, Button> factory, HudExtrasButtonContext context, string key, out Button button)
        {
            button = null;
            try
            {
                button = factory(context);
                if (button == null || button.Parent != null) throw new InvalidOperationException("Factory must return a fresh unattached Button.");
                return true;
            }
            catch (Exception ex) { button = null; LogFailure("button " + key, ex); return false; }
        }

        private bool HostAvailable() => MainViewModel.viewModelLoaded && ReferenceEquals(observedMain, MainViewModel.Instance) &&
            observedMain.Show_HUD_Extras && host != null && host.IsVisible && host.IsEnabled && host.IsHitTestVisible &&
            ReferenceEquals(host, GameXAMLManagerAPI.Instance.FindGlobalElement(HostName));
        private void LogFailure(string key, Exception ex)
        {
            if (!loggedFailures.Add(key)) return;
            try { NativeApiLog.Error(log, "Side-HUD " + key + " failed: " + ex); } catch { }
        }
        private sealed class Binding : IHudExtrasButtonsCapability
        {
            private readonly HudExtrasButtonsService service; private readonly string owner;
            internal Binding(HudExtrasButtonsService service, string owner) { this.service = service; this.owner = owner; }
            public bool TryRegisterButton(HudExtrasButtonDefinition definition, out IHudExtrasButtonRegistration registration, out NativeCapabilityDiagnostic diagnostic) =>
                service.Register(owner, definition, out registration, out diagnostic);
            public void SetOwnerActive(bool active) { lock (service.sync) { service.ownerActivation[owner] = active; service.dirty = true; } }
        }
        internal sealed class State
        {
            internal readonly Registration Item; internal readonly bool Visible, Enabled;
            internal readonly int Version, TooltipVersion; internal readonly string Tooltip;
            internal State(Registration item, bool ownerActive)
            { Item = item; Visible = ownerActive && item.Visible; Enabled = item.Enabled; Version = item.Version; TooltipVersion = item.TooltipVersion; Tooltip = item.Tooltip; }
        }
        internal sealed class Registration : IHudExtrasButtonRegistration
        {
            private readonly HudExtrasButtonsService service;
            internal readonly string Owner, Key; internal readonly HudExtrasButtonDefinition Definition;
            internal bool Visible = true, Enabled = true; internal int Version, TooltipVersion; internal string Tooltip;
            internal Registration(HudExtrasButtonsService service, string owner, HudExtrasButtonDefinition definition)
            { this.service = service; Owner = owner; Definition = definition; Key = owner + ":" + definition.ButtonId; Tooltip = definition.Tooltip; }
            public void SetVisible(bool visible) { lock (service.sync) { Visible = visible; service.dirty = true; } }
            public void SetEnabled(bool enabled) { lock (service.sync) { Enabled = enabled; service.dirty = true; } }
            public void SetTooltip(string tooltip) { lock (service.sync) { Tooltip = tooltip ?? string.Empty; TooltipVersion++; service.dirty = true; } }
            public void RequestContentRefresh() { lock (service.sync) { Version++; TooltipVersion++; service.dirty = true; } }
        }
        private sealed class Visual
        {
            internal readonly Button Button; internal readonly int Version; internal int TooltipVersion = -1; internal bool Failed;
            // BTN_Building animates Button.Visibility: page and owner gates must live on a parent.
            internal readonly Grid Container = new Grid { Width = HudExtrasButtonLayout.Width, Height = HudExtrasButtonLayout.Height,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
            internal Visual(Button button, int version) { Button = button; Version = version; }
        }
        private sealed class GuardedCommand : ICommand
        {
            private readonly HudExtrasButtonsService service; private readonly Registration item; private readonly Button button;
            internal GuardedCommand(HudExtrasButtonsService service, Registration item, Button button) { this.service = service; this.item = item; this.button = button; }
            public event System.EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter)
            {
                try
                {
                    lock (service.sync) if (!item.Visible || !item.Enabled || !service.OwnerActive(item.Owner)) return;
                    if (!service.HostAvailable() || !button.IsVisible || !button.IsEnabled ||
                        !service.visuals.TryGetValue(item, out Visual visual) || !ReferenceEquals(visual.Button, button) || visual.Failed) return;
                    if (item.Definition.Command.CanExecute(button)) item.Definition.Command.Execute(button);
                }
                catch (Exception ex) { service.LogFailure("command " + item.Key, ex); }
            }
        }
    }
}
