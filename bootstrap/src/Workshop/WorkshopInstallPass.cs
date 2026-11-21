using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
        // Keep the lease after Finish so a second process cannot replace files of this session.
        private static FileStream gameLease;

        internal static void Run(string game, bool enabled)
        {
            try
            {
                var paths = new InstallPaths(game);
                string leasePath = Path.Combine(paths.Work, "session.lock");
                bool recovery;
                bool hasRuntime;
                try
                {
                    recovery = InstallRunner.RequiresRecovery(game);
                    hasRuntime = PairContract.HasRuntimeDirectory(game);
                    InstallTree.CheckUnlinked(leasePath);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
                {
                    // Unsupported install paths used to allow play with Workshop inactive.
                    // Inspect only journal names through the alias. If the game root was
                    // reached through a link, take the same lease as its canonical path.
                    if (InstallRunner.RequiresRecovery(game, permitLinkedInspection: true)) throw;
                    if (HasUnlinkedLeaseStorage(paths, leasePath)) TakeLease(paths, leasePath);
                    if (InstallRunner.RequiresRecovery(game, permitLinkedInspection: true)) throw;
                    Write(LogLevel.Warning, "Workshop installation is inactive in this game path: " + ex.Message);
                    return;
                }
                if (!hasRuntime && !recovery) return;
                TakeLease(paths, leasePath);

                var policy = APIShared.Workshop.WorkshopManagerPolicy.Read(game);
                if (policy.Error != null) Write(LogLevel.Error, policy.Error);
                bool active = enabled && hasRuntime;
                bool allowPending = active && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCDEModManagerLaunchId")) &&
                    !Directory.Exists(Path.Combine(game, "_SE", ".staging"));
                if (!InstallRunner.Run(game, message => Write(LogLevel.Info, message), message => Write(LogLevel.Error, message), policy.Manages, allowPending))
                    Stop("Workshop recovery is incomplete. See the APIShared.WorkshopInstall entries in BepInEx/LogOutput.log.");
                if (active) AppDomain.CurrentDomain.SetData(InstallRunner.ActiveKey, true);
            }
            catch (Exception ex) { Stop("Workshop could not secure this game startup. Close any other instance of the game before trying again.\n\n" + ex.Message); }
        }

        private static void TakeLease(InstallPaths paths, string path)
        {
            if (gameLease != null) return;
            Directory.CreateDirectory(paths.Work);
            gameLease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private static bool HasUnlinkedLeaseStorage(InstallPaths paths, string leasePath)
        {
            string game = paths.Game.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (string part = leasePath; !string.Equals(part, game, StringComparison.OrdinalIgnoreCase); part = Path.GetDirectoryName(part))
            {
                try { if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return true;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr window, string text, string caption, uint flags);

        private static void Stop(string reason)
        {
            Write(LogLevel.Fatal, reason);
            try { MessageBoxW(IntPtr.Zero, reason, "APIShared Workshop", 0x10010); }
            finally { Environment.Exit(1); }
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
