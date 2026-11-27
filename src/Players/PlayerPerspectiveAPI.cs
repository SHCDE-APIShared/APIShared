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
    public static partial class PlayerPerspectiveAPI
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
        private static PlayerViewIdentity.Lease viewLease;
        private static int selectedSpectatorPlayerId;
        private static int transitionViewPlayerId;
        private static int transitionActive;
        private static int viewUncertain;

        /// <summary>
        /// Changes Vanilla's view and publishes it for an original spectator only.
        /// Returns false while another role owns the participant view or a Save/send scope is active.
        /// The selection lasts for the current native world: every load, unload or new mission clears it
        /// with a Cleared change notification, so consumers select again for each mission.
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
                {
                    // A plain view selection ends input control first (with Vanilla deselection).
                    EndControl(true);
                    changed = TryChangeViewCore(playerId, GetRawNativeViewPlayerId, EngineInterface.SetEditorPlayer,
                        out failure, contextValid: IsUnboundViewIdentity);
                }
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
            // The ended world's input actor goes first, without any GameAction into the new world.
            if (inputControl.IsEngaged) try
            {
                object engineGate = PlayerViewIdentityRuntime.Instance.EngineGate ?? perspectiveEngineGate;
                if (engineGate == null) EndControl(false);
                else lock (engineGate) EndControl(false);
            }
            catch (Exception error) { NativeApiLog.Error(transitionLog, "PLAYER_CONTROL_RETIRE_FAILED: " + error); }
            PlayerViewIdentityRuntime.Instance.RetireEndedWorld();
            ClearSpectatorView();
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

        /// <summary>Clears the identity override when the spectator session ends. APIShared also calls it
        /// at every native world change (load, unload, new mission).</summary>
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
            EndControl(true);
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

        // Shared release path of an owned view lease; only the exact owner and lease release it.
        internal static bool TryClearOwnedView(PlayerViewIdentity core, object owner,
            ref PlayerViewIdentity.Lease ownedLease, out string reason)
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
