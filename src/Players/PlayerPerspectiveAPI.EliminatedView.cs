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
    // Eliminated-participant views: foreign report viewpoints that keep the positive local identity.
    public static partial class PlayerPerspectiveAPI
    {
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
                    var observations = ApiSharedRuntime.ProcessInstance.PlayerDefeat;
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
            // An original spectator's input control also holds a positive override; it is not an
            // eliminated-participant view and stays with ClearSpectatorControl/ClearSpectatorView.
            if (viewLease != null || (!inputControl.IsEngaged &&
                Volatile.Read(ref identityOverrideActive) != 0 && Volatile.Read(ref identityOverridePlayerId) > 0))
                ClearSpectatorView();
        }

        private static void OnViewIdentityFailure(PlayerViewIdentity.FailureRecord failure)
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
        internal static bool TryInvalidateOwnedView(PlayerViewIdentity core, object owner,
            PlayerViewIdentity.Lease ownedLease, PlayerViewIdentity.FailureRecord failure)
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
    }
}
