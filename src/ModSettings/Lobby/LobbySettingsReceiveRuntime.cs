using APIShared.Internal;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;
using R3;
using SHCDESE.API;
using SHCDESE.API.Components.Network;
using Steamworks;
using System;
using System.Reflection;
using System.Threading;

namespace APIShared.ModSettings
{
    internal static class LobbySettingsReceiveRuntime
    {
        private delegate void Receive(GameXAMLManagerAPI self, LobbyModSettingSyncPacket packet, CSteamID? sender);
        private static Hook hook;
        private static Receive original;
        private static LobbySettingsReceiveQueue queue;
        private static ManualLogSource log;
        private static object lobbyIdentity;
        private static ulong? lobbyId;
        private static long? missionId;
        private static int dispatchFailure;
        private static IDisposable missionEnded;

        internal static void RequireAvailable()
        {
            if (hook == null) throw new InvalidOperationException("Mod-settings main-thread receiver is unavailable.");
        }

        internal static void Initialize(ManualLogSource logger)
        {
            if (hook != null) return;
            log = logger;
            Hook candidate = null;
            try
            {
                if (!UnityMainThreadDispatch.IsMainThread)
                    throw new InvalidOperationException("Mod-settings receive initialization requires the Unity thread.");
                MethodInfo target = typeof(GameXAMLManagerAPI).GetMethod("ReceiveSettingsUpdate",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(LobbyModSettingSyncPacket), typeof(CSteamID?) }, null);
                if (target == null || target.ReturnType != typeof(void) || target.IsStatic || target.GetMethodBody() == null)
                    throw new MissingMethodException("GameXAMLManagerAPI.ReceiveSettingsUpdate contract changed.");
                queue = new LobbySettingsReceiveQueue(UnityMainThreadDispatch.TryEnqueue,
                    generation => { RefreshContext(); return generation == queue.Generation; },
                    (packet, sender) => original(GameXAMLManagerAPI.Instance, packet,
                        sender.HasValue ? new CSteamID(sender.Value) : (CSteamID?)null),
                    error => DebugLogHelper.LogError(log, "Mod-settings receive failed: " + error));
                RefreshContext();
                if (!ApiShared.Current.TryGetLobbyState(APISharedPlugin.PluginGuid, out var lobby, out var diagnostic) ||
                    !lobby.TryRegisterObserver("ModSettings.ReceiveContext", _ => RefreshContext(), out diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Lobby-state receiver observation unavailable.");
                // Record a mission end when it happens, so the first lobby packet afterwards is not
                // mistaken for stale work of the ended mission. Process-lifetime subscription.
                MissionEvents.SetOwner(APISharedPlugin.PluginGuid);
                if (missionEnded == null) missionEnded = MissionEvents.Ended.Subscribe(_ => RefreshContext());
                candidate = new Hook(target, (Receive)OnReceive,
                    new HookConfig { ManualApply = true, ID = "APIShared.ModSettings.Receive" });
                original = candidate.GenerateTrampoline<Receive>();
                candidate.Apply();
                hook = candidate; // Retained for process lifetime; no runtime unpatch path.
            }
            catch (Exception error)
            {
                if (hook == null) candidate?.Dispose();
                DebugLogHelper.LogError(log, "Mod-settings main-thread receiver unavailable: " + error);
            }
        }

        private static void RefreshContext()
        {
            if (!UnityMainThreadDispatch.IsMainThread)
            {
                UnityMainThreadDispatch.TryEnqueue(RefreshContext);
                return;
            }
            var currentLobby = Platform_Multiplayer.Instance?.activeLobby;
            if (Interlocked.Exchange(ref dispatchFailure, 0) != 0)
                DebugLogHelper.LogError(log, "Mod-settings receive dropped: Unity main-thread dispatch unavailable.");
            ulong? currentLobbyId = currentLobby?.id.m_SteamID;
            long? currentMission = MissionLifecycleService.ActiveContext?.SessionId;
            bool lobbyChanged = !ReferenceEquals(lobbyIdentity, currentLobby) || lobbyId != currentLobbyId;
            // A start from this lobby keeps queued host values current: they are the values the
            // host started with, and FlushBeforeMultiplayerStart applies them before the map loads.
            // Leaving the lobby or a started/replaced mission makes queued values stale.
            bool missionLeft = missionId.HasValue && missionId != currentMission;
            lobbyIdentity = currentLobby;
            lobbyId = currentLobbyId;
            missionId = currentMission;
            if (lobbyChanged || missionLeft) queue?.Invalidate();
        }

        // Called by the mission lifecycle on the Unity thread inside Platform_Multiplayer.StartGame/StartSave:
        // Vanilla has received the last lobby packets (RefreshLobbyList) and has not loaded the map yet.
        // Queued settings are applied now, as the Extender's synchronous receiver would have done.
        internal static void FlushBeforeMultiplayerStart()
        {
            if (queue == null || !UnityMainThreadDispatch.IsMainThread) return;
            queue.Flush();
        }

        private static void OnReceive(GameXAMLManagerAPI self, LobbyModSettingSyncPacket packet, CSteamID? sender)
        {
            if (!queue.Enqueue(packet, sender?.m_SteamID)) Interlocked.Exchange(ref dispatchFailure, 1);
        }
    }
}
