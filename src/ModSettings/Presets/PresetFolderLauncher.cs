using APIShared.Workshop.Proton;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace APIShared.ModSettings
{
    internal static class PresetFolderLauncher
    {
        private static readonly TimeSpan StartupObservation = TimeSpan.FromSeconds(10);

        internal static Task OpenAsync(string directory)
        {
            var wine = WinePathInterop.Instance;
            if (wine.IsWine)
                return Task.Run(() => OpenThroughWine(wine, directory));
            if (Environment.OSVersion.Platform == PlatformID.Unix)
                return Task.Run(() => OpenOnLinux(directory));

            // The Windows shell can reuse an existing Explorer process.
            using (Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })) { }
            return Task.CompletedTask;
        }

        private static void OpenThroughWine(IWinePathApi wine, string directory)
        {
            if (!wine.TryToUnix(directory, out string hostDirectory))
                throw new IOException("Wine could not translate the personal preset directory: " + directory);

            string bash = ProtonExecutionContext.FindHostBash(wine, null);
            using (var run = HostRun.Start(wine, bash,
                new[] { "-c", "exec xdg-open \"$1\"", "apishared-open-preset-folder", ProtonPaths.HostAbsolute(hostDirectory) }))
                ObserveHostLaunch(run, StartupObservation);
        }

        internal static void ObserveHostLaunch(HostRun run, TimeSpan observation)
        {
            try { run.Wait(observation); }
            catch (TimeoutException)
            {
                // xdg-open may run until the file manager closes. Leave the owned run for HostRun to prune.
            }
        }

        private static void OpenOnLinux(string directory)
        {
            var start = new ProcessStartInfo("xdg-open", ProtonProcess.Quote(directory))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var process = Process.Start(start))
            {
                if (process == null)
                    throw new IOException("The Linux desktop opener xdg-open did not start.");
                if (process.WaitForExit((int)StartupObservation.TotalMilliseconds) && process.ExitCode != 0)
                    throw new IOException("The Linux desktop opener xdg-open failed (exit " + process.ExitCode + ").");
            }
        }
    }
}
