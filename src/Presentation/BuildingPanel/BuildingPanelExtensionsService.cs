using APIShared.Events;
using APIShared.Presentation;
using BepInEx.Logging;
using CrusaderDE;
using Noesis;
using R3;
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
        private IDisposable missionEndSubscription; // Process lifetime, rooted here.
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

        private bool Claim(string owner, BuildingPanelRecruitmentAreaDefinition definition,
            out IBuildingPanelRecruitmentAreaClaim claim, out NativeCapabilityDiagnostic diagnostic)
        {
            claim = null;
            if (definition == null || string.IsNullOrWhiteSpace(definition.ClaimId) || definition.Factory == null ||
                definition.BuildingTypes == null || definition.BuildingTypes.Length == 0)
            { diagnostic = Diagnostic(NativeCapabilityState.ValidationFailed, "A claim ID, at least one building type and a factory are required."); return false; }
            if (!EnsureHooks(out string failure))
            { diagnostic = Diagnostic(NativeCapabilityState.Faulted, failure); return false; }
            Func<BuildingPanelExtensionContext, FrameworkElement> factory = definition.Factory;
            if (!registry.TryClaim(owner, definition.ClaimId, definition.BuildingTypes,
                item => Fresh(factory((BuildingPanelExtensionContext)item)), definition.AppliesTo, definition.Update,
                out var created, out string reason, out string conflictOwner))
            {
                diagnostic = conflictOwner == null
                    ? Diagnostic(NativeCapabilityState.ValidationFailed, reason)
                    : new NativeCapabilityDiagnostic(NativeCapabilityIds.BuildingPanelExtensions, NativeCapabilityState.Conflict,
                        string.Empty, reason, conflictOwner);
                return false;
            }
            claim = created;
            diagnostic = Diagnostic(NativeCapabilityState.Available, "Recruitment area claimed until Release.");
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
                    APIShared.Internal.MissionEvents.SetOwner("APIShared_Serp");
                    missionEndSubscription = APIShared.Internal.MissionEvents.Ended.Subscribe(OnMissionEnded);
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

        // Mission end hands the recruitment area back to Vanilla; the panel may live on into the next mission.
        private void OnMissionEnded(MissionLifecycleNotification notification)
        {
            if (refreshing || view.Host == null) return;
            refreshing = true;
            try { registry.ReleaseActive(view); }
            catch (Exception ex) { ReportFailure("*", "mission end", ex); }
            finally { refreshing = false; }
        }

        // Refresh path: allocation-free unless the HUD instance changed (Attach) or a callback failed.
        private void Refresh(MainViewModel main)
        {
            if (refreshing || !registry.HasEntries) return;
            refreshing = true;
            try
            {
                HUD_Buildings hud = main?.HUDBuildingPanel;
                // A missing view model or panel only hides extensions and hands the area back to Vanilla; the
                // panel state survives, so the same panel returning is not attached twice.
                if (hud != null && !ReferenceEquals(hud, observedHud)) Attach(hud);
                if (view.Host == null) return;
                BuildingPanelSelection selection = default;
                bool selected = hud != null && TryReadSelection(main, out selection);
                registry.Apply(view, context, selection, selected);
            }
            catch (Exception ex) { ReportFailure("*", "refresh", ex); }
            finally { refreshing = false; }
        }

        // Cold path, once per HUD_Buildings instance: the host is created under the panel's root grid
        // (InBuildingLayoutRoot, the UserControl's content), so later refreshes never search for it.
        private void Attach(HUD_Buildings hud)
        {
            // Hand the old panel back to Vanilla first, in case it is still shown; then forget it.
            if (view.Host != null)
            {
                try { registry.Apply(view, context, default, false); }
                catch (Exception ex) { ReportFailure("*", "panel replacement", ex); }
            }
            observedHud = hud;
            view.Host = null;
            context = null;
            registry.Reset();
            if (!(hud.Content is Panel root)) throw new InvalidOperationException("The building panel root is not a Panel.");
            var vanilla = new FrameworkElement[] {
                hud.RefBarracksPanel, hud.RefMercPostPanel, hud.RefBedouinStockadePanel, hud.RefShowEngineersGuildPanel,
                hud.RefShowTunellersGuildPanel, hud.RefShowCathedralPanel, hud.RefHUDBuildingFullClickMask };
            for (int index = 0; index < vanilla.Length; index++)
                if (vanilla[index] == null) throw new InvalidOperationException("A Vanilla recruitment panel of the building panel is missing.");
            var host = new StackPanel
            {
                Name = HostName, Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 12, 0), Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(host, 40);
            root.Children.Add(host);
            view.Root = root;
            view.Vanilla = vanilla;
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

        // Cold path: one error per item and phase ("*" for the shared refresh and mission end).
        private void ReportFailure(string item, string phase, Exception error)
        {
            if (!loggedFailures.Add(item + "|" + phase)) return;
            TryLog(true, item == "*"
                ? "Building panel " + phase + " failed; it retries on the next refresh: " + error
                : "Building panel item " + item + " disabled after its " + phase + " failed; RequestContentRefresh re-enables it: " + error);
        }

        private void TryLog(bool error, string message)
        {
            try { if (error) NativeApiLog.Error(log, message); else NativeApiLog.Debug(log, message); }
            catch { }
        }

        // Fields are resolved once per HUD_Buildings instance in Attach; every operation is a direct property access.
        private sealed class NoesisView : IBuildingPanelView<FrameworkElement>
        {
            internal StackPanel Host;
            internal Panel Root;
            internal FrameworkElement[] Vanilla;
            public void Insert(FrameworkElement element, int index) =>
                Host.Children.Insert(Math.Min(index, Host.Children.Count), element);
            public void Remove(FrameworkElement element) => Host.Children.Remove(element);
            public void SetShown(FrameworkElement element, bool shown) =>
                element.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            public void SetHostShown(bool shown) => Host.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            public void InsertArea(FrameworkElement element)
            {
                Panel.SetZIndex(element, 35);
                Root.Children.Add(element);
            }
            public void RemoveArea(FrameworkElement element) => Root.Children.Remove(element);
            public int VanillaPanelCount => Vanilla.Length;
            public BuildingPanelVisibility GetVanilla(int index)
            {
                Visibility value = Vanilla[index].Visibility;
                return value == Visibility.Visible ? BuildingPanelVisibility.Visible
                    : value == Visibility.Hidden ? BuildingPanelVisibility.Hidden : BuildingPanelVisibility.Collapsed;
            }
            public void SetVanilla(int index, BuildingPanelVisibility visibility) =>
                Vanilla[index].Visibility = visibility == BuildingPanelVisibility.Visible ? Visibility.Visible
                    : visibility == BuildingPanelVisibility.Hidden ? Visibility.Hidden : Visibility.Collapsed;
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
            public bool TryClaimRecruitmentArea(BuildingPanelRecruitmentAreaDefinition definition,
                out IBuildingPanelRecruitmentAreaClaim claim, out NativeCapabilityDiagnostic diagnostic) =>
                service.Claim(owner, definition, out claim, out diagnostic);
        }
    }
}
