using APIShared.Internal;
using BepInEx.Logging;
using System;
using System.Diagnostics;

namespace APIShared.ModSettings
{
    /// <summary>Unity side of PersonalSettingsWriteQueue: frame-scheduled quiet-period flushes and the
    /// mission lifecycle and process exit flush points.</summary>
    /// <remarks>Initialized once on the validated Unity thread after managed initialization. The UI work
    /// registration, the Application.quitting handler and the mission observer are static, process-lifetime
    /// roots that outlive the destroyed plugin component. The frame callback has demand only while a
    /// write is pending. Each part fails independently; without the frame scheduler edits write at once.</remarks>
    internal static class PersonalSettingsWriteRuntime
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static bool initialized;
        private static ManualLogSource log;

        internal static void Initialize(ManualLogSource logger)
        {
            if (initialized) return;
            initialized = true;
            log = logger;
            try
            {
                if (!UnityMainThreadDispatch.IsMainThread)
                    throw new InvalidOperationException("Personal settings writes must initialize on the validated Unity thread.");
                // Also retries failed immediate writes when the frame scheduler is unavailable.
                UnityEngine.Application.quitting += FlushReported;
                if (!Presentation.UiWorkRuntime.TryRegister(APISharedPlugin.PluginGuid, "internal/40-personal-settings-writes",
                    PersonalSettingsWriteQueue.OnQuietPeriodElapsed, 0, out Presentation.IUiFrameWorkRegistration work, out string reason))
                    throw new InvalidOperationException(reason);
                PersonalSettingsWriteQueue.Bind(new PersonalSettingsWriteQueue.Binding(
                    () => Clock.Elapsed, work.RequestAfter,
                    () => UnityMainThreadDispatch.IsMainThread, UnityMainThreadDispatch.TryEnqueue));
            }
            catch (Exception exception)
            {
                NativeApiLog.Warning(log, "PERSONAL_SETTINGS_WRITES: settings edits are written immediately; " + exception);
            }
            try
            {
                if (!ApiShared.Current.TryGetMissionLifecycle(APISharedPlugin.PluginGuid, out IMissionLifecycleCapability lifecycle,
                        out NativeCapabilityDiagnostic diagnostic) ||
                    !lifecycle.TryRegisterObserver("ModSettings.PersonalSettingsWrites", OnMission, OnMission, OnMission, out diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Mission observer registration failed.");
            }
            catch (Exception exception)
            {
                NativeApiLog.Warning(log, "PERSONAL_SETTINGS_WRITES: mission start and end flushes are unavailable; " + exception);
            }
        }

        // Every initialization checkpoint, start and end: a launch, map start or return to the menu
        // never leaves an edit only in memory. Clean controllers return at once.
        private static void OnMission(MissionLifecycleNotification notification)
        {
            if (!UnityMainThreadDispatch.TryRunInlineOrEnqueue(FlushReported))
                NativeApiLog.Warning(log, "PERSONAL_SETTINGS_WRITES: Unity main-thread dispatch unavailable for a mission flush.");
        }

        // Storage failures are already reported by the storage; this reports unexpected exceptions.
        private static void FlushReported()
        {
            try { PersonalSettingsWriteQueue.FlushAll(); }
            catch (Exception exception) { NativeApiLog.Error(log, "PERSONAL_SETTINGS_WRITES: flush failed; the edit stays pending: " + exception); }
        }
    }
}
