using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace APIShared
{
    internal static class NativeApiLog
    {
        public static void Debug(ManualLogSource log, string message) => APIShared.Internal.DebugLogHelper.LogDebug(log, message);
        public static void Info(ManualLogSource log, string message) => log?.LogInfo(Stamp(message));
        public static void Warning(ManualLogSource log, string message) => log?.LogWarning(Stamp(message));
        public static void Error(ManualLogSource log, string message) => log?.LogError(Stamp(message));
        private static string Stamp(string message) => $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
    }
}
