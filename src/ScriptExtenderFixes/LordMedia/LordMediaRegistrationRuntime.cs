using APIShared.Internal;
using BepInEx.Logging;
using SHCDESE.API;
using System;
using System.Collections.Generic;
using System.IO;

namespace APIShared.ScriptExtenderFixes
{
    internal static class LordMediaRegistrationRuntime
    {
        private static readonly LordMediaRegistrationReport report = new LordMediaRegistrationReport();
        private static IMissionLifecycleCapability lifecycle;
        private static ManualLogSource log;
        private static bool initialized;

        // The central lifecycle retains these static callbacks past Unity's startup cleanup.
        // Register only after managed services and UnityMainThreadDispatch are initialized.
        internal static void Initialize(ManualLogSource logger)
        {
            if (initialized) return;
            initialized = true;
            log = logger;
            try
            {
                if (!ApiShared.Current.TryGetMissionLifecycle(APISharedPlugin.PluginGuid, out lifecycle, out var diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Mission lifecycle unavailable.");
                if (!lifecycle.TryRegisterObserver("ScriptExtenderFixes.LordMedia", OnStart, OnEnd, null, out diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Mission observer registration failed.");
            }
            catch (Exception error) { Failed(error); }
        }

        private static void OnStart(MissionLifecycleNotification notification)
        {
            long expected = notification.Context.SessionId;
            if (!UnityMainThreadDispatch.TryRunInlineOrEnqueue(() =>
                report.Report(expected, () => lifecycle.Current?.SessionId, Capture, Exists, Warn, Failed)))
                Failed(new InvalidOperationException("Unity main-thread dispatch unavailable."));
        }

        private static void OnEnd(MissionLifecycleNotification notification)
        {
            long ended = notification.Context.SessionId;
            // An old queued End may run after a newer Start; End checks the exact session.
            UnityMainThreadDispatch.TryRunInlineOrEnqueue(() => report.End(ended));
        }

        private static IEnumerable<LordMediaSlot> Capture()
        {
            var ai = GameAIManagerAPI.Instance;
            var players = GamePlayerManagerAPI.Instance;
            // The real game exposes this public snapshot; no publicized private-field access.
            List<CustomisationFileManager.CustomLord> catalog = CustomisationFileManager.Instance.GetCustomLords();
            return Capture(catalog, id => ai.IsCustomLord(players.GetAILord(id)), ai.GetCustomAILordNameByPlayerId,
                name => ai.LordDataDict.ContainsKey(name.ToLower()));
        }

        internal static IEnumerable<LordMediaSlot> Capture(IEnumerable<CustomisationFileManager.CustomLord> catalog,
            Func<int, bool> isCustomSlot, Func<int, string> getName, Func<string, bool> isRegistered)
        {
            var byName = new Dictionary<string, CustomisationFileManager.CustomLord>(StringComparer.Ordinal);
            foreach (var lord in catalog)
            {
                if (lord == null || string.IsNullOrEmpty(lord.lordName)) continue;
                // Ambiguous identities must never select an arbitrary package for a warning.
                if (byName.ContainsKey(lord.lordName)) byName[lord.lordName] = null;
                else byName.Add(lord.lordName, lord);
            }
            var slots = new List<LordMediaSlot>();
            for (int playerId = 1; playerId <= GamePlayerManagerAPI.MAX_PLAYERS; playerId++)
            {
                if (!isCustomSlot(playerId)) continue;
                string name = getName(playerId);
                if (string.IsNullOrEmpty(name) || isRegistered(name) ||
                    !byName.TryGetValue(name, out var lord) || lord == null) continue;
                slots.Add(new LordMediaSlot(playerId, name, lord.lordDisplayName, lord.customPath, false));
            }
            return slots;
        }

        private static bool Exists(string path)
        {
            // File.Exists also returns false on access errors. Those are not proof of absence.
            try { return (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static void Warn(string message) => NativeApiLog.Warning(log, "SE_LORD_MEDIA: " + message);
        private static void Failed(Exception error) => NativeApiLog.Warning(log,
            "SE_LORD_MEDIA: Lord registration diagnostics could not complete; no missing Lord metadata is inferred. " + error);
    }
}
