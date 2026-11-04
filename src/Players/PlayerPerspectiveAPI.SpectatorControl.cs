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
    // Spectator input control: an original spectator controls one chosen player's Vanilla input.
    public static partial class PlayerPerspectiveAPI
    {
        // Sole owner of the native input actor; see LocalInputControl.
        private static readonly LocalInputControl inputControl = new LocalInputControl(ControlInputNative.WriteActor);
        private static readonly Action<int> deselectNative = DeselectNative;

        /// <summary>
        /// Declares that a mod uses spectator input control. Without any registration APIShared
        /// installs nothing for this feature. The first registration installs two dormant native input
        /// boundaries for the process lifetime (actor zero follows Vanilla completely). Call from the
        /// mod's CrusaderLibrary.LibraryLoaded handler (no thread suspension) or outside a running
        /// mission; inside a mission the first registration is refused. Idempotent per owner GUID.
        /// Returns false with a reason for an unsupported native image or a failed installation.
        /// </summary>
        public static bool TryRegisterSpectatorControl(string ownerGuid, out string reason)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid)) { reason = "An owner GUID is required."; return false; }
            return ControlInputNative.TryInstall(out reason);
        }

        /// <summary>
        /// Gives an original spectator the complete Vanilla map input of one chosen player and selects
        /// that player's view: selection, unit orders, building panels, and the view-based economy
        /// actions (tax, rations). There are no rights categories; the player's AI keeps acting too.
        /// Requirements: Unity main thread, a ready offline multiplayer-type game (local skirmish) with
        /// Vanilla's local spectator flag, a nonpositive EditorDirector.ActivePlayerID, no participant
        /// view lease and an occupied human/skirmish slot, after TryRegisterSpectatorControl succeeded.
        /// Real multiplayer and the map editor (where Vanilla switches players itself) return false.
        /// Switching to another player first deselects the previous player's units through Vanilla's
        /// own path. While active, GetLocalPlayerId reports the controlled player. Managed placement
        /// and demolition (EngineInterface.PlaceMapperItem/DeleteBuilding) pass
        /// EditorDirector.ActivePlayerID; the consumer owning such a hook substitutes
        /// GetInputControlledPlayerId. Control ends with ClearSpectatorControl, any other view
        /// selection or clear, and every native world change (load, unload, new mission). A failed
        /// view write rolls back; an unconfirmed rollback blocks spectator input until cleared.
        /// </summary>
        public static bool TrySetSpectatorControl(int playerId)
        {
            if (!Internal.UnityMainThreadDispatch.IsMainThread || localPlayerIdHook == null ||
                playerId < 1 || playerId > 8 || !ControlInputNative.IsReady) return false;
            object engineGate = PlayerViewIdentityRuntime.Instance.EngineGate ?? perspectiveEngineGate;
            if (engineGate == null || GameModes.GameModeHelper.IsMapEditor() || GameModes.GameModeHelper.IsRealMultiplayer())
                return false;
            bool changed = false;
            string failure = null;
            try
            {
                lock (engineGate)
                {
                    EngineInterface.PlayState state = GameData.Instance?.lastGameState;
                    EditorDirector director = EditorDirector.instance;
                    long world = MissionLifecycleService.NativeWorld?.Sequence ?? 0;
                    if (state == null || director == null || world <= 0 || !ShouldOverrideLocalPlayerId(
                            playerId, state.game_type, state.spectatorMode, director.ActivePlayerID) ||
                        !state.is_human_or_skirmish_player(playerId) || !IsUnboundViewIdentity())
                        return false;
                    changed = TrySetControlCore(inputControl, playerId, world, GetRawNativeViewPlayerId,
                        EngineInterface.SetEditorPlayer, deselectNative,
                        () => IsUnboundViewIdentity() && MissionLifecycleService.NativeWorld?.Sequence == world, out failure);
                }
            }
            catch (Exception error) { changed = false; failure = error.ToString(); }
            finally { changes.Drain(); }
            if (!changed && failure != null) NativeApiLog.Error(transitionLog, "PLAYER_CONTROL_SWITCH_FAILED: " + failure);
            return changed;
        }

        /// <summary>
        /// Ends spectator input control after Vanilla-deselecting the controlled player's units. The
        /// selected view stays; spectator input is suppressed again. Safe to call when inactive.
        /// </summary>
        public static void ClearSpectatorControl()
        {
            try
            {
                object engineGate = PlayerViewIdentityRuntime.Instance.EngineGate ?? perspectiveEngineGate;
                if (engineGate == null) EndControl(true);
                else lock (engineGate) EndControl(true);
            }
            catch (Exception error) { NativeApiLog.Error(transitionLog, "PLAYER_CONTROL_CLEAR_FAILED: " + error); }
        }

        /// <summary>Gets the player whose map input the original spectator currently controls, or 0.</summary>
        public static int GetInputControlledPlayerId()
        {
            int player = inputControl.Player;
            if (player <= 0) return 0;
            return inputControl.IsActiveIn(MissionLifecycleService.NativeWorld?.Sequence ?? 0) ? player : 0;
        }

        // Caller holds the engine monitor. The injected delegates let the transaction be tested
        // without a running game. The new actor is published only for a confirmed native view.
        internal static bool TrySetControlCore(LocalInputControl control, int playerId, long world,
            Func<int> readView, Action<int> setView, Action<int> deselect, Func<bool> contextValid, out string failure)
        {
            failure = null;
            // A binding from an ended world carries nothing into this one.
            if (control.IsEngaged && control.World != world) control.Release();
            int previous = control.Player;
            if (previous == playerId && readView() == playerId) return true;
            // Vanilla deselects as the CURRENT view; the queued command keeps that source.
            if (previous > 0 && readView() == previous) deselect(previous);
            if (TryChangeViewCore(playerId, readView, setView, out failure, playerId, contextValid))
            {
                control.Activate(world, playerId);
                return true;
            }
            // A verified rollback restored the previous view (and its identity override). An unconfirmed
            // one blocks input and reports the spectator identity again, never the unconfirmed target.
            if (Volatile.Read(ref viewUncertain) != 0) { control.Suspend(); ResetSpectatorControlIdentity(); }
            else if (previous <= 0 || readView() != previous) control.Release();
            return false;
        }

        // Positive overrides of an original spectator come only from control; a participant's leased
        // identity is never touched here.
        private static void ResetSpectatorControlIdentity()
        {
            lock (viewChangeGate)
                if (viewLease == null && Volatile.Read(ref identityOverridePlayerId) > 0)
                    Volatile.Write(ref identityOverridePlayerId, -1);
        }

        // Caller holds the engine monitor (or no monitor exists). Deselection runs only while the
        // controlled player's view is still current in the bound world.
        internal static void EndControlCore(LocalInputControl control, long world, Func<int> readView, Action<int> deselect)
        {
            if (!control.IsEngaged) return;
            int controlled = control.Player;
            try
            {
                if (deselect != null && controlled > 0 && world > 0 && control.World == world && readView() == controlled)
                    deselect(controlled);
            }
            finally
            {
                control.Release();
                ResetSpectatorControlIdentity();
            }
        }

        // Dormant fast path: without engaged control no lifecycle or native read happens.
        private static void EndControl(bool deselect)
        {
            if (!inputControl.IsEngaged) return;
            EndControlCore(inputControl, MissionLifecycleService.NativeWorld?.Sequence ?? 0,
                GetRawNativeViewPlayerId, deselect ? deselectNative : null);
        }

        // Vanilla's own right-click/troop-panel deselection (GameAction CloseTroopsPanel ->
        // native 0x897F0): local marking clear plus a queued synchronized unselect whose source
        // is the current native view. Failure leaves only the old selection; it is reported.
        private static void DeselectNative(int playerId)
        {
            try { EngineInterface.GameAction(Enums.GameActionCommand.CloseTroopsPanel, 0, 0); }
            catch (Exception error) { NativeApiLog.Error(transitionLog, "PLAYER_CONTROL_DESELECT_FAILED: player=" + playerId + ": " + error); }
        }
    }
}
