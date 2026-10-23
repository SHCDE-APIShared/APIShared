using APIShared.Internal;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;
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
            if (ReferenceEquals(lobbyIdentity, currentLobby) && lobbyId == currentLobbyId && missionId == currentMission) return;
            lobbyIdentity = currentLobby;
            lobbyId = currentLobbyId;
            missionId = currentMission;
            queue?.Invalidate();
        }

        private static void OnReceive(GameXAMLManagerAPI self, LobbyModSettingSyncPacket packet, CSteamID? sender)
        {
            if (!queue.Enqueue(packet, sender?.m_SteamID)) Interlocked.Exchange(ref dispatchFailure, 1);
        }
    }
}
