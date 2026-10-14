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
            string repairHash = File.Exists(WorkshopPaths.Inside(transaction, "supersedes.tsv")) ? WorkshopInventory.Hash(WorkshopPaths.Inside(transaction, "supersedes.tsv")) : "";
            bool wine = GetProcAddress(GetModuleHandle("ntdll.dll"), "wine_get_version") != IntPtr.Zero;
            ProcessStartInfo start;
            if (wine)
            {
                string shell = new[] { @"Z:\bin\bash", @"Z:\usr\bin\bash" }.FirstExisting();
                string script = WorkshopPaths.Inside(assets, "workshop-updater.sh");
                if (!File.Exists(script)) throw new FileNotFoundException("Proton updater script is missing.", script);
                start = new ProcessStartInfo(shell, Quote(HostPath(script)) + " " + Quote(HostPath(game)) + " " + Quote(HostPath(transaction)) + " " + hash + " --restart " + Quote(repairHash));
            }
            else
            {
                string script = WorkshopPaths.Inside(assets, "workshop-updater.ps1");
                if (!File.Exists(script)) throw new FileNotFoundException("Windows updater script is missing.", script);
                start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(script) + " -GameRoot " + Quote(game) + " -Transaction " + Quote(transaction) + " -OperationsHash " + hash + " -RestartSteam" + (repairHash.Length == 0 ? "" : " -SupersedesHash " + repairHash));
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
                    if (process.HasExited) throw new IOException("Updater rejected the transaction before game exit; inspect " + transaction + "/journal.log. Exit code: " + process.ExitCode);
                    await Task.Delay(100);
                }
                Cancel(transaction);
                throw new IOException("Updater readiness was not confirmed; the game remains running. Inspect " + transaction + " before retrying.");
            }
        }
        internal static void Cancel(string transaction) => File.WriteAllText(WorkshopPaths.Inside(transaction, "cancel.flag"), "Cancelled before game exit.");
        internal static async Task<WorkshopUpdaterActivity> Activity(string game)
        {
            bool wine = GetProcAddress(GetModuleHandle("ntdll.dll"), "wine_get_version") != IntPtr.Zero;
            if (!wine) {
                var read = Task.Run(() => WorkshopUpdaterActivity.ReadWindows(game));
                if (await Task.WhenAny(read, Task.Delay(2000)) != read) return new WorkshopUpdaterActivity { Busy = true };
                return await read;
            }
            string state = WorkshopInstallation.StateRoot(game);
            if (!File.Exists(WorkshopPaths.Inside(state, "apply.lock"))) return new WorkshopUpdaterActivity();
            string assets = Path.Combine(Path.GetDirectoryName(typeof(WorkshopUpdaterLauncher).Assembly.Location), "Workshop");
            string shell = new[] { @"Z:\bin\bash", @"Z:\usr\bin\bash" }.FirstExisting();
            var start = new ProcessStartInfo(shell, Quote(HostPath(WorkshopPaths.Inside(assets, "workshop-updater.sh"))) + " " + Quote(HostPath(game)) + " --status") {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(start)) {
                if (process == null) return new WorkshopUpdaterActivity { Busy = true };
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                var exited = Task.Run(() => process.WaitForExit(1750));
                if (!await exited) {
                    try { process.Kill(); process.WaitForExit(150); } catch { }
                    return new WorkshopUpdaterActivity { Busy = true };
                }
                string text = (await output).Trim(); await error;
                if (process.ExitCode != 0) return new WorkshopUpdaterActivity { Busy = true };
                if (text == "idle") return new WorkshopUpdaterActivity();
                // The host query has validated flock, /proc identity and pins.
                return WorkshopUpdaterActivity.Parse(text, "linux", (pid, stamp) => true);
            }
        }
        internal static async Task Maintain(string game)
        {
            if (WorkshopRuntime.IsExternallyManaged) return;
            await WorkshopTransaction.StateGate.WaitAsync();
            try {
                string assets = Path.Combine(Path.GetDirectoryName(typeof(WorkshopUpdaterLauncher).Assembly.Location), "Workshop");
                bool wine = GetProcAddress(GetModuleHandle("ntdll.dll"), "wine_get_version") != IntPtr.Zero;
                ProcessStartInfo start;
                if (wine) {
                    string shell = new[] { @"Z:\bin\bash", @"Z:\usr\bin\bash" }.FirstExisting();
                    start = new ProcessStartInfo(shell, Quote(HostPath(WorkshopPaths.Inside(assets, "workshop-updater.sh"))) + " " + Quote(HostPath(game)) + " --maintain");
                } else start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(WorkshopPaths.Inside(assets, "workshop-updater.ps1")) + " -GameRoot " + Quote(game) + " -Maintain");
                start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
                start.RedirectStandardOutput = true; start.RedirectStandardError = true;
                using (var process = Process.Start(start)) {
                    if (process == null) throw new IOException("Maintenance process could not be started.");
                    var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                    await Task.Run(() => process.WaitForExit());
                    string text = (await output).Trim(), error = (await errors).Trim();
                    if (text.Length != 0) WorkshopRuntime.Log(text);
                    if (process.ExitCode != 0) WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop maintenance deferred; " + error);
                }
            } catch (Exception ex) {
                WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop maintenance deferred; " + ex.Message);
            } finally { WorkshopTransaction.StateGate.Release(); }
        }
        private static string FirstExisting(this string[] paths)
        {
            foreach (string path in paths) if (File.Exists(path)) return path;
            throw new FileNotFoundException("Host bash was not found through Wine's Z: drive.");
        }
    }
}
