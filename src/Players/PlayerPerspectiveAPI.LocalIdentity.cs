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
    // Local identity: the GetLocalPlayerId correction and the controlled/viewed/raw player readers.
    public static partial class PlayerPerspectiveAPI
    {
        internal static void Initialize(ManualLogSource log)
        {
            if (localPlayerIdHook != null) return;
            Hook candidate = null;
            try
            {
                perspectiveEngineGate = Internal.EngineMonitor.Current;
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
    }
}
