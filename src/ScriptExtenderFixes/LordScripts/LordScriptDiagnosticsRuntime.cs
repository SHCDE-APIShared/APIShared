using APIShared.Internal;
using BepInEx.Logging;
using SHCDESE.API;
using SHCDESE.API.Components.AI;
using SHCDESE.API.Components.ModManager;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace APIShared.ScriptExtenderFixes
{
    // Logs scripted Lords once per real multiplayer match (SE_LORD_SCRIPTS). Script Extender runs a Lord's init.lua
    // per player slot by the slot's local Lord name (LuaManager.RegisterActiveLordAIs ->
    // GameAIManagerAPI.GetCustomAILordNameByPlayerId -> LordDataDict[name.ToLower()].LuaInitPath), only when its
    // local AllowScriptedLords option is on, and checks only NetworkMode 1 packages when joining a lobby. Logging
    // only: nothing is blocked, sent or changed.
    internal static class LordScriptDiagnosticsRuntime
    {
        private const int MaxPlayers = 8;
        private static IMissionLifecycleCapability lifecycle;
        private static ManualLogSource log;
        private static bool initialized;

        // Called after managed service initialization on the validated Unity thread. The lifecycle observer is
        // process-lived and outlives the plugin component destroyed by the startup cleanup.
        internal static void Initialize(ManualLogSource logger)
        {
            if (initialized) return;
            initialized = true;
            log = logger;
            try
            {
                if (!ApiShared.Current.TryGetMissionLifecycle(APISharedPlugin.PluginGuid, out lifecycle, out var diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Mission lifecycle unavailable.");
                if (!lifecycle.TryRegisterObserver("ScriptExtenderFixes.LordScripts", OnStart, null, null, out diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Mission observer registration failed.");
            }
            catch (Exception exception)
            {
                NativeApiLog.Warning(log, "SE_LORD_SCRIPTS unavailable: " + exception);
            }
        }

        private static void OnStart(MissionLifecycleNotification notification)
        {
            if (notification.IsReplay || notification.Context.IsEditor || !notification.Context.Mode.IsRealMultiplayer) return;
            if (!UnityMainThreadDispatch.TryRunInlineOrEnqueue(() =>
            {
                // A queued notification must not describe a later map under an old session ID.
                if (lifecycle.Current?.SessionId != notification.Context.SessionId) return;
                try { Report(); }
                catch (Exception exception) { NativeApiLog.Warning(log, "SE_LORD_SCRIPTS check failed: " + exception); }
            })) NativeApiLog.Warning(log, "SE_LORD_SCRIPTS unavailable: Unity main-thread dispatch unavailable.");
        }

        private static void Report()
        {
            GameAIManagerAPI ai = GameAIManagerAPI.Instance;
            Dictionary<string, CustomLordEntry> lords = ai?.LordDataDict;
            if (lords == null || lords.Count == 0) return;
            var packages = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<ModInfo, string> registered in GameAssetModManager.Instance.GetRegisteredAssetDirectories())
                if (registered.Key?.GUID != null && !packages.ContainsKey(registered.Key.GUID))
                    packages[registered.Key.GUID] = registered.Key;

            var slots = new List<LordScriptSlot>();
            for (int playerId = 1; playerId <= MaxPlayers; playerId++)
            {
                string name = ai.GetCustomAILordNameByPlayerId(playerId);
                // Same lookup as Script Extender (culture-sensitive ToLower, matching LordDataDict keys).
                if (string.IsNullOrEmpty(name) || !lords.TryGetValue(name.ToLower(), out CustomLordEntry entry) ||
                    string.IsNullOrEmpty(entry?.LuaInitPath))
                    continue;
                ModInfo package = entry.AssetProviderGuid != null &&
                    packages.TryGetValue(entry.AssetProviderGuid, out ModInfo found) ? found : null;
                slots.Add(new LordScriptSlot(playerId, name, package?.GUID ?? entry.AssetProviderGuid, package?.Version,
                    package == null ? (bool?)null : package.NetworkMode == ModNetworkMode.Networked, HashFile(entry.LuaInitPath)));
            }
            if (slots.Count == 0) return;

            bool scriptsAllowed = SHCDESE.BepInEx.Bootstrap.Plugin.Instance?.AllowScriptedLords?.Value ?? true;
            NativeApiLog.Info(log, "SE_LORD_SCRIPTS: " + LordScriptReport.Summary(slots, scriptsAllowed));
            foreach (string warning in LordScriptReport.Warnings(slots, scriptsAllowed))
                NativeApiLog.Warning(log, "SE_LORD_SCRIPTS: " + warning);
        }

        // Short content identity for comparing players' logs; a missing or unreadable file is named instead.
        private static string HashFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return "missing";
                using (SHA256 sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").Substring(0, 16);
            }
            catch (Exception exception)
            {
                return "unreadable(" + exception.GetType().Name + ")";
            }
        }
    }
}
