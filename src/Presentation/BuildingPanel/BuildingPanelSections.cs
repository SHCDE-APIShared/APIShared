using System;
using System.Collections.Generic;
using System.Reflection;
using APIShared.Internal;
using CrusaderDE;
using Noesis;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Presentation
{
    /// <summary>Owner-bound sections inside Vanilla's building panels, with one shared XAML insertion point
    /// and one shared open-building detection.</summary>
    /// <remarks>
    /// <para>APIShared's <c>HUD_Buildings.xaml</c> patch adds the host <c>APISharedBuildingPanelSectionsHost</c>:
    /// an 800×170 Grid aligned right and bottom like Vanilla's building panels, directly after
    /// <c>FletchersPanel</c>. A section's factory creates its control once per HUD instance, when the HUD is
    /// first seen; APIShared adds it to the host collapsed and makes it visible only while a panel of one of the
    /// section's building types is open and <see cref="BuildingPanelSectionOptions.ShowWhen"/> agrees. Sections
    /// position themselves with Margin and alignment inside the host; APIShared arranges nothing and changes no
    /// Vanilla control.</para>
    /// <para>Detection runs after each completed in-game GUI check (<c>FatControler.NoesisGUIUpdateChecksInGame</c>,
    /// also while paused), only while at least one section is registered: the HUD shows a building panel, the
    /// panel's sub mode belongs to a building, and <c>in_structure</c> resolves to a living building with a
    /// nonzero GlobalId. The record is read without the engine monitor, like Vanilla's own HUD reads, so a
    /// building created or destroyed during that frame can be seen one frame late; commands must revalidate.</para>
    /// <para>All factories and callbacks run synchronously on the Unity thread, outside registry locks, in
    /// section order, each isolated: a failing factory disables its section for that HUD instance; a failing
    /// callback or condition hides it until the panel shows another context. Each failure is logged once.
    /// Registrations last for the process; no polling of its own is needed by a consumer. After the mission
    /// ends, Closed is delivered at the next in-game GUI check.</para>
    /// <para>Since 0.73.0 a presenter (<see cref="TryRegisterPresenter"/>) can show the panel for another building, for
    /// example another player's building for a spectator or foreign-control mod. While it reports one, sections see that
    /// building with <see cref="BuildingPanelContext.IsPresented"/> and the acting player, and route owner actions through
    /// <see cref="TrySubmitAction"/> to <see cref="Buildings.BuildingActions"/>.</para>
    /// </remarks>
    public static class BuildingPanelSections
    {
        internal const string HostName = "APISharedBuildingPanelSectionsHost";
        private static readonly BuildingPanelSectionRegistry registry = new BuildingPanelSectionRegistry();
        private static readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
        private static bool initialized;
        private static string initializationFailure;
        private static HUD_Buildings boundHud;
        private static Grid host;
        private static long generation;
        private static BuildingPanelContext current;

        /// <summary>Registers a section on the initialized Unity thread after Script Extender readiness. False with
        /// a reason for invalid or duplicate options or unavailable game members. The section joins at the next GUI
        /// check; its control is created then for the current HUD instance.</summary>
        public static bool TryRegister(BuildingPanelSectionOptions options, out BuildingPanelSectionRegistration registration,
            out string reason)
        {
            registration = null;
            if (!EnsureInitialized(out reason)) return false;
            if (!registry.TryRegister(options, out BuildingPanelSectionEntry entry, out reason)) return false;
            registration = new BuildingPanelSectionRegistration(entry);
            return true;
        }

        /// <summary>Reads the building of the open panel on the initialized Unity thread, whatever its type.
        /// True with a null context when no building panel is open; false when the thread or game members are
        /// unsuitable. The same instance is returned while nothing changes.</summary>
        public static bool TryGetContext(out BuildingPanelContext context)
        {
            context = null;
            if (!EnsureInitialized(out _)) return false;
            try { context = Read(MainViewModel.viewModelLoaded ? MainViewModel.Instance : null); return true; }
            catch (Exception error) { Report("context", error); return false; }
        }

        /// <summary>Since 0.73.0: registers a presenter on the initialized Unity thread after Script Extender readiness: a mod
        /// that shows Vanilla's building panel for another building than the local player's own selection. While it
        /// reports a building and the building HUD is shown, every section sees that building in its context, with
        /// <see cref="BuildingPanelContext.ActingPlayerId"/> set to the player the presenter acts for. False with a reason
        /// for invalid or duplicate options or unavailable game members.</summary>
        /// <remarks>Report no building while the presenter's panel is closed. Registrations last for the process.</remarks>
        public static bool TryRegisterPresenter(BuildingPanelPresenterOptions options, out string reason)
        {
            if (!EnsureInitialized(out reason)) return false;
            return registry.TryRegisterPresenter(options, out reason);
        }

        /// <summary>Since 0.73.0: Unity thread: a section's control asks to run a registered
        /// <see cref="Buildings.BuildingActions">building action</see> on a presented panel's building. The request goes to
        /// the presenter that still shows this building, which executes it through its own authorized path.</summary>
        /// <remarks>Only presented panels are routed: for Vanilla's own selection the action's mod uses its own path (for
        /// example its own Chore). The action must be registered and apply to the building's type.</remarks>
        /// <param name="context">The context of the section's current showing.</param>
        /// <param name="actionOwnerGuid">GUID of the mod that registered the action.</param>
        /// <param name="actionId">The action's owner-local ID.</param>
        /// <param name="argument">Action-defined value.</param>
        /// <param name="reason">Why the request was not accepted.</param>
        public static bool TrySubmitAction(BuildingPanelContext context, string actionOwnerGuid, string actionId, int argument,
            out string reason)
        {
            if (!UnityMainThreadDispatch.IsMainThread) { reason = "Building panel actions require the initialized Unity thread."; return false; }
            if (context == null || !context.IsPresented)
            { reason = "Only a presented building panel routes actions; use the action's own path for the local selection."; return false; }
            if (!Buildings.BuildingActions.AppliesTo(actionOwnerGuid, actionId, context.BuildingType))
            { reason = "No building action " + actionOwnerGuid + "/" + actionId + " applies to " + context.BuildingType + "."; return false; }
            BuildingPanelPresenterEntry presenter = null;
            foreach (BuildingPanelPresenterEntry entry in registry.Presenters)
                if (entry.Owner == context.PresenterOwnerGuid && entry.Id == context.PresenterRegistrationId) { presenter = entry; break; }
            // Only the ID is compared here; a reused ID of a destroyed building is rejected by the request's
            // GlobalId in BuildingActions.TryExecute.
            if (presenter == null || !TryQuery(presenter, out int shown, out int acting) ||
                shown != context.BuildingId || acting != context.ActingPlayerId)
            { reason = "The presenter no longer shows this building."; return false; }
            var request = new Buildings.BuildingActionRequest(actionOwnerGuid, actionId, context.ActingPlayerId, context.BuildingId,
                context.GlobalId, argument);
            try
            {
                if (presenter.Submit(request, out reason)) { reason = ""; return true; }
                reason = string.IsNullOrEmpty(reason) ? "The presenter refused the action." : reason;
                return false;
            }
            catch (Exception error)
            {
                Report("submit/" + presenter.Owner + "/" + presenter.Id, error);
                reason = "The presenter failed to submit the action.";
                return false;
            }
        }

        private static bool EnsureInitialized(out string reason)
        {
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Building panel sections require the initialized Unity thread."; return false; }
            if (initialized) { reason = ""; return true; }
            if (initializationFailure != null) { reason = initializationFailure; return false; }
            var contract = new ManagedMemberContract()
                .Field(typeof(MainViewModel), nameof(MainViewModel.viewModelLoaded), typeof(bool))
                .Field(typeof(MainViewModel), nameof(MainViewModel.HUDmain), typeof(HUD_Main))
                .Field(typeof(MainViewModel), nameof(MainViewModel.HUDBuildingPanel), typeof(HUD_Buildings))
                .Method(typeof(MainViewModel), "get_Instance", typeof(MainViewModel))
                .Method(typeof(MainViewModel), "get_Show_HUD_Building", typeof(bool))
                .Method(typeof(GameData), "get_Instance", typeof(GameData))
                .Method(typeof(GameData), "get_lastGameState", typeof(EngineInterface.PlayState))
                .Field(typeof(EngineInterface.PlayState), nameof(EngineInterface.PlayState.app_sub_mode), typeof(int))
                .Field(typeof(EngineInterface.PlayState), nameof(EngineInterface.PlayState.in_structure), typeof(int));
            var members = new List<MemberInfo>();
            reason = contract.FindMismatch(members);
            if (reason == null)
                foreach (MemberInfo member in members)
                    if ((member is FieldInfo field && !field.IsPublic) || (member is MethodInfo method && !method.IsPublic))
                    { reason = "Building panel sections require a public game member: " + member; break; }
            if (reason == null && !PresentationEvents.TryRegisterWhenActive(PresentationOperation.GuiChecks,
                APISharedPlugin.PluginGuid, "building-panel-sections", null, OnGuiChecks, out reason, 0, HasDemand))
                reason = "Building panel sections could not observe GUI checks: " + reason;
            // The successful registration above reports an empty reason, not null.
            if (!string.IsNullOrEmpty(reason)) { initializationFailure = reason; return false; }
            initialized = true;
            reason = "";
            return true;
        }

        private static bool HasDemand() => registry.Entries.Length != 0;

        private static void OnGuiChecks(PresentationPostEventArgs args)
        {
            if (!args.OriginalCompleted || !UnityMainThreadDispatch.IsMainThread) return;
            BuildingPanelSectionEntry[] entries = registry.Entries;
            if (entries.Length == 0) return;
            try
            {
                MainViewModel main = MainViewModel.viewModelLoaded ? MainViewModel.Instance : null;
                HUD_Buildings hud = main?.HUDBuildingPanel;
                if (!ReferenceEquals(hud, boundHud)) Rebind(hud, entries);
                if (host == null) return;
                BuildingPanelContext context = Read(main);
                bool any = false;
                foreach (BuildingPanelSectionEntry entry in entries)
                {
                    if (entry.ContentGeneration != generation) CreateContent(entry);
                    Step(entry, context);
                    any |= entry.Shown != null;
                }
                SetVisible(host, any);
            }
            catch (Exception error) { Report("update", error); }
        }

        // A new HUD instance: end every showing on the old controls, then let every section create
        // its control for the new host on this or a later check.
        private static void Rebind(HUD_Buildings hud, BuildingPanelSectionEntry[] entries)
        {
            Grid oldHost = host;
            foreach (BuildingPanelSectionEntry entry in entries)
            {
                if (entry.Shown != null) Close(entry);
                if (entry.Content != null)
                {
                    try { oldHost?.Children.Remove(entry.Content); }
                    catch (Exception error) { Report("release/" + entry.Owner + "/" + entry.Id, error); }
                }
                entry.Content = null;
                entry.Faulted = null;
            }
            boundHud = hud;
            current = null;
            generation++;
            host = hud?.FindName(HostName) as Grid;
            if (hud != null && host == null)
                Report("missing-host", new InvalidOperationException(
                    "The building panel section host is missing from HUD_Buildings; Vanilla's panels stay unchanged."));
        }

        private static void CreateContent(BuildingPanelSectionEntry entry)
        {
            entry.ContentGeneration = generation;
            FrameworkElement content = null;
            try
            {
                content = entry.Factory(new BuildingPanelSectionFactoryContext(host, generation));
                if (content == null) throw new InvalidOperationException("The factory returned no control.");
                content.Visibility = Visibility.Collapsed;
                host.Children.Add(content);
                entry.Content = content;
            }
            catch (Exception error)
            {
                entry.Content = null;
                Report("factory/" + entry.Owner + "/" + entry.Id, error);
            }
        }

        private static void Step(BuildingPanelSectionEntry entry, BuildingPanelContext context)
        {
            if (entry.Faulted != null && !ReferenceEquals(entry.Faulted, context)) entry.Faulted = null;
            bool show = context != null && entry.Content != null && entry.Faulted == null &&
                entry.Types.Contains(context.BuildingType) && Accepts(entry, context);
            switch (BuildingPanelSectionPolicy.Plan(entry.Shown?.Context, context, show))
            {
                case BuildingPanelSectionTransition.Open: Open(entry, context); break;
                case BuildingPanelSectionTransition.Update:
                    if (!Invoke(entry, entry.Updated, entry.Shown, "updated")) { entry.Faulted = context; Close(entry); }
                    break;
                case BuildingPanelSectionTransition.Close: Close(entry); break;
                case BuildingPanelSectionTransition.Reopen: Close(entry); Open(entry, context); break;
            }
        }

        private static bool Accepts(BuildingPanelSectionEntry entry, BuildingPanelContext context)
        {
            if (entry.ShowWhen == null) return true;
            try { return entry.ShowWhen(context); }
            catch (Exception error)
            {
                Report("condition/" + entry.Owner + "/" + entry.Id, error);
                entry.Faulted = context;
                return false;
            }
        }

        private static void Open(BuildingPanelSectionEntry entry, BuildingPanelContext context)
        {
            var args = new BuildingPanelSectionEventArgs(context, entry.Content);
            entry.Shown = args;
            SetVisible(entry.Content, true);
            if (Invoke(entry, entry.Opened, args, "opened")) return;
            entry.Faulted = context;
            entry.Shown = null;
            SetVisible(entry.Content, false);
        }

        private static void Close(BuildingPanelSectionEntry entry)
        {
            BuildingPanelSectionEventArgs args = entry.Shown;
            entry.Shown = null;
            if (entry.Content != null) SetVisible(entry.Content, false);
            Invoke(entry, entry.Closed, args, "closed");
        }

        private static bool Invoke(BuildingPanelSectionEntry entry, Action<BuildingPanelSectionEventArgs> callback,
            BuildingPanelSectionEventArgs args, string what)
        {
            if (callback == null) return true;
            try { callback(args); return true; }
            catch (Exception error) { Report(what + "/" + entry.Owner + "/" + entry.Id, error); return false; }
        }

        // The building of the open panel, or null. Reused while unchanged, so a new instance marks a change.
        // A presenter's building takes precedence over Vanilla's own selection while the building HUD is shown.
        private static BuildingPanelContext Read(MainViewModel main)
        {
            if (main?.HUDmain == null || main.HUDBuildingPanel == null || !main.Show_HUD_Building) return current = null;
            foreach (BuildingPanelPresenterEntry presenter in registry.Presenters)
                if (TryQuery(presenter, out int presented, out int acting))
                    return current = Context(presented, acting, presenter);
            EngineInterface.PlayState state = GameData.Instance?.lastGameState;
            if (state == null || state.in_structure <= 0 || !BuildingPanelSectionPolicy.IsBuildingSubMode(state.app_sub_mode))
                return current = null;
            return current = Context(state.in_structure, 0, null);
        }

        private static unsafe BuildingPanelContext Context(int id, int acting, BuildingPanelPresenterEntry presenter)
        {
            GameBuildingManagerAPI manager = GameBuildingManagerAPI.Instance;
            if (manager == null || !BuildingAccess.TryGetById(manager, id, out GameBuilding* building, out _) ||
                building == null || building->r_AliveState != AliveState.IsAlive || building->r_GlobalId == 0)
                return null;

            int owner = building->r_PlayerIdOwner;
            uint globalId = building->r_GlobalId;
            eStructs type = building->r_BuildingType;
            int local = PlayerPerspectiveAPI.GetControlledPlayerId(), viewed = PlayerPerspectiveAPI.GetViewedPlayerId();
            if (presenter == null) acting = local;
            string presenterOwner = presenter?.Owner, presenterId = presenter?.Id;
            if (current != null && current.Same(id, globalId, type, owner, local, viewed, generation, acting, presenterOwner, presenterId))
                return current;
            return new BuildingPanelContext(id, globalId, type, owner, local, viewed, generation, acting, presenterOwner, presenterId);
        }

        private static bool TryQuery(BuildingPanelPresenterEntry presenter, out int buildingId, out int actingPlayerId)
        {
            try
            {
                if (presenter.Query(out buildingId, out actingPlayerId) && buildingId > 0 && actingPlayerId > 0) return true;
            }
            catch (Exception error) { Report("presenter/" + presenter.Owner + "/" + presenter.Id, error); }
            buildingId = 0;
            actingPlayerId = 0;
            return false;
        }

        private static void SetVisible(FrameworkElement element, bool visible)
        {
            Visibility value = visible ? Visibility.Visible : Visibility.Collapsed;
            if (element.Visibility != value) element.Visibility = value;
        }

        private static void Report(string key, Exception error)
        {
            if (!reported.Add(key)) return;
            NativeApiLog.Error(ApiSharedRuntime.ProcessInstance.Logger, "Building panel section " + key + " failed: " + error);
        }
    }
}
