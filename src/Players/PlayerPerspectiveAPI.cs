using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using SHCDESE.API;
using SHCDESE.GameGlobals;
using SHCDESE.Interop.Enums;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.PlayerControl;

namespace APIShared
{
    /// <summary>Separates the locally controlled player from Vanilla's native report viewpoint.</summary>
    public static class PlayerPerspectiveAPI
    {
        private delegate int LocalPlayerIdCall(GamePlayerManagerAPI self);

        private static readonly PlayerPerspectiveChangeHub changes = new PlayerPerspectiveChangeHub();

        /// <summary>
        /// Observes committed API perspective transactions, in registration and process sequence order.
        /// No replay or implicit UI dispatch: read GetViewedPlayerId for the initial snapshot. Callbacks
        /// run outside the transition lock on the thread draining the FIFO (possibly another caller
        /// during concurrent transactions). Reentrant changes are queued. Exceptions are isolated.
        /// Owner GUID and owner-local ID must be nonempty and unique while registered. Dispose stops
        /// future captures; already queued immutable deliveries can still arrive. Native view writes
        /// made outside this API are not observed. Clear is signalled even if the native view is equal.
        /// </summary>
        public static IDisposable SubscribeChanges(string ownerGuid, string registrationId,
            Action<PlayerPerspectiveChange> callback) => changes.Subscribe(ownerGuid, registrationId, callback);

        private static Hook localPlayerIdHook;
        private static LocalPlayerIdCall originalLocalPlayerId;
        private static readonly object viewChangeGate = new object();
        private static ManualLogSource transitionLog;
        private static int identityOverrideActive;
        private static int identityOverridePlayerId = -1;
        private static bool lifecycleBound;
        private static object perspectiveEngineGate;
        private static readonly PlayerViewIdentityOwner viewOwner = new PlayerViewIdentityOwner("spectator-view", OnViewIdentityFailure);
        private static ControlSaveIdentity.Lease viewLease;
        private static int selectedSpectatorPlayerId;
        private static int transitionViewPlayerId;
        private static int transitionActive;
        private static int viewUncertain;

        internal static void Initialize(ManualLogSource log)
        {
            if (localPlayerIdHook != null) return;
            Hook candidate = null;
            try
            {
                perspectiveEngineGate = typeof(EngineInterface).GetField("threadLock",
                    BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                MethodInfo method = typeof(GamePlayerManagerAPI).GetMethod(
                    nameof(GamePlayerManagerAPI.GetLocalPlayerId),
                    BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (method == null || method.ReturnType != typeof(int))
                    throw new MissingMethodException("Installed GamePlayerManagerAPI.GetLocalPlayerId signature changed.");

                candidate = new Hook(method, (LocalPlayerIdCall)OnGetLocalPlayerId,
                    new HookConfig { ManualApply = true, ID = "APIShared.PlayerPerspective.LocalIdentity" });
                LocalPlayerIdCall original = candidate.GenerateTrampoline<LocalPlayerIdCall>();
                originalLocalPlayerId = original;
                candidate.Apply();
                localPlayerIdHook = candidate;
                transitionLog = log;
                changes.OnError = error => log.LogError("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] PLAYER_PERSPECTIVE_OBSERVER_FAILED: " + error);
            }
            catch (Exception error)
            {
                // A failed, unpublished candidate is the only hook we ever undo.
                try { candidate?.Undo(); candidate?.Dispose(); } catch { }
                log.LogError("PLAYER_PERSPECTIVE_HOOK_FAILED: " + error);
            }
        }

        private static int OnGetLocalPlayerId(GamePlayerManagerAPI self)
        {
            // The hot hook never touches Unity state. Session teardown clears this flag,
            // and inactive calls retain the original Script Extender behavior.
            int protectedPlayer = PlayerViewIdentityRuntime.Instance.ProtectedPlayerId;
            if (protectedPlayer > 0) return protectedPlayer;
            if (Volatile.Read(ref identityOverrideActive) != 0) return Volatile.Read(ref identityOverridePlayerId);
            return originalLocalPlayerId(self);
        }

        internal static bool ShouldOverrideLocalPlayerId(
            int selectedPlayerId, int gameType, int spectatorMode, int controlledPlayerId) =>
            selectedPlayerId >= 1 && selectedPlayerId <= 8 &&
            gameType == (int)eGameTypeModes.GAMETYPE_MULTIPLAYER &&
            spectatorMode != 0 && controlledPlayerId <= 0;

        /// <summary>Gets the player actually controlled by this client, or -1 for a spectator.</summary>
        public static int GetControlledPlayerId()
        {
            if (GameData.Instance?.lastGameState == null || EditorDirector.instance == null) return -1;
            int playerId = EditorDirector.instance.ActivePlayerID;
            return playerId >= 1 && playerId <= 8 ? playerId : -1;
        }

        /// <summary>Gets the selected report viewpoint; outside spectator mode this is the native view.</summary>
        public static int GetViewedPlayerId()
        {
            var failure = PlayerViewIdentityRuntime.Instance.Identity?.LastFailure;
            if (failure != null && ReferenceEquals(failure.Lease, viewLease)) return -1;
            if (Volatile.Read(ref transitionActive) != 0)
                return Volatile.Read(ref transitionViewPlayerId);
            if (Volatile.Read(ref viewUncertain) != 0) return -1;
            int selected = Volatile.Read(ref selectedSpectatorPlayerId);
            if (selected != 0) return selected;
            return GetRawNativeViewPlayerId();
        }

        /// <summary>Reads the native index without applying the spectator identity correction.</summary>
        public static int GetRawNativeViewPlayerId()
        {
            try
            {
                ulong address = GameGlobalsManager.Instance.LocalPlayerIdVA;
                return address == 0 ? -1 : Marshal.ReadInt32(new IntPtr(unchecked((long)address)));
            }
            catch { return -1; }
        }

        /// <summary>
        /// Changes Vanilla's view and publishes it for an original spectator only.
        /// Returns false while another role owns the participant view or a Save/send scope is active.
        /// </summary>
        public static bool TrySetSpectatorView(int playerId)
        {
            if (localPlayerIdHook == null || playerId < 1 || playerId > 8) return false;
            EngineInterface.PlayState state = GameData.Instance?.lastGameState;
            EditorDirector director = EditorDirector.instance;
            if (state == null || director == null || !ShouldOverrideLocalPlayerId(
                    playerId, state.game_type, state.spectatorMode, director.ActivePlayerID) ||
                !state.is_human_or_skirmish_player(playerId))
                return false;

            bool changed;
            string failure;
            try
            {
                object engineGate = PlayerViewIdentityRuntime.Instance.EngineGate ?? perspectiveEngineGate;
                if (engineGate == null)
                    changed = TryChangeViewCore(playerId, GetRawNativeViewPlayerId, EngineInterface.SetEditorPlayer,
                        out failure, contextValid: IsUnboundViewIdentity);
                else lock (engineGate)
                    changed = TryChangeViewCore(playerId, GetRawNativeViewPlayerId, EngineInterface.SetEditorPlayer,
                        out failure, contextValid: IsUnboundViewIdentity);
            }
            finally { changes.Drain(); }
            if (!changed) transitionLog?.LogError("PLAYER_PERSPECTIVE_SWITCH_FAILED: " + failure);
            return changed;
        }

        private static bool IsUnboundViewIdentity()
        {
            var core = PlayerViewIdentityRuntime.Instance.Identity;
            return core == null || (core.CurrentLease == null && !core.IsScopeActive);
        }

        internal static void BindLifecycle(MissionLifecycleService lifecycle)
        {
            if (lifecycleBound || lifecycle == null) return;
            if (!lifecycle.Bind("APIShared.player-perspective").TryRegisterObserver("view-reset",
                _ => EndViewWorld(), _ => EndViewWorld(), null, out var diagnostic))
                throw new InvalidOperationException("Player-perspective lifecycle unavailable: " + diagnostic?.Reason);
            lifecycleBound = true;
        }

        private static void EndViewWorld()
        {
            PlayerViewIdentityRuntime.Instance.RetireEndedWorld();
            ClearSpectatorView();
        }

        /// <summary>
        /// Selects a report viewpoint for an eliminated participant while preserving their positive
        /// controlled/local player ID. Call on Unity's main thread in a ready multiplayer-type game
        /// (including local skirmish), with Vanilla's local spectator flag already enabled. A current
        /// observed official loss or confirmed lord death is required; no living lord alone is not proof.
        /// Foreign views require Save and Chore identity protection. The unchanged own view remains
        /// available without that optional protection. This grants no command/control permission.
        /// Confirmed changes use SubscribeChanges; failed writes roll back and uncertainty fails closed.
        /// </summary>
        public static bool TrySetEliminatedPlayerView(int playerId)
        {
            if (!Internal.UnityMainThreadDispatch.IsMainThread || localPlayerIdHook == null ||
                playerId < 1 || playerId > 8) return false;
            object engineGate = PlayerViewIdentityRuntime.Instance.EngineGate ?? perspectiveEngineGate;
            if (engineGate == null) return false;
            try
            {
                lock (engineGate)
                {
                    var context = MissionLifecycleService.ActiveContext;
                    var state = GameData.Instance?.lastGameState;
                    int actual = GetControlledPlayerId();
                    var observations = ApiSharedRuntime.ProcessInstance.PlayerDefeatState;
                    var runtime = PlayerViewIdentityRuntime.Instance;
                    var core = runtime.Identity;
                    if (context == null || state == null || actual < 1 || actual > 8 ||
                        state.game_type != (int)eGameTypeModes.GAMETYPE_MULTIPLAYER || state.spectatorMode == 0 ||
                        !state.is_human_or_skirmish_player(playerId) || observations == null ||
                        !observations.TryGetState(context.SessionId, actual, out var snapshot) || !snapshot.IsEliminated) return false;
                    if (viewLease != null && (core == null || !core.IsOwned(viewOwner, viewLease))) viewLease = null;
                    if (core?.CurrentLease != null && !core.IsOwned(viewOwner, viewLease)) return false;
                    if (playerId == actual && viewLease == null &&
                        GetRawNativeViewPlayerId() == actual)
                    {
                        long ownWorld = MissionLifecycleService.NativeWorld?.Sequence ?? 0;
                        return TryChangeViewCore(actual, GetRawNativeViewPlayerId, KeepNativeView, out _, actual,
                            () => ownWorld > 0 && MissionLifecycleService.NativeWorld?.Sequence == ownWorld &&
                                MissionLifecycleService.ActiveContext?.SessionId == context.SessionId);
                    }
                    if (!runtime.ForeignViewsReady || core == null || core.IsScopeActive) return false;
                    if (viewLease == null)
                    {
                        long world = MissionLifecycleService.NativeWorld?.Sequence ?? 0;
                        if (!core.TryBind(viewOwner, world, actual, out viewLease, out _)) return false;
                    }
                    if (!core.IsCurrent(viewOwner, viewLease) || viewLease.Human != actual || core.Failure != null) return false;
                    var selectedLease = viewLease;
                    bool changed = TryChangeViewCore(playerId, GetRawNativeViewPlayerId,
                        EngineInterface.SetEditorPlayer, out string failure, actual,
                        () => core.IsCurrent(viewOwner, selectedLease) &&
                            MissionLifecycleService.ActiveContext?.SessionId == context.SessionId);
                    if (!changed) NativeApiLog.Error(transitionLog, "ELIMINATED_PLAYER_VIEW_FAILED: " + failure);
                    return changed;
                }
            }
            catch (Exception error)
            { NativeApiLog.Error(transitionLog, "ELIMINATED_PLAYER_VIEW_FAILED: " + error); return false; }
            finally { changes.Drain(); }
        }

        private static void KeepNativeView(int playerId) { }

        /// <summary>
        /// Restores an eliminated participant's own native view before releasing its identity protection.
        /// An ended native world is retired without writing into its replacement. On restoration failure
        /// protection is retained. No gameplay permission is granted or revoked by this helper.
        /// </summary>
        public static void ClearEliminatedPlayerView()
        {
            if (viewLease != null ||
                (Volatile.Read(ref identityOverrideActive) != 0 && Volatile.Read(ref identityOverridePlayerId) > 0))
                ClearSpectatorView();
        }

        private static void OnViewIdentityFailure(ControlSaveIdentity.FailureRecord failure)
        {
            var runtime = PlayerViewIdentityRuntime.Instance;
            if (runtime.EngineGate == null) return;
            try
            {
                lock (runtime.EngineGate)
                    TryInvalidateOwnedView(runtime.Identity, viewOwner, viewLease, failure);
            }
            finally { changes.Drain(); }
        }

        // Validation and marking share the lifecycle's engine monitor. Drain after
        // leaving it, so observers cannot run under the native critical section.
        internal static bool TryInvalidateOwnedView(ControlSaveIdentity core, object owner,
            ControlSaveIdentity.Lease ownedLease, ControlSaveIdentity.FailureRecord failure)
        {
            if (failure == null || core == null || !ReferenceEquals(ownedLease, failure.Lease) ||
                !core.IsCurrent(owner, ownedLease)) return false;
            lock (viewChangeGate)
            {
                if (Interlocked.Exchange(ref viewUncertain, 1) != 0) return true;
                changes.Enqueue(Volatile.Read(ref selectedSpectatorPlayerId), -1, PlayerPerspectiveChangeReason.Uncertain);
            }
            return true;
        }

        // The injected delegates also let the transaction be tested without a running game.
        internal static bool TryChangeView(int playerId, Func<int> readNativeView,
            Action<int> setNativeView, out string failure, int protectedPlayerId = -1, Func<bool> contextValid = null)
        {
            try { return TryChangeViewCore(playerId, readNativeView, setNativeView, out failure, protectedPlayerId, contextValid); }
            finally { changes.Drain(); }
        }

        private static bool TryChangeViewCore(int playerId, Func<int> readNativeView,
            Action<int> setNativeView, out string failure, int protectedPlayerId = -1, Func<bool> contextValid = null)
        {
            failure = null;
            if (playerId < 1 || playerId > 8 || readNativeView == null || setNativeView == null)
            {
                failure = "Invalid view transition request.";
                return false;
            }

            lock (viewChangeGate)
            {
                if (!ValidateContext(contextValid, out failure)) return false;
                int previousRaw;
                try { previousRaw = readNativeView(); }
                catch (Exception error)
                {
                    failure = "Could not read the previous native view: " + error;
                    return false;
                }
                if (!ValidateContext(contextValid, out failure)) return false;
                if (previousRaw < 0 || previousRaw > 8)
                {
                    failure = "Previous native view cannot be restored: " + previousRaw + ".";
                    return false;
                }

                int previousPublished = Volatile.Read(ref selectedSpectatorPlayerId);
                if (previousPublished != previousRaw) previousPublished = 0;
                int previousOverride = Volatile.Read(ref identityOverrideActive);
                int previousIdentity = Volatile.Read(ref identityOverridePlayerId);
                int previousUncertain = Volatile.Read(ref viewUncertain);
                // Publish protection before Vanilla can write its native viewpoint.
                Volatile.Write(ref identityOverridePlayerId, protectedPlayerId);
                Volatile.Write(ref identityOverrideActive, 1);
                Volatile.Write(ref transitionViewPlayerId, previousUncertain != 0 ? -1 :
                    previousPublished != 0 ? previousPublished : previousRaw);
                Volatile.Write(ref transitionActive, 1);
                Exception changeError = null;
                try
                {
                    setNativeView(playerId);
                    if (!ValidateContext(contextValid, out failure)) return false;
                    int observed = readNativeView();
                    if (!ValidateContext(contextValid, out failure)) return false;
                    if (observed == playerId)
                    {
                        Volatile.Write(ref selectedSpectatorPlayerId, playerId);
                        Volatile.Write(ref viewUncertain, 0);
                        Volatile.Write(ref transitionActive, 0);
                        changes.Enqueue(Volatile.Read(ref transitionViewPlayerId), playerId, PlayerPerspectiveChangeReason.Selected);
                        return true;
                    }
                }
                catch (Exception error) { changeError = error; }

                // Reentrant lifecycle replacement can occur even under the engine
                // monitor. Never restore or publish an old world's view in that case.
                if (!ValidateContext(contextValid, out failure)) return false;

                bool restored = false;
                Exception restoreError = null;
                try
                {
                    setNativeView(previousRaw);
                    if (!ValidateContext(contextValid, out failure)) return false;
                    int observed = readNativeView();
                    if (!ValidateContext(contextValid, out failure)) return false;
                    restored = observed == previousRaw;
                }
                catch (Exception error) { restoreError = error; }
                if (!ValidateContext(contextValid, out failure)) return false;
                Volatile.Write(ref selectedSpectatorPlayerId, restored ? previousPublished : 0);
                Volatile.Write(ref viewUncertain, restored ? previousUncertain : 1);
                if (restored)
                {
                    Volatile.Write(ref identityOverridePlayerId, previousIdentity);
                    Volatile.Write(ref identityOverrideActive, previousOverride);
                }
                Volatile.Write(ref transitionActive, 0);
                if (!restored && previousUncertain == 0)
                    changes.Enqueue(Volatile.Read(ref transitionViewPlayerId), -1, PlayerPerspectiveChangeReason.Uncertain);
                failure = "Native view " + playerId + " was not confirmed; rollback=" +
                    (restored ? "verified" : "failed") +
                    (changeError == null ? string.Empty : ", changeError=" + changeError) +
                    (restoreError == null ? string.Empty : ", rollbackError=" + restoreError) + ".";
                return false;
            }
        }

        private static bool ValidateContext(Func<bool> current, out string failure)
        {
            failure = null;
            try { if (current == null || current()) return true; }
            catch (Exception error) { failure = "View context check failed: " + error.Message; return false; }
            failure = "The protected view session or native world changed.";
            return false;
        }

        /// <summary>Clears the identity override when the spectator session ends.</summary>
        public static void ClearSpectatorView()
        {
            try
            {
                object engineGate = PlayerViewIdentityRuntime.Instance.EngineGate ?? perspectiveEngineGate;
                if (engineGate == null) ClearViewCore();
                else lock (engineGate) ClearViewCore();
            }
            catch (Exception error)
            { NativeApiLog.Error(transitionLog, "ELIMINATED_PLAYER_VIEW_CLEAR_FAILED: " + error); }
            finally { changes.Drain(); }
        }

        private static void ClearViewCore()
        {
            var core = PlayerViewIdentityRuntime.Instance.Identity;
            if (!TryClearOwnedView(core, viewOwner, ref viewLease, out string failure))
            {
                PlayerViewIdentityRuntime.Instance.ObserveFailure(core);
                NativeApiLog.Error(transitionLog, "ELIMINATED_PLAYER_VIEW_CLEAR_FAILED: " + failure); return;
            }
            lock (viewChangeGate)
            {
                int previous = Volatile.Read(ref viewUncertain) != 0 ? -1 :
                    Volatile.Read(ref selectedSpectatorPlayerId);
                bool hadOverride = previous != 0 || Volatile.Read(ref identityOverrideActive) != 0;
                Volatile.Write(ref selectedSpectatorPlayerId, 0);
                Volatile.Write(ref viewUncertain, 0);
                Volatile.Write(ref transitionActive, 0);
                Volatile.Write(ref identityOverrideActive, 0);
                Volatile.Write(ref identityOverridePlayerId, -1);
                if (hadOverride) changes.Enqueue(previous, -1, PlayerPerspectiveChangeReason.Cleared);
            }
        }

        // Shared production release path; fixtures combine it with LocalControlRuntime
        // and the exact same core to prove that public view cleanup cannot steal control.
        internal static bool TryClearOwnedView(ControlSaveIdentity core, object owner,
            ref ControlSaveIdentity.Lease ownedLease, out string reason)
        {
            reason = null;
            var captured = ownedLease;
            if (captured != null && core != null && core.IsOwned(owner, captured) &&
                !core.TryRestoreAndRelease(owner, captured, out reason)) return false;
            if (ReferenceEquals(ownedLease, captured)) ownedLease = null;
            return true;
        }
    }
}
