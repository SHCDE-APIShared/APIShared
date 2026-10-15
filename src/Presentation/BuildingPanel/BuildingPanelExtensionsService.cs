using APIShared.Events;
using APIShared.Presentation;
using BepInEx.Logging;
using CrusaderDE;
using Noesis;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;

namespace APIShared
{
    // Shared owner of the selected building's extension stack (FW-23). Dormant until the first registration:
    // then it hooks MainViewModel.setUpInbuilding (Post) and takes the internal GuiChecks Post, both on the
    // Unity thread. Each refresh reads the selected building from the game state and walks the registry; the
    // host StackPanel is created once per HUD_Buildings instance under its root grid, so no refresh searches
    // the visual tree or uses reflection. Registrations, ordering and isolation live in the registry.
    internal sealed partial class BuildingPanelExtensionsService
    {
        internal const string HostName = "APISharedBuildingPanelExtensionsHost";
        private delegate void SetUpCall(MainViewModel self, int overridePanel, int overrideType);
        private readonly object installSync = new object();
        private readonly ManualLogSource log;
        private readonly BuildingPanelExtensionRegistry<FrameworkElement> registry;
        private readonly ManagedInterceptionHook<SetUpCall> setUpHook = new ManagedInterceptionHook<SetUpCall>();
        private readonly HashSet<string> loggedFailures = new HashSet<string>(StringComparer.Ordinal);
        private readonly NoesisView view = new NoesisView();
        private readonly Action<FatControler> guiPost;
        private bool hooksInstalled, refreshing;
        private string hooksFailure;
        private HUD_Buildings observedHud;
        private BuildingPanelExtensionContext context;

        private BuildingPanelExtensionsService(ManualLogSource logger)
        {
            log = logger;
            registry = new BuildingPanelExtensionRegistry<FrameworkElement>(ReportFailure);
            guiPost = OnGuiChecks;
        }

        // Startup: the members the refresh binds (MemberContract) and the setUpInbuilding hook target are
        // resolved now, so a changed game or Script Extender makes this capability ValidationFailed at once.
        // No hook is installed here; the service stays dormant until the first registration.
        internal static bool TryCreate(ManualLogSource log, out BuildingPanelExtensionsService service,
            out NativeCapabilityDiagnostic diagnostic)
        {
            service = null;
            if (!ManagedMemberContracts.TryValidate(NativeCapabilityIds.BuildingPanelExtensions, log, out diagnostic))
                return false;
            var target = typeof(MainViewModel).GetMethod(nameof(MainViewModel.setUpInbuilding),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(int), typeof(int) }, null);
            if (target == null || target.ReturnType != typeof(void))
            {
                diagnostic = Diagnostic(NativeCapabilityState.ValidationFailed,
                    "The refresh hook target MainViewModel.setUpInbuilding(int, int) is missing or changed.");
                try { NativeApiLog.Error(log, "capability=" + NativeCapabilityIds.BuildingPanelExtensions + ", status=unavailable, reason=" + diagnostic.Reason); }
                catch { }
                return false;
            }
            service = new BuildingPanelExtensionsService(log);
            return true;
        }

        internal IBuildingPanelExtensionsCapability Bind(string owner) => new Binding(this, owner);

        private static NativeCapabilityDiagnostic Diagnostic(NativeCapabilityState state, string reason) =>
            new NativeCapabilityDiagnostic(NativeCapabilityIds.BuildingPanelExtensions, state, string.Empty, reason);

        private bool Register(string owner, BuildingPanelExtensionDefinition definition,
            out IBuildingPanelExtensionRegistration registration, out NativeCapabilityDiagnostic diagnostic)
        {
            registration = null;
            if (definition == null || string.IsNullOrWhiteSpace(definition.ExtensionId) || definition.Factory == null)
            { diagnostic = Diagnostic(NativeCapabilityState.ValidationFailed, "An extension ID and a factory are required."); return false; }
            if (!EnsureHooks(out string failure))
            { diagnostic = Diagnostic(NativeCapabilityState.Faulted, failure); return false; }
            Func<BuildingPanelExtensionContext, FrameworkElement> factory = definition.Factory;
            if (!registry.TryAdd(owner, definition.ExtensionId, definition.Order, item => Fresh(factory((BuildingPanelExtensionContext)item)),
                definition.AppliesTo, definition.Update, out var entry, out string reason))
            { diagnostic = Diagnostic(NativeCapabilityState.ValidationFailed, reason); return false; }
            registration = entry;
            diagnostic = Diagnostic(NativeCapabilityState.Available, "Building panel extension registered for the process lifetime.");
            return true;
        }

        private static FrameworkElement Fresh(FrameworkElement element)
        {
            if (element != null && element.Parent != null) throw new InvalidOperationException("The factory returned an element that already has a parent.");
            return element;
        }

        // First registration only. A failure is kept: later registrations report it instead of retrying hooks.
        private bool EnsureHooks(out string failure)
        {
            lock (installSync)
            {
                failure = hooksFailure;
                if (hooksInstalled || failure != null) return hooksInstalled;
                try
                {
                    setUpHook.Install(typeof(MainViewModel), nameof(MainViewModel.setUpInbuilding), typeof(void),
                        new[] { typeof(int), typeof(int) }, OnSetUp, "BuildingPanelExtensions.setUpInbuilding");
                    PresentationEvents.SetGuiChecksPost(guiPost);
                    hooksInstalled = true;
                    TryLog(false, "Building panel extension refresh hooks installed (setUpInbuilding and GuiChecks Post).");
                    return true;
                }
                catch (Exception ex)
                {
                    failure = hooksFailure = "Building panel refresh hooks are unavailable: " + ex.Message;
                    TryLog(true, failure + " " + ex);
                    return false;
                }
            }
        }

        private void OnSetUp(MainViewModel self, int overridePanel, int overrideType)
        {
            setUpHook.Original(self, overridePanel, overrideType);
            Refresh(self);
        }

        private void OnGuiChecks(FatControler controller) => Refresh(MainViewModel.viewModelLoaded ? MainViewModel.Instance : null);

        // Refresh path: allocation-free unless the HUD instance changed (Attach) or a callback failed.
        private void Refresh(MainViewModel main)
        {
            if (refreshing || !registry.HasEntries) return;
            refreshing = true;
            try
            {
                HUD_Buildings hud = main?.HUDBuildingPanel;
                if (!ReferenceEquals(hud, observedHud)) Attach(hud);
                if (view.Host == null) return;
                BuildingPanelSelection selection = default;
                bool selected = TryReadSelection(main, out selection);
                registry.Apply(view, context, selection, selected);
            }
            catch (Exception ex) { ReportFailure(null, "refresh", ex); }
            finally { refreshing = false; }
        }

        // Cold path, once per HUD_Buildings instance: the host is created under the panel's root grid
        // (InBuildingLayoutRoot, the UserControl's content), so later refreshes never search for it.
        private void Attach(HUD_Buildings hud)
        {
            observedHud = hud;
            view.Host = null;
            context = null;
            registry.Reset();
            if (hud == null) return;
            if (!(hud.Content is Panel root)) throw new InvalidOperationException("The building panel root is not a Panel.");
            var host = new StackPanel
            {
                Name = HostName, Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 12, 0), Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(host, 40);
            root.Children.Add(host);
            view.Host = host;
            context = new BuildingPanelExtensionContext(hud);
        }

        // The building Vanilla shows in the in-building panel: alive, with a global ID. Map editor excluded.
        private static unsafe bool TryReadSelection(MainViewModel main, out BuildingPanelSelection selection)
        {
            selection = default;
            if (main == null || !main.Show_HUD_Building || main.IsMapEditorMode) return false;
            GameData data = GameData.Instance;
            EngineInterface.PlayState state = data?.lastGameState;
            if (state == null || state.app_mode != (int)Enums.AppModes.APP_MODE_IN_BUILDING || state.in_structure <= 0) return false;
            GameBuildingManagerAPI buildings = GameBuildingManagerAPI.Instance;
            if (buildings == null || !buildings.TryGetBuildingById(state.in_structure, out GameBuilding* building) ||
                building == null || building->r_AliveState != AliveState.IsAlive || building->r_GlobalId == 0) return false;
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            selection = new BuildingPanelSelection(state.in_structure, unchecked((int)building->r_GlobalId),
                building->r_PlayerIdOwner, players != null ? players.GetLocalPlayerId() : 0,
                building->r_BuildingType, state.app_sub_mode);
            return true;
        }

        // Cold path: one error per extension and phase (and one for the shared refresh).
        private void ReportFailure(BuildingPanelExtensionRegistry<FrameworkElement>.Entry entry, string phase, Exception error)
        {
            string key = (entry == null ? "*" : entry.Key) + "|" + phase;
            if (!loggedFailures.Add(key)) return;
            TryLog(true, entry == null
                ? "Building panel extension refresh failed; it retries on the next refresh: " + error
                : "Building panel extension " + entry.Key + " disabled after its " + phase + " failed; RequestContentRefresh re-enables it: " + error);
        }

        private void TryLog(bool error, string message)
        {
            try { if (error) NativeApiLog.Error(log, message); else NativeApiLog.Debug(log, message); }
            catch { }
        }

        private sealed class NoesisView : IBuildingPanelView<FrameworkElement>
        {
            internal StackPanel Host;
            public void Insert(FrameworkElement element, int index) =>
                Host.Children.Insert(Math.Min(index, Host.Children.Count), element);
            public void Remove(FrameworkElement element) => Host.Children.Remove(element);
            public void SetShown(FrameworkElement element, bool shown) =>
                element.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            public void SetHostShown(bool shown) => Host.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }

        private sealed class Binding : IBuildingPanelExtensionsCapability
        {
            private readonly BuildingPanelExtensionsService service;
            private readonly string owner;
            internal Binding(BuildingPanelExtensionsService service, string owner) { this.service = service; this.owner = owner; }
            public bool TryRegisterExtension(BuildingPanelExtensionDefinition definition,
                out IBuildingPanelExtensionRegistration registration, out NativeCapabilityDiagnostic diagnostic) =>
                service.Register(owner, definition, out registration, out diagnostic);
            public void SetOwnerActive(bool active) => service.registry.SetOwnerActive(owner, active);
        }
    }
}
