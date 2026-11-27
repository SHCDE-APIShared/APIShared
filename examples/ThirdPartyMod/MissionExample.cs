using System;
using APIShared;
using APIShared.GameModes;

namespace ThirdPartyMod
{
    internal static class MissionExample
    {
        // Arbitrary foreign GUID, optional contexts and multiplayer restriction.
        internal static readonly GameplayModActivationProfile Profile = new GameplayModActivationProfile(
            ExamplePlugin.Guid, "APIShared Example", GameplayModAllowedContext.CustomGame |
            GameplayModAllowedContext.MapEditor, allowRealMultiplayer: false);
        private static readonly GameplayModeGate Gate = new GameplayModeGate(Profile);
        private static Action<string> log;
        private static NativeWorldReadClient nativeReads;

        // Only for a confirmed restart of the current mission. Wrap its complete
        // preparation, so settings owners see the replacement before cleanup.
        internal static void Restart(Action prepareAndLoad) => MissionRestart.Run(ExamplePlugin.Guid, prepareAndLoad);

        internal static void Register(ModApiClient api, Action<string> logger)
        {
            log = logger;
            api.WhenReady(AcquireNativeReads);
            if (!api.TryGetMissionLifecycle(out var missions, out var diagnostic) ||
                !missions.TryRegisterObserver("missions", OnStarted, OnEnded, Gate.Update, out diagnostic))
                log(diagnostic.Reason);
        }

        private static void AcquireNativeReads(ModApiClient api)
        {
            if (!api.TryGetNativeWorldReads(out nativeReads, out var diagnostic))
                log(diagnostic.Reason);
        }

        // A later observer can receive an old Start after an earlier observer replaced
        // the world. This is diagnostic readback, with no game mutation or pointer access.
        private static void ObserveCurrentWorld(MissionLifecycleNotification notification)
        {
            var reads = nativeReads;
            if (reads == null) return;
            NativeWorldReadSnapshot observed;
            lock (reads.SyncRoot)
            {
                if (!reads.TryReadCurrent(out observed, out _) ||
                    observed.Mission.SessionId != notification.Context.SessionId) return;
            }
            // Log outside the monitor. The receipt describes that read, not a continuing lease.
            log($"Observed native world {observed.WorldId}, mode {observed.Mode.Kind}, real MP={observed.Mode.IsRealMultiplayer}");
        }

        // Revalidate exact world identity and return fresh metadata when refreshing a
        // retained observation. Callers must use current.Mode, not expected.Mode.
        internal static bool TryRefreshWorldObservation(NativeWorldReadSnapshot expected,
            out NativeWorldReadSnapshot current, out string reason)
        {
            var reads = nativeReads;
            if (reads == null)
            { current = default; reason = "Protected native-world reads are unavailable."; return false; }
            lock (reads.SyncRoot) return reads.TryReadCurrent(expected, out current, out reason);
        }

        private static void OnStarted(MissionLifecycleNotification notification)
        {
            Gate.Update(notification);
            bool allowed = Gate.IsAllowed;
            string reason = Gate.Reason;
            log($"Mission {notification.Context.SessionId}: allowed={allowed}, replay={notification.IsReplay}, reason={reason}");
            ObserveCurrentWorld(notification);
            // A policy decision only: a real mod decides which of its own features to activate.
        }

        private static void OnEnded(MissionLifecycleNotification notification)
        {
            Gate.Update(notification);
            log($"Mission {notification.Context.SessionId} ended; clean up mod-local session state here.");
        }
    }
}
