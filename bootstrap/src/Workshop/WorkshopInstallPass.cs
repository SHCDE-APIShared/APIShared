using System;
using System.Globalization;
using BepInEx.Logging;
using APIShared.Workshop.Install;

namespace APIShared.DependencyBootstrap
{
    // Preloader step of the safe Workshop installation (src/Workshop/Install): recovers
    // interrupted swaps and applies staged requests while nothing in BepInEx/plugins is open.
    // Silent when no installation work exists. Installation outcomes are logged on their own
    // source because they are user-relevant events, not startup tracing.
    internal static class WorkshopInstallPass
    {
        private static ManualLogSource log;

        internal static void Run(string game)
        {
            // Tells the APIShared plugin that staged changes are applied at the next start.
            try { AppDomain.CurrentDomain.SetData(InstallRunner.ActiveKey, true); } catch { }
            try
            {
                // An external mod manager's policy wins over changes staged earlier (src/Workshop/WorkshopManagerPolicy.cs).
                var policy = APIShared.Workshop.WorkshopManagerPolicy.Read(game);
                if (policy.Error != null) Write(LogLevel.Error, policy.Error);
                InstallRunner.Run(game, message => Write(LogLevel.Info, message), message => Write(LogLevel.Error, message), policy.Manages);
            }
            catch (Exception ex) { Write(LogLevel.Error, "Workshop installation pass failed: " + ex.Message); }
        }

        private static void Write(LogLevel level, string message)
        {
            try
            {
                if (log == null) log = Logger.CreateLogSource("APIShared.WorkshopInstall");
                log.Log(level, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message);
            }
            catch { }
        }
    }
}
