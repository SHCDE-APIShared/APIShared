using System;
using System.Threading;
using BepInEx.Logging;
using R3;

namespace APIShared
{
    // Admission for inline stubs that APIShared installs on demand (a consumer's first registration)
    // instead of at LibraryLoaded. RedBird's inline hooks never suspend other threads while they write
    // the 14-byte jump, so such a patch is only safe where no thread can execute the span. The
    // gameplay spans that use this window (Hunter owner gates, herd flight gates) run only inside
    // a map's simulation; before the first mission initialization of the process no map exists, so no
    // thread executes them. The window closes for good on the first mission initialization notification
    // (BeforeLoad onwards) and never reopens, also not between missions, because a lobby or loading
    // screen gives no guarantee that the simulation thread has left those functions.
    internal static class NativeInlinePatchWindow
    {
        private static int closed;
        private static IDisposable subscription;

        internal static bool IsOpen => Volatile.Read(ref closed) == 0;

        internal const string ClosedReason =
            "This native hook installs on the first registration only before the first mission starts; register during startup.";

        // APIShared native initialization, before any consumer can register. Rooted for the process.
        // Without the mission notification the window closes at once (fail closed).
        internal static void Initialize(ManualLogSource log)
        {
            if (subscription != null || !IsOpen) return;
            try
            {
                subscription = Internal.MissionEvents.Initialization.Subscribe(_ => Close());
                NativeApiLog.Info(log, "Native inline patch window open until the first mission initialization.");
            }
            catch (Exception error)
            {
                Close();
                NativeApiLog.Error(log, "Native inline patch window closed; on-demand inline hooks stay unavailable: " + error.Message);
            }
        }

        internal static void Close() => Volatile.Write(ref closed, 1);

        // Test-only reset of the once-per-process state.
        internal static void ResetForTest() => Volatile.Write(ref closed, 0);
    }
}
