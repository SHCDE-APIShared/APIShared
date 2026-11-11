using BepInEx.Logging;

namespace APIShared
{
    // The one logging entry point for APIShared code. Lines get a millisecond timestamp; warnings
    // and errors go through RuntimeHealth (tagged, deduplicated). Test projects that compile
    // production sources replace this class with their own double.
    internal static class NativeApiLog
    {
        public static void Debug(ManualLogSource log, string message) => APIShared.Internal.DebugLogHelper.LogDebug(log, message);
        public static void Info(ManualLogSource log, string message) => APIShared.Internal.DebugLogHelper.LogInfo(log, message);
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Warning(ManualLogSource log, string message) => APIShared.Internal.RuntimeHealth.Failure(log, APIShared.Internal.RuntimeHealth.CallerFeature(), false, message);
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Error(ManualLogSource log, string message) => APIShared.Internal.RuntimeHealth.Failure(log, APIShared.Internal.RuntimeHealth.CallerFeature(), true, message);
    }
}
