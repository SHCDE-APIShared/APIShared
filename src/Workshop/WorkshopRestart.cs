using APIShared.Workshop.Install;
using APIShared.Workshop.Proton;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace APIShared.Workshop
{
    // The Steam startup gate owns consent and requests Unity shutdown after this handoff.
    // Only the next preloader may apply the batch. The helper only observes processes and
    // records its own status, so killing it cannot interrupt a deployment.
    internal static class WorkshopRestart
    {
        private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(20);
        private static readonly string[] StatusFiles = { "ready", "armed", "claimed", "result", "ready.tmp", "armed.tmp", "claimed.tmp", "result.tmp" };

        internal static Task RequestAsync(string gameRoot, string batchId) =>
            Task.Run(() => Request(gameRoot, batchId));

        private static void Request(string gameRoot, string batchId)
        {
            if (!Guid.TryParseExact(batchId, "N", out var id) || id.ToString("N") != batchId)
                throw new ArgumentException("Invalid Workshop restart batch identifier.", nameof(batchId));
            string directory = WorkshopPaths.Inside(gameRoot, "_APIShared/I/restart/" + batchId);
            if (Directory.Exists(directory)) throw new IOException("A restart helper already owns this Workshop batch.");
            Directory.CreateDirectory(directory);
            string assets = Path.Combine(Path.GetDirectoryName(typeof(WorkshopRestart).Assembly.Location), "Workshop");
            using (var self = Process.GetCurrentProcess())
            {
                string executable = self.MainModule.FileName;
                if (WinePathInterop.Instance.IsWine)
                {
                    string script = WorkshopPaths.Inside(assets, "workshop-restart.sh");
                    var context = ProtonExecutionContext.Create(gameRoot, new ProtonPathOptions(), TimeSpan.FromSeconds(5));
                    string[] arguments = { "--handoff", context.Paths.ToHostExisting(directory), context.Paths.ToHostExisting(executable),
                        executable, batchId, Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH") ?? "" };
                    using (var run = context.StartScript(context.Paths.ToHostExisting(script), arguments))
                    {
                        // The host submitter exits after the user service accepts the job.
                        // The independent waiter still has to identify the game and report ready.
                        run.Wait(ReadinessTimeout);
                        AwaitReady(directory, batchId, () => null);
                    }
                }
                else
                {
                    string script = WorkshopPaths.Inside(assets, "workshop-restart.ps1");
                    if (!File.Exists(script)) throw new FileNotFoundException("Workshop restart helper is missing.", script);
                    string interpreter = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                    string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + ProtonProcess.Quote(script) +
                        " -Handoff -Directory " + ProtonProcess.Quote(directory) + " -GameExe " + ProtonProcess.Quote(executable) +
                        " -GamePid " + self.Id.ToString(CultureInfo.InvariantCulture) +
                        " -GameStart " + self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + " -Batch " + batchId;
                    var start = new ProcessStartInfo(interpreter, arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                    using (var child = Process.Start(start))
                    {
                        if (child == null) throw new IOException("Workshop restart helper could not be started.");
                        AwaitReady(directory, batchId, () => child.HasExited && child.ExitCode != 0 ? child.ExitCode.ToString(CultureInfo.InvariantCulture) : null);
                    }
                }
            }

            // A helper that saw the game exit before the caller reached this point must
            // leave the staged batch for the next manual launch.
            string armed = Path.Combine(directory, "armed");
            WorkshopPaths.CheckLinks(armed);
            WorkshopPaths.CheckLinks(armed + ".tmp");
            InstallTree.WriteDurable(armed + ".tmp", batchId);
            File.Move(armed + ".tmp", armed);
        }

        private static void AwaitReady(string directory, string batchId, Func<string> exited)
        {
            string ready = Path.Combine(directory, "ready");
            string result = Path.Combine(directory, "result");
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < ReadinessTimeout)
            {
                if (File.Exists(result)) throw new IOException(File.ReadAllText(result).Trim());
                string exitCode = exited();
                if (exitCode != null) throw new IOException("Workshop restart helper exited before handoff (" + exitCode + ").");
                if (File.Exists(ready) && File.ReadAllText(ready).Trim() == batchId) return;
                Thread.Sleep(50);
            }
            throw new TimeoutException("Workshop restart helper did not confirm readiness.");
        }

        internal static void ReportPrevious(string gameRoot)
        {
            string directory = WorkshopPaths.Inside(gameRoot, "_APIShared/I/restart");
            if (!Directory.Exists(directory)) return;
            foreach (string run in Directory.EnumerateDirectories(directory))
            {
                string id = Path.GetFileName(run);
                if (!Guid.TryParseExact(id, "N", out _) || (File.GetAttributes(run) & FileAttributes.ReparsePoint) != 0) continue;
                string result = Path.Combine(run, "result");
                // A terminal result ends all status writes by the helper. Keep any inactive
                // pre-readiness attempt for a day, and only remove our own known filenames.
                if (File.Exists(result))
                {
                    WorkshopPaths.CheckLinks(result);
                    string status = File.ReadAllText(result).Trim();
                    if (status.StartsWith("failed", StringComparison.Ordinal))
                        WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop restart " + id + ": " + status);
                    else WorkshopRuntime.Log("Workshop restart " + id + ": " + status);
                }
                else if (Directory.GetLastWriteTimeUtc(run) > DateTime.UtcNow.AddDays(-1)) continue;
                foreach (string name in StatusFiles)
                {
                    string path = Path.Combine(run, name);
                    WorkshopPaths.CheckLinks(path);
                    File.Delete(path);
                }
                if (Directory.GetFileSystemEntries(run).Length == 0) Directory.Delete(run);
            }
        }
    }
}
