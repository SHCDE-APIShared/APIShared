using APIShared;
using APIShared.Presentation;
using System;

namespace ThirdPartyMod
{
    // Keep this runtime for the process. Resolve the current HUD inside Refresh, never capture a map's view.
    internal static class UiWorkExample
    {
        private static IUiFrameWorkRegistration refresh;
        private static Action updateCurrentHud;
        private static long session;

        internal static void Register(ModApiClient client, Action currentHudRefresh)
        {
            updateCurrentHud = currentHudRefresh;
            if (!client.Ui.TryRegisterBeforeRender("example/refresh", Refresh, out refresh, out string reason))
                throw new InvalidOperationException(reason);
        }
        internal static void SessionStarted(long id) { System.Threading.Interlocked.Exchange(ref session, id); refresh.Request(); }
        internal static void SessionEnded() { System.Threading.Interlocked.Exchange(ref session, 0); refresh.CancelPending(); refresh.SetContinuous(false); }
        internal static void Changed() => refresh.Request();
        internal static void RetryLater() => refresh.RequestAfter(TimeSpan.FromMilliseconds(100));
        internal static void Dragging(bool active) => refresh.SetContinuous(active);
        private static void Refresh() { if (System.Threading.Interlocked.Read(ref session) != 0) updateCurrentHud(); }
    }
}
