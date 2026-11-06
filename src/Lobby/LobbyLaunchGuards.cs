using System;
using System.Reflection;
using APIShared.Events;
using APIShared.Internal;
using APIShared.Localization;
using BepInEx.Logging;
using CrusaderDE;

namespace APIShared.Lobby
{
    /// <summary>One lobby Ready/Start attempt or prepared multiplayer-save admission checkpoint.</summary>
    public sealed class LobbyLaunchGuardContext
    {
        internal LobbyLaunchGuardContext(FRONT_Multiplayer lobby, string command, LobbyLaunchOperation operation,
            bool requiresPeerAgreement = true)
        {
            Lobby = lobby; Command = command; Operation = operation;
            RequiresPeerAgreement = requiresPeerAgreement;
        }
        /// <summary>Exact live lobby receiver during a button callback; null for prepared HostLoadGame admission.
        /// Use a non-null receiver only during the synchronous callback.</summary>
        public FRONT_Multiplayer Lobby { get; }
        /// <summary>Vanilla command: Ready, ReadyLock, Play, COOP_START or prepared HostLoadGame.</summary>
        public string Command { get; }
        /// <summary>Ready or Start; other lobby actions are never dispatched to guards.</summary>
        public LobbyLaunchOperation Operation { get; }
        /// <summary>False for Play with skirmishGame, COOP_START with this receiver's singlePlayerCoop,
        /// or prepared HostLoadGame with exactly one human in its freshly validated roster.
        /// Ready/ReadyLock, prepared saves with multiple humans, and missing/unknown button-receiver
        /// evidence return true. Captured synchronously before guards; in-game MP flags are not used.</summary>
        public bool RequiresPeerAgreement { get; }
    }

    /// <summary>Shared pre-launch admission for gameplay capabilities whose peers must agree before Ready/Start.</summary>
    /// <remarks>Register on Unity during initialization. Registrations and the managed hook live until process exit;
    /// change consumer demand logically. Guards run synchronously on the lobby/UI thread outside registry locks,
    /// ordered by priority and ordinal owner/ID, without replay. Return null/empty to allow or a localized player-facing
    /// reason to block. The first rejection wins. A throwing guard fails closed and is logged; original lobby actions
    /// and the existing hook chain run exactly once only when every guard allows. This broker transports no readiness
    /// data: use existing authenticated settings/readiness channels. Prepared multiplayer saves run the same
    /// guards after settings preparation at host release, client acknowledgement and final native-start admission;
    /// their receiver is null and rejections use the existing save-preparation failure path. Consumers follow the
    /// context's RequiresPeerAgreement for peer admission and retain their own feature demand and mode policy;
    /// running-game multiplayer flags do not classify fresh lobby launches. No polling or native game hook is installed.</remarks>
    public static class LobbyLaunchGuards
    {
        private delegate void Click(FRONT_Multiplayer self, string command);
        private static readonly ManagedInterceptionHook<Click> Hook = new ManagedInterceptionHook<Click>();
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("APIShared.LobbyLaunchGuards");
        private static readonly LobbyLaunchGuardRegistry Registry = new LobbyLaunchGuardRegistry(Install,
            () => UiText.Get("LobbyLaunch.GuardFailure"));
        private static bool installed;
        private static FieldInfo readyField, lockedField, closingField;
        /// <summary>Whether the shared permanent ButtonClicked interceptor has been installed.</summary>
        public static bool IsAvailable => installed;

        /// <summary>Registers an admission callback, initially participating in subsequent Ready/Start attempts.
        /// Invalid/duplicate identities, wrong-thread calls or a missing managed method return false with a reason.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Func<LobbyLaunchGuardContext, string> guard, out string diagnostic, int order = 0)
        {
            if (!UnityMainThreadDispatch.IsMainThread)
            { diagnostic = "Lobby launch guards must register on the initialized Unity thread."; return false; }
            return Registry.TryRegister(ownerGuid, registrationId, guard, out diagnostic, order);
        }

        private static void Install()
        {
            if (installed) return;
            readyField = RequireBooleanField("MPLocalReady");
            lockedField = RequireBooleanField("MPLocalReadyLocked");
            closingField = RequireBooleanField("closePanelDisplayed");
            // These fields select the exact local branches in Vanilla's Play/COOP_START.
            // Validate their actual runtime visibility and staticness before publishing a hook
            // whose dispatch contains the direct references.
            RequireBooleanField("skirmishGame", true, true);
            RequireBooleanField("singlePlayerCoop", false, true);
            Hook.Install(typeof(FRONT_Multiplayer), nameof(FRONT_Multiplayer.ButtonClicked), typeof(void),
                new[] { typeof(string) }, Dispatch, "LobbyLaunchGuards");
            installed = true;
        }

        private static FieldInfo RequireBooleanField(string name, bool isStatic = false, bool isPublic = false)
        {
            FieldInfo field = typeof(FRONT_Multiplayer).GetField(name,
                (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(bool) || field.IsStatic != isStatic ||
                (isPublic && !field.IsPublic))
                throw new MissingFieldException(typeof(FRONT_Multiplayer).FullName, name + ": Boolean");
            return field;
        }

        private static void Dispatch(FRONT_Multiplayer self, string command)
        {
            if (!LobbyLaunchGuardRegistry.TryGetOperation(command, out LobbyLaunchOperation operation))
            { Hook.Original(self, command); return; }
            // Ready/ReadyLock are toggles: never trap a player in an already-ready/locked state.
            // These private fields are resolved and type-checked against the real loaded assembly at install.
            if (!LobbyLaunchGuardRegistry.ShouldCheck(command, (bool)readyField.GetValue(self),
                (bool)lockedField.GetValue(self), (bool)closingField.GetValue(self)))
            { Hook.Original(self, command); return; }
            bool agreement = LobbyLaunchGuardRegistry.RequiresPeerAgreement(command,
                !ReferenceEquals(self, null), FRONT_Multiplayer.skirmishGame,
                !ReferenceEquals(self, null) && self.singlePlayerCoop);
            var decision = Registry.Evaluate(new LobbyLaunchGuardContext(self, command, operation, agreement));
            if (decision.Blocked)
            { Report(decision.Owner, operation, decision.Reason, decision.Detail); return; }
            Hook.Original(self, command);
        }

        // Called only by the existing save-readiness coordinator, after its fresh roster,
        // settings and fingerprint checks. An empty registry permits ordinary save loading;
        // no second interceptor, transport or report is needed for this synchronous decision.
        internal static LobbyLaunchGuardDecision EvaluatePreparedMultiplayerLoad(int humanCount) =>
            Registry.Evaluate(LobbyLaunchGuardRegistry.PreparedMultiplayerLoadContext(humanCount));

        private static void Report(string owner, LobbyLaunchOperation operation, string reason, string detail)
        {
            // A chat/log reporting failure must never reopen a rejected gameplay start.
            try { LobbyLaunchStatus.ReportAttempt(owner, operation, new LobbyLaunchStatusItem(reason,
                LobbyLaunchStatusKind.Blocking, detail, LobbyLaunchStatusScope.Lobby)); }
            catch (Exception error) { DebugLogHelper.LogError(Log, "Lobby launch rejected: " + reason + "; " + detail + "; reporting: " + error); }
        }
    }

    internal readonly struct LobbyLaunchGuardDecision
    {
        internal readonly string Owner, Reason, Detail;
        internal bool Blocked => !string.IsNullOrEmpty(Reason);
        internal LobbyLaunchGuardDecision(string owner, string reason, string detail)
        { Owner = owner; Reason = reason; Detail = detail; }
    }

    internal sealed class LobbyLaunchGuardRegistry
    {
        // Shared by button and prepared-save evaluations, including cross-registry test
        // fixtures. The flag is clear before either caller performs its admitted action.
        [ThreadStatic] private static bool dispatching;
        private readonly object sync = new object();
        private readonly OwnedCallbacks<Func<LobbyLaunchGuardContext, string>> callbacks = new OwnedCallbacks<Func<LobbyLaunchGuardContext, string>>();
        private readonly Action install;
        private readonly Func<string> failureText;
        internal LobbyLaunchGuardRegistry(Action install, Func<string> failureText)
        { this.install = install; this.failureText = failureText; }
        internal bool TryRegister(string owner, string id, Func<LobbyLaunchGuardContext, string> guard,
            out string diagnostic, int order = 0)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || guard == null)
            { diagnostic = "Owner GUID, registration ID and guard are required."; return false; }
            lock (sync)
            {
                foreach (var item in callbacks.Snapshot)
                    if (item.Owner == owner && item.Id == id)
                    { diagnostic = "Duplicate lobby launch guard identity."; return false; }
                try { install(); }
                catch (Exception error) { diagnostic = error.Message; return false; }
                return callbacks.TryAdd(owner, id, guard, out diagnostic, order);
            }
        }
        internal LobbyLaunchGuardDecision Evaluate(LobbyLaunchGuardContext context)
        {
            if (dispatching)
                return new LobbyLaunchGuardDecision("APIShared_Serp", failureText(),
                    "Recursive Ready/Start from a launch guard was refused.");
            dispatching = true;
            try
            {
                foreach (var item in callbacks.Snapshot)
                {
                    string reason;
                    try { reason = item.Callback(context); }
                    catch (Exception error) { return new LobbyLaunchGuardDecision(item.Owner, failureText(), item.Id + ": " + error); }
                    if (!string.IsNullOrWhiteSpace(reason))
                        return new LobbyLaunchGuardDecision(item.Owner, reason, "guard=" + item.Id + "; command=" + context.Command);
                }
                return default;
            }
            finally { dispatching = false; }
        }
        internal static LobbyLaunchGuardContext PreparedMultiplayerLoadContext(int humanCount)
        {
            if (humanCount < 1) throw new ArgumentOutOfRangeException(nameof(humanCount));
            return new LobbyLaunchGuardContext(null, "HostLoadGame", LobbyLaunchOperation.Start, humanCount > 1);
        }
        internal static bool TryGetOperation(string command, out LobbyLaunchOperation operation)
        {
            operation = LobbyLaunchOperation.Ready;
            if (command == "Ready" || command == "ReadyLock") return true;
            operation = LobbyLaunchOperation.Start;
            return command == "Play" || command == "COOP_START";
        }
        internal static bool ShouldCheck(string command, bool ready, bool locked, bool closing) =>
            !closing && !(command == "Ready" && ready) && !(command == "ReadyLock" && locked);
        internal static bool RequiresPeerAgreement(string command, bool hasReceiver, bool skirmish, bool singlePlayerCoop) =>
            !hasReceiver || !(command == "Play" && skirmish || command == "COOP_START" && singlePlayerCoop);
    }
}
