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
        private static readonly object pendingGate = new object();
        private static readonly HashSet<string> pendingOwners = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<IngameMenuRegistry.Entry> pendingEntries = new HashSet<IngameMenuRegistry.Entry>();
        private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("APIShared.Menu");
        private static MainViewModel observedMain;
        private static bool installed, scheduled, refreshing, initializing, openingPrepared, hostFailureReported;

        /// <summary>Legacy registration: sorted by order/ordinal owner/ID. First visible legacy action supplies
        /// QuitMission/Left unless an explicit visible claim wins; the remainder go to the additional page.
        /// Binary/source signature retained. Live changes now require Invalidate; no recurring provider polling.</summary>
        public static bool TryRegister(string ownerGuid, string id, Func<IngameMenuActionState> state,
            Action execute, out string reason, int order = 0)
            => Register(ownerGuid, id, state, execute, null, true, out _, out reason, order);

        /// <summary>Registers with explicit placement or additional-page-only options. Conflicts, including hidden
        /// registered claims, reject atomically unless replaceExisting was requested. Right also requires replaceVanilla.
        /// Failure returns no handle and changes no claims. The accepted handle lasts until process exit.</summary>
        public static bool TryRegister(string ownerGuid, string id, Func<IngameMenuActionState> state,
            Action execute, IngameMenuActionOptions options, out IngameMenuActionRegistration registration,
            out string reason, int order = 0)
            => Register(ownerGuid, id, state, execute, options, false, out registration, out reason, order);

        private static bool Register(string owner, string id, Func<IngameMenuActionState> state, Action execute,
            IngameMenuActionOptions options, bool legacy, out IngameMenuActionRegistration registration, out string reason, int order)
        {
            registration = null;
            try
            {
                RequireUnity();
                if (!registry.CanRegister(owner, id, state, execute, options, legacy, out reason)) return false;
                if (!installed)
                {
                    hook.Install(typeof(HUD_IngameMenu), nameof(HUD_IngameMenu.Init), typeof(void), Type.EmptyTypes, OnInit, "ingame-menu");
                    installed = true;
                }
                var entry = registry.Register(owner, id, state, execute, options, legacy, order);
                registration = new IngameMenuActionRegistration(() => RequestInvalidation(null, entry));
                reason = null;
            }
            catch (Exception error) { reason = error.Message; return false; }
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
            return true;
        }

        /// <summary>Thread-safe owner-wide invalidation, also for legacy registrations. Requests coalesce onto
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
                if (scheduled || (UnityMainThreadDispatch.IsMainThread && registry.Evaluating)) return;
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
            try { hook.Original(menu); }
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
                FlushPending(); CaptureVanilla();
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
            try { log.LogError("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] Ingame menu action failed: " +
                (entry == null ? "layout" : entry.Owner + "/" + entry.Id) + ": " + error); } catch { }
        }
    }
}
