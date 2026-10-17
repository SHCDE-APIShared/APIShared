using System;
using System.Globalization;
using BepInEx.Logging;

namespace APIShared.DependencyBootstrap
{
    // Diagnostics must never affect loader behavior. No separate files or startup traces.
    internal static class ProbeTrace
    {
        private static ManualLogSource log;
        internal static void Initialize()
        {
            try { if (log == null) log = Logger.CreateLogSource("APIShared.DependencyBootstrap"); }
            catch { }
        }
        internal static void Warning(string detail) => Write(LogLevel.Warning, detail);
        internal static void Error(string detail) => Write(LogLevel.Error, detail);
        private static void Write(LogLevel level, string detail)
        {
            try
            {
                Initialize();
                log?.Log(level, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + detail);
            }
            catch { }
        }
    }
}
