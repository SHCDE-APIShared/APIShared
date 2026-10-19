using System;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using APIShared.Events;

namespace APIShared.Presentation
{
    /// <summary>
    /// Single owner of Vanilla's troop-action HUD setup (<c>HUD_Troops.SetuptroopActionsUI(bool)</c>) for
    /// independently installed mods that add buttons to the troop panel. Mods place a host element named
    /// <see cref="HostNamePrefix"/>... in the troop panel XAML whose DataContext exposes the documented
    /// layout properties; this service refreshes every registered mod and then lays out all hosts once.
    /// </summary>
    /// <remarks>
    /// Host contract (unchanged from the established cross-mod convention): the element name starts with
    /// <see cref="HostNamePrefix"/>; its DataContext has a public readable <c>bool WantsVisibility</c> and a
    /// public writable <c>bool LayoutAvailable</c>. Optional metadata: <c>int TroopActionLayoutVersion</c>
    /// (must equal <see cref="CurrentLayoutVersion"/>), <c>string TroopActionId</c> and
    /// <c>int TroopActionPriority</c> (non-negative, lower first). Without metadata the legacy name
    /// <c>SerpTroopAction_&lt;priority&gt;_&lt;id&gt;</c> is parsed. At most two free slots (bottom right,
    /// bottom middle) are assigned; duplicate IDs stay hidden. Everything runs on the Unity thread.
    /// </remarks>
    public static class TroopActionHud
    {
        /// <summary>Required element-name prefix of a shared troop-action host.</summary>
        public const string HostNamePrefix = TroopActionHudLayoutPolicy.HostNamePrefix;
        /// <summary>The only accepted <c>TroopActionLayoutVersion</c> metadata value.</summary>
        public const int CurrentLayoutVersion = TroopActionHudLayoutPolicy.CurrentLayoutVersion;

        private delegate void SetupTroopActionsDelegate(HUD_Troops self, bool fromInitialOpening);

        private static readonly object sync = new object();
        private static Registration[] registrations = Array.Empty<Registration>();
        private static Hook setupHook;
        private static SetupTroopActionsDelegate trampoline;
        private static ManualLogSource logger;
        private static long failures;
        [ThreadStatic] private static bool refreshing;

        internal static void Initialize(ManualLogSource log) => Volatile.Write(ref logger, log);

        /// <summary>
        /// Registers a process-lifetime visibility refresher. Call on the Unity thread after the troop
        /// panel XAML has been patched. The first registration installs the process-lifetime hook.
        /// </summary>
        /// <remarks>
        /// The refresher runs synchronously on the Unity thread after every Vanilla troop-action setup,
        /// ordered by ordinal owner then ID, followed by one shared layout pass. It must only update its
        /// own host view model (for example <c>WantsVisibility</c>); it must not issue commands. When a troop
        /// panel already exists (direct editor launch builds the HUD before mods load) the new refresher and
        /// a layout pass run immediately. Exceptions are isolated, counted and logged; there is no replay,
        /// unregistration or hook teardown, so features control their buttons through logical state.
        /// </remarks>
        /// <param name="ownerGuid">The registering mod's stable GUID.</param>
        /// <param name="registrationId">An owner-local stable ID.</param>
        /// <param name="refreshVisibility">Receives the live troop panel; never null when invoked.</param>
        /// <param name="reason">Empty on success; otherwise why nothing was registered.</param>
        /// <returns>True when the refresher was registered.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Action<HUD_Troops> refreshVisibility, out string reason)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || refreshVisibility == null)
            { reason = "Owner, ID and refresher are required."; return false; }
            lock (sync)
            {
                foreach (Registration entry in registrations)
                    if (entry.Owner == ownerGuid && entry.Id == registrationId)
                    { reason = "This owner already registered that ID."; return false; }
                if (setupHook == null && !TryInstall(out reason))
                    return false;
                var next = new Registration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[next.Length - 1] = new Registration(ownerGuid, registrationId, refreshVisibility);
                Array.Sort(next, (a, b) =>
                {
                    int comparison = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
                    return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Id, b.Id);
                });
                Volatile.Write(ref registrations, next);
            }
            reason = string.Empty;
            // A direct editor launch can build the HUD before BepInEx installs this hook.
            HUD_Troops panel = TryGetCurrentPanel();
            if (panel != null)
            {
                Invoke(new Registration(ownerGuid, registrationId, refreshVisibility), panel);
                Reflow(panel);
            }
            return true;
        }

        /// <summary>Runs every registered refresher and one layout pass for the current troop panel, if any.</summary>
        /// <remarks>Unity thread. For example after a map starts, when feature state changed outside a Vanilla setup.</remarks>
        public static void RefreshCurrentPanel()
        {
            HUD_Troops panel = TryGetCurrentPanel();
            if (panel != null)
                RefreshAll(panel);
        }

        /// <summary>Lays out every shared host in the panel once.</summary>
        /// <remarks>Unity thread. Call after changing a host's <c>WantsVisibility</c> outside a Vanilla setup;
        /// a null panel is ignored.</remarks>
        public static void Reflow(HUD_Troops troopPanel) =>
            TroopActionHudLayout.Reflow(troopPanel, Volatile.Read(ref logger));

        /// <summary>Total isolated refresher exceptions.</summary>
        public static long CallbackFailures => Interlocked.Read(ref failures);

        private static bool TryInstall(out string reason)
        {
            Hook candidate = null;
            try
            {
                MethodInfo method = typeof(HUD_Troops).GetMethod("SetuptroopActionsUI",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
                if (method == null || method.ReturnType != typeof(void))
                    throw new MissingMethodException(typeof(HUD_Troops).FullName, "SetuptroopActionsUI");
                candidate = new Hook(method, (SetupTroopActionsDelegate)SetupTroopActions,
                    new HookConfig { ID = "APIShared.Presentation.TroopActionHud" });
                trampoline = candidate.GenerateTrampoline<SetupTroopActionsDelegate>();
                setupHook = candidate;
                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                // Roll back an unpublished candidate only; a published hook is never removed.
                candidate?.Dispose();
                trampoline = null;
                reason = "Troop action HUD unavailable: " + ex.Message;
                return false;
            }
        }

        private static void SetupTroopActions(HUD_Troops self, bool fromInitialOpening)
        {
            trampoline(self, fromInitialOpening);
            if (self != null)
                RefreshAll(self);
        }

        private static void RefreshAll(HUD_Troops panel)
        {
            // A refresher that reenters through a nested Vanilla setup must not run the list twice.
            if (refreshing)
                return;
            refreshing = true;
            try
            {
                foreach (Registration registration in Volatile.Read(ref registrations))
                    Invoke(registration, panel);
                Reflow(panel);
            }
            finally { refreshing = false; }
        }

        private static void Invoke(Registration registration, HUD_Troops panel)
        {
            try { registration.Refresh(panel); }
            catch (Exception ex)
            {
                long count = Interlocked.Increment(ref failures);
                if ((count & (count - 1)) == 0)
                    InterceptionDiagnostics.Report(registration.Owner + "/" + registration.Id + " troop action refresh", ex);
            }
        }

        private static HUD_Troops TryGetCurrentPanel()
        {
            try
            {
                return MainViewModel.viewModelLoaded ? MainViewModel.Instance?.HUDTroopPanel : null;
            }
            catch { return null; }
        }

        private sealed class Registration
        {
            internal readonly string Owner, Id;
            internal readonly Action<HUD_Troops> Refresh;
            internal Registration(string owner, string id, Action<HUD_Troops> refresh)
            { Owner = owner; Id = id; Refresh = refresh; }
        }
    }
}
