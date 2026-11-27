using System;
using APIShared;

namespace ThirdPartyMod
{
    internal static class MissionReadinessExample
    {
        private static IMissionPlayStateReadinessCapability readiness;
        private static Action<MissionContext> refreshCurrentMission;

        // Called once on Unity after APIShared managed initialization. Retain this runtime and
        // resolve current HUD/identity inside refresh; the readiness receipt does not grant either.
        internal static void Register(ModApiClient client, Action<MissionContext> refresh)
        {
            refreshCurrentMission = refresh;
            if (!client.TryGetMissionPlayStateReadiness(out readiness, out var diagnostic) ||
                !readiness.TryRegisterObserver("example/mission-state", OnReady, out diagnostic))
                throw new InvalidOperationException(diagnostic?.Reason);
        }
        private static void OnReady(MissionPlayStateReadyNotification notification)
        {
            if (readiness.Current?.SessionId != notification.Context.SessionId) return;
            refreshCurrentMission(notification.Context);
        }
    }
}
