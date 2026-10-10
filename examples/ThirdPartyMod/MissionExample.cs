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

        internal static void Register(ModApiClient api, Action<string> logger)
        {
            log = logger;
            if (!api.TryGetMissionLifecycle(out var missions, out var diagnostic) ||
                !missions.TryRegisterObserver("missions", OnStarted, OnEnded, Gate.Update, out diagnostic))
                log(diagnostic.Reason);
        }

        private static void OnStarted(MissionLifecycleNotification notification)
        {
            Gate.Update(notification);
            bool allowed = Gate.IsAllowed;
            string reason = Gate.Reason;
            log($"Mission {notification.Context.SessionId}: allowed={allowed}, replay={notification.IsReplay}, reason={reason}");
            // A policy decision only: a real mod decides which of its own features to activate.
        }

        private static void OnEnded(MissionLifecycleNotification notification)
        {
            Gate.Update(notification);
            log($"Mission {notification.Context.SessionId} ended; clean up mod-local session state here.");
        }
    }
}
