using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace APIShared.Workshop
{
    internal static class WorkshopUpdaterLauncher
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        private static string Quote(string value)
        {
            if (value.Contains("\"") || value.EndsWith("\\", StringComparison.Ordinal)) throw new ArgumentException("Unsupported command-line argument.");
            return "\"" + value + "\"";
        }
        private static string HostPath(string path)
        {
            if (!path.StartsWith(@"Z:\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Proton updater requires game and transaction paths on Wine's Z: drive.");
            return "/" + path.Substring(3).Replace('\\', '/');
        }
        internal static async Task Launch(string transaction, string game)
        {
            string assets = Path.Combine(Path.GetDirectoryName(typeof(WorkshopUpdaterLauncher).Assembly.Location), "Workshop");
            string hash = WorkshopInventory.Hash(WorkshopPaths.Inside(transaction, "operations.tsv"));
            bool wine = GetProcAddress(GetModuleHandle("ntdll.dll"), "wine_get_version") != IntPtr.Zero;
            ProcessStartInfo start;
            if (wine)
            {
                string shell = new[] { @"Z:\bin\bash", @"Z:\usr\bin\bash" }.FirstExisting();
                string script = WorkshopPaths.Inside(assets, "workshop-updater.sh");
                if (!File.Exists(script)) throw new FileNotFoundException("Proton updater script is missing.", script);
                start = new ProcessStartInfo(shell, Quote(HostPath(script)) + " " + Quote(HostPath(game)) + " " + Quote(HostPath(transaction)) + " " + hash + " --restart");
            }
            else
            {
                string script = WorkshopPaths.Inside(assets, "workshop-updater.ps1");
                if (!File.Exists(script)) throw new FileNotFoundException("Windows updater script is missing.", script);
                start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(script) + " -GameRoot " + Quote(game) + " -Transaction " + Quote(transaction) + " -OperationsHash " + hash + " -RestartSteam");
            }
            start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
            string ready = WorkshopPaths.Inside(transaction, "ready.flag");
            if (File.Exists(ready)) throw new InvalidOperationException("This transaction already has an updater handoff.");
            using (var process = Process.Start(start))
            {
                if (process == null) throw new IOException("Updater process could not be started.");
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    if (File.Exists(ready) && File.ReadAllText(ready) == hash) return;
                    if (process.HasExited) throw new IOException("Updater rejected the transaction before game exit; inspect its journal. Exit code: " + process.ExitCode);
                    await Task.Delay(100);
                }
                Cancel(transaction);
                throw new IOException("Updater readiness was not confirmed; the game remains running. Inspect the transaction before retrying.");
            }
        }
        internal static void Cancel(string transaction) => File.WriteAllText(WorkshopPaths.Inside(transaction, "cancel.flag"), "Cancelled before game exit.");
        private static string FirstExisting(this string[] paths)
        {
            foreach (string path in paths) if (File.Exists(path)) return path;
            throw new FileNotFoundException("Host bash was not found through Wine's Z: drive.");
        }
    }
}
