using System;
using System.Collections.Generic;
using System.ComponentModel;
using APIShared.Events;
using APIShared.Internal;
using CrusaderDE;

namespace APIShared.Presentation
{
    /// <summary>Single process-lifetime owner of ESC menu positions and the additional-actions page.</summary>
    /// <remarks>Register and query on Unity. Providers/commands run there at opening, explicit invalidation and
    /// command validation; never per tick/frame. Hide permanent registrations through null/invisible state.
    /// Most recent explicit takeover wins; hiding it reveals preceding visible claims. Vanilla retains its commands,
    /// confirmation and pause semantics. Consumers retain gameplay/MP authorization. Provider failures are isolated.</remarks>
    public static partial class IngameMenuActions
    {
        private delegate void Init(HUD_IngameMenu menu);
        private static readonly ManagedInterceptionHook<Init> hook = new ManagedInterceptionHook<Init>();
        private static readonly IngameMenuRegistry registry = new IngameMenuRegistry();
        private static readonly VanillaMenuOverrides vanillaOverrides = new VanillaMenuOverrides();
        private static readonly Action<VanillaMenuOverrides.Entry, Exception> overrideFailure = ReportOverride;
        private static readonly object pendingGate = new object();
        private static readonly HashSet<string> pendingOwners = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<IngameMenuRegistry.Entry> pendingEntries = new HashSet<IngameMenuRegistry.Entry>();
        private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("APIShared.Menu");
        private static MainViewModel observedMain;
        private static bool installed, scheduled, refreshing, initializing, openingPrepared, hostFailureReported;

        /// <summary>Registers with explicit placement or additional-page-only options. Conflicts, including hidden
        /// registered claims, reject atomically unless replaceExisting was requested. Right also requires replaceVanilla.
        /// Failure returns no handle and changes no claims. The accepted handle lasts until process exit.</summary>
        public static bool TryRegister(string ownerGuid, string id, Func<IngameMenuActionState> state,
            Action execute, IngameMenuActionOptions options, out IngameMenuActionRegistration registration,
            out string reason, int order = 0)
        {
            return TryRegisterCore(ownerGuid, id, state, execute, null, options, out registration, out reason, order);
        }

        /// <summary>Overrides the enabled state and execution of one existing Vanilla ESC action without adding
        /// a position claim, replacement button or additional-page entry. Its text, visibility, command and element
        /// identity remain unchanged. One process-lifetime owner may register each Vanilla row; conflicts reject.</summary>
        /// <remarks>Register on initialized Unity after the mission-lifecycle capability is available; its shared
        /// command dispatcher is required, and unavailable registration rejects before changing the menu.
        /// The provider runs there on opening, invalidation and freshly at
        /// command execution: null preserves Vanilla, false disables and consumes its command, true enables and
        /// executes the supplied action. Notify relevant changes through the returned handle, never polling.
        /// Consumers retain mission, multiplayer authority, confirmation and pause handling. Failed providers disable
        /// and consume the action; callback failures are isolated and logged once. No retry, replay or unregistration.
        /// Existing placement registrations remain independent, including an original button moved to the extra page.</remarks>
        public static bool TryOverrideVanillaAction(string ownerGuid, string id, IngameMenuRow row,
            Func<bool?> enabled, Action execute, out IngameMenuActionRegistration registration, out string reason)
        {
            registration = null;
            try
            {
                RequireUnity();
                if (registry.Evaluating) { reason = "Register overrides outside a state-provider callback."; return false; }
                if (!vanillaOverrides.CanRegister(ownerGuid, id, row, enabled, execute, out reason)) return false;
                foreach (var entry in registry.Entries)
                    if (entry.Owner == ownerGuid && entry.Id == id)
                    { reason = "Duplicate menu action: " + ownerGuid + "/" + id; return false; }
                if (!ApiShared.ForMod(ownerGuid).TryGetMissionLifecycle(out _, out var dispatcher) ||
                    dispatcher?.State != NativeCapabilityState.Available)
                { reason = "Vanilla menu command dispatcher unavailable: " + (dispatcher?.Reason ?? "mission lifecycle is not ready"); return false; }
                EnsureInstalled();
                vanillaOverrides.Register(ownerGuid, id, row, enabled, execute);
                registration = new IngameMenuActionRegistration(() => RequestInvalidation(ownerGuid, null));
                reason = null;
            }
            catch (Exception error) { reason = error.Message; return false; }
            PresentAcceptedRegistration();
            return true;
        }

        /// <summary>Registers an inline custom control with the same placement, conflict and lifetime rules as an action.</summary>
        /// <remarks>Register on Unity during startup. The factory runs on Unity at first visible presentation once per
        /// menu view; return a fresh, unattached Control. It owns its children, bindings and input handling. The broker
        /// owns root width, margin, enabled state and the State tooltip (scaled from the root FontSize). State.Text is
        /// diagnostic only. A failed factory is isolated for that view. Ordinary invalidation preserves the control
        /// and its focus when placement is unchanged. Actual moves can change focus; a new view receives a new control.
        /// Commands must use the supplied context's TryExecute and retain consumer mission/authority checks.
        /// No disposal, implicit takeover, provider polling, or registration replay is added.</remarks>
        public static bool TryRegisterContent(string ownerGuid, string id, Func<IngameMenuActionState> state,
            Func<IngameMenuContentContext, Noesis.Control> createContent, IngameMenuActionOptions options,
            out IngameMenuActionRegistration registration, out string reason, int order = 0)
        {
            if (createContent == null)
            { registration = null; reason = "A content factory is required."; return false; }
            return TryRegisterCore(ownerGuid, id, state, () => { }, createContent, options, out registration, out reason, order);
        }

        private static bool TryRegisterCore(string ownerGuid, string id, Func<IngameMenuActionState> state,
            Action execute, Func<IngameMenuContentContext, Noesis.Control> createContent,
            IngameMenuActionOptions options, out IngameMenuActionRegistration registration, out string reason, int order)
        {
            registration = null;
            try
            {
                RequireUnity();
                if (vanillaOverrides.Evaluating) { reason = "Register actions outside an enabled-provider callback."; return false; }
                if (vanillaOverrides.HasIdentity(ownerGuid, id)) { reason = "Duplicate menu action: " + ownerGuid + "/" + id; return false; }
                if (!registry.CanRegister(ownerGuid, id, state, execute, options, out reason)) return false;
                EnsureInstalled();
                var entry = registry.Register(ownerGuid, id, state, execute, options, order);
                entry.CreateContent = createContent;
                registration = new IngameMenuActionRegistration(() => RequestInvalidation(null, entry));
                reason = null;
            }
            catch (Exception error) { reason = error.Message; return false; }
            PresentAcceptedRegistration();
            return true;
        }
        private static void EnsureInstalled()
        {
            if (installed) return;
            hook.Install(typeof(HUD_IngameMenu), nameof(HUD_IngameMenu.Init), typeof(void), Type.EmptyTypes, OnInit, "ingame-menu");
            installed = true;
        }
        private static void PresentAcceptedRegistration()
        {
            // Presentation failure does not retroactively reject an already accepted permanent registration.
            try
            {
                if (MainViewModel.viewModelLoaded)
                {
                    Observe(MainViewModel.Instance);
                    if (observedMain.Show_HUD_IngameMenu) { ResolveView(observedMain.HUDIngameMenu); Refresh(false); }
                }
            }
            catch (Exception error) { Report(null, error); }
        }

        /// <summary>Thread-safe owner-wide invalidation. Requests coalesce onto
        /// the validated Unity dispatcher; closed menus defer evaluation. Notify relevant changes, never periodic ticks/frames.
        /// Invalidation inside a provider marks its owner dirty for the next external event, without recursive evaluation.</summary>
        public static void Invalidate(string ownerGuid)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid)) throw new ArgumentException("An owner GUID is required.", nameof(ownerGuid));
            RequestInvalidation(ownerGuid, null);
        }
        private static void RequestInvalidation(string owner, IngameMenuRegistry.Entry entry)
        {
            lock (pendingGate)
            {
                if (entry == null) pendingOwners.Add(owner); else pendingEntries.Add(entry);
                if (scheduled || (UnityMainThreadDispatch.IsMainThread && (registry.Evaluating || vanillaOverrides.Evaluating))) return;
                scheduled = true;
            }
            if (!UnityMainThreadDispatch.TryEnqueue(FlushInvalidations))
            {
                lock (pendingGate) scheduled = false;
                Report(null, new InvalidOperationException("Menu invalidation requires the initialized Unity dispatcher."));
            }
        }

        /// <summary>Unity-thread cached occupancy and all registered claims. Does not invoke providers, query native
        /// state or reserve anything. Before opening HasState can be false; closed snapshots may be stale.</summary>
        public static IngameMenuRowState GetRowState(IngameMenuRow row)
        { RequireUnity(); return registry.GetRow(row); }

        private static void RequireUnity()
        {
            if (!UnityMainThreadDispatch.IsMainThread)
                throw new InvalidOperationException("Register/query ESC menu actions on the initialized Unity thread.");
        }
        private static void FlushPending()
        {
            lock (pendingGate)
            {
                foreach (string owner in pendingOwners) registry.Invalidate(owner);
                foreach (var entry in pendingEntries) entry.Dirty = true;
                pendingEntries.Clear();
                pendingOwners.Clear(); scheduled = false;
            }
        }
        private static void FlushInvalidations()
        {
            FlushPending();
            if (!refreshing && MenuVisible()) Refresh(false);
        }
        private static bool MenuVisible() => MainViewModel.viewModelLoaded && observedMain != null &&
            ReferenceEquals(observedMain, MainViewModel.Instance) && observedMain.Show_HUD_IngameMenu;
        private static void OnInit(HUD_IngameMenu menu)
        {
            initializing = true;
            try
            {
                // Not every Vanilla mode rewrites every enabled value (editor Load, for example).
                // Remove only our owned values first, then let Vanilla set the new mode's baseline.
                try { vanillaOverrides.RestoreApplied(overrideFailure); }
                finally { hook.Original(menu); } // Restoration must never suppress the original Init.
            }
            finally { initializing = false; }
            try
            {
                Observe(MainViewModel.Instance);
                ResolveView(menu);
                vanillaMenuHeight = layoutRoot.Height; // Original Init has just restored its mode-specific geometry.
                menuTooltips?.CloseAll();
                ShowAdditional(false);
                openingPrepared = true;
                Refresh(true);
            }
            catch (Exception error) { Report(null, error); }
        }
        private static void Observe(MainViewModel main)
        {
            if (ReferenceEquals(main, observedMain)) return;
            if (observedMain != null) observedMain.PropertyChanged -= OnMainChanged;
            observedMain = main;
            if (main != null) main.PropertyChanged += OnMainChanged;
        }
        private static void OnMainChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(MainViewModel.Show_HUD_IngameMenu)) return;
            if (initializing) return;
            if (!ReferenceEquals(sender, observedMain) || !UnityMainThreadDispatch.IsMainThread) return;
            try
            {
                if (!observedMain.Show_HUD_IngameMenu)
                { openingPrepared = false; ShowAdditional(false); return; }
                ResolveView(observedMain.HUDIngameMenu);
                if (!openingPrepared) Refresh(true); // Confirmation cancellation can show without Init.
                openingPrepared = false;
            }
            catch (Exception error) { Report(null, error); }
        }
        private static bool Refresh(bool all)
        {
            if (initializing || refreshing || current == null) return false;
            refreshing = true;
            try
            {
                FlushPending(); vanillaOverrides.Apply(vanillaButtons, overrideFailure); CaptureVanilla();
                registry.Evaluate(all, Report);
                Render(registry.Resolve());
                return true;
            }
            catch (Exception error) { Report(null, error); return false; }
            finally { refreshing = false; }
        }
        private static void Execute(IngameMenuRegistry.Entry entry, HUD_IngameMenu view, bool fromAdditional)
        {
            try
            {
                if (!MenuVisible() || !ReferenceEquals(view, current) || !ReferenceEquals(view, observedMain.HUDIngameMenu) ||
                    fromAdditional != additionalVisible) return;
                if (!Refresh(true)) return; // A failed presentation must not authorize a stale click.
                if (registry.CanExecute(entry, fromAdditional) && MenuVisible() &&
                    ReferenceEquals(view, current) && ReferenceEquals(view, observedMain.HUDIngameMenu) &&
                    fromAdditional == additionalVisible) entry.Execute();
            }
            catch (Exception error) { Report(entry, error); }
        }
        private static void Report(IngameMenuRegistry.Entry entry, Exception error)
        {
            if (entry == null ? hostFailureReported : entry.FailureReported) return;
            if (entry == null) hostFailureReported = true; else entry.FailureReported = true;
            NativeApiLog.Error(log, "Ingame menu action failed: " + (entry == null ? "layout" : entry.Owner + "/" + entry.Id) + ": " + error);
        }
        internal static bool TryExecuteVanillaOverride(HUD_IngameMenu view, int function)
        {
            var entry = vanillaOverrides.Get(function);
            if (entry == null) return false;
            return ExecuteVanillaOverride(entry, view);
        }
        // Keep the optional click-validation closure out of the ordinary no-override path.
        private static bool ExecuteVanillaOverride(VanillaMenuOverrides.Entry entry, HUD_IngameMenu view)
        {
            try
            {
                RequireUnity();
                return vanillaOverrides.TryExecute(entry, overrideFailure, () =>
                    !refreshing && !initializing && MenuVisible() && ReferenceEquals(view, current) &&
                    ReferenceEquals(view, observedMain.HUDIngameMenu) &&
                    vanillaButtons[(int)entry.Row]?.IsVisible == true && vanillaButtons[(int)entry.Row].IsEnabled);
            }
            catch (Exception error) { VanillaMenuOverrides.ReportFailure(entry, error, overrideFailure); return true; }
        }
        private static void ReportOverride(VanillaMenuOverrides.Entry entry, Exception error) =>
            NativeApiLog.Error(log, "Ingame menu Vanilla override failed: " + entry.Owner + "/" + entry.Id + ": " + error);
    }
}
