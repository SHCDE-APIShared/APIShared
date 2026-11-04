using BepInEx.Logging;
using MonoMod.RuntimeDetour;
using System;
using System.Threading;

namespace APIShared.ScriptExtenderFixes
{
    internal static class PopularityWorldPopupRuntime
    {
        private static ILHook hook;
        private static int attempted;

        internal static void Initialize(string nativeHash, ManualLogSource log)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            // Native spawn runs its mask updater before the caller installs the new
            // popularity snapshot. Vanilla caches that first mask in a Unity child.
            // This stateless managed correction survives all mission/HUD teardown.
            const string native = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
            if (!string.Equals(nativeHash, native, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("World popularity popup skipped: unsupported native mask contract.");
            var target = WorldPopularityIl.FindTarget();
            hook = new ILHook(target, WorldPopularityIl.Apply,
                new ILHookConfig { ManualApply = true, ID = "APIShared.WorldPopularityPopup" });
            // Root before publication; published hooks are never removed or retried.
            hook.Apply();
            if (!hook.IsApplied) throw new InvalidOperationException("World popularity popup IL publication incomplete.");
            log.LogInfo("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                "] WORLD_POPULARITY_POPUP_READY: masks use the displayed popup snapshot.");
        }
    }
}
