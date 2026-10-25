using BepInEx.Logging;
using CrusaderDE;
using SHCDESE.GameGlobals;
using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.ScriptExtenderFixes
{
    // Process lifetime registration; missions only change the logical gate. The
    // detached report always comes from the current player perspective, including previews.
    internal sealed class PopularityDisplayRuntime
    {
        private static PopularityDisplayRuntime instance;
        private long session;
        private PopularityParameters parameters;
        private PopularityPresentation presentation;
        internal static void Initialize(string nativeHash, ManualLogSource log)
        {
            if (instance != null) return;
            const string native = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
            const string managed = "BC8B6A395F01D48557DB413600C8DD8D1FDFD3ABDF97BFBBB68A3C56B04FD789";
            if (!string.Equals(nativeHash, native, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Popularity display skipped: unsupported native/managed report contract.");
            Internal.InstalledAssemblyIdentity.Verify(typeof(MainViewModel).Assembly,
                Internal.InstalledAssemblyIdentity.GameAssemblyPath(BepInEx.Paths.GameRootPath), managed);
            var candidate = new PopularityDisplayRuntime();
            candidate.presentation = new PopularityPresentation(candidate.Capture,
                text => log.LogInfo(Stamp(text)), text => log.LogError(Stamp(text)));
            if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out var missions, out var diagnostic) ||
                !missions.TryRegisterObserver("popularity-display", candidate.Started, candidate.Ended, null, out diagnostic))
                throw new InvalidOperationException("Popularity display mission lifecycle unavailable: " + diagnostic?.Reason);
            candidate.presentation.Register();
            instance = candidate;
        }
        private void Started(MissionLifecycleNotification note) => Interlocked.Exchange(ref session, note.Context.SessionId);
        private void Ended(MissionLifecycleNotification note) => Interlocked.CompareExchange(ref session, 0, note.Context.SessionId);
        private PopularityDisplayCapture Capture()
        {
            long current = Interlocked.Read(ref session);
            if (current == 0) return default;
            if (parameters == null) parameters = new PopularityParameters(GameGlobalsManager.Instance);
            var selection=Economy.PopularityPlayerRuntime.CaptureReport(PlayerPerspectiveAPI.GetViewedPlayerId());
            return new PopularityDisplayCapture(current,parameters.Refresh(selection),selection);
        }
        private static string Stamp(string text) => "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + text;
    }
}
