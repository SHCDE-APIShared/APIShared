using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace APIShared
{
    internal static class NativeApiLog
    {
        public static void Debug(ManualLogSource log, string message) => APIShared.Internal.DebugLogHelper.LogDebug(log, message);
        // API_SHARED_DIAGNOSTICS: includes consumer-owned logger sources.
        public static void Info(ManualLogSource log, string message) => APIShared.Internal.DebugLogHelper.LogInfo(log, message);
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Warning(ManualLogSource log, string message) => APIShared.Internal.RuntimeHealth.Failure(log, APIShared.Internal.RuntimeHealth.CallerFeature(), false, message);
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Error(ManualLogSource log, string message) => APIShared.Internal.RuntimeHealth.Failure(log, APIShared.Internal.RuntimeHealth.CallerFeature(), true, message);    }
}
