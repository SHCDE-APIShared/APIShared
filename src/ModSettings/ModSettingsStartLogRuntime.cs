using APIShared.Internal;
using BepInEx.Logging;
using System;

namespace APIShared.ModSettings
{
    internal static class ModSettingsStartLogRuntime
    {
        private static IMissionLifecycleCapability lifecycle;
        private static ModSettingsStartLog writer;
        private static ManualLogSource log;

        // Called after managed service initialization on the validated Unity thread.
        // The static owner and lifecycle observer outlive the destroyed plugin component.
        internal static void Initialize(ManualLogSource logger)
        {
            if (writer != null) return;
            log = logger;
            try
            {
                if (!ApiShared.Current.TryGetMissionLifecycle(APISharedPlugin.PluginGuid, out lifecycle, out var diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Mission lifecycle unavailable.");
                writer = new ModSettingsStartLog(() => ModSettingsApplication.Endpoints,
                    message => DebugLogHelper.LogInfo(log, message),
                    (key, error) => DebugLogHelper.LogWarning(log, "MODSETTINGS_READ_FAILED " + key + ": " + error));
                if (!lifecycle.TryRegisterObserver("ModSettings.StartLog", OnStart, null, null, out diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Mission observer registration failed.");
            }
            catch (Exception exception)
            {
                DebugLogHelper.LogWarning(log, "MODSETTINGS_UNAVAILABLE: " + exception);
            }
        }

        private static void OnStart(MissionLifecycleNotification notification)
        {
            if (!UnityMainThreadDispatch.TryRunInlineOrEnqueue(() =>
            {
                // A queued notification cannot read a later map's settings under an old session ID.
                if (lifecycle.Current?.SessionId != notification.Context.SessionId) return;
                try { writer.Handle(notification); }
                catch (Exception exception) { DebugLogHelper.LogWarning(log, "MODSETTINGS_FAILED: " + exception); }
            })) DebugLogHelper.LogWarning(log, "MODSETTINGS_UNAVAILABLE: Unity main-thread dispatch unavailable.");
        }
    }
}
