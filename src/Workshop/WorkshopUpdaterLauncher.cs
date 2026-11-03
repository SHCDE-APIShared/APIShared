using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using APIShared.Workshop.Proton;

namespace APIShared.Workshop
{
    internal static class WorkshopUpdaterLauncher
    {
        private static readonly object protonGate = new object();
        private static Task<ProtonExecutionContext> protonContext;
        private static string protonGame;
        private static string Quote(string value)
        {
            if (value.Contains("\"") || value.EndsWith("\\", StringComparison.Ordinal)) throw new ArgumentException("Unsupported command-line argument.");
            return "\"" + value + "\"";
        }
        // Initialization is shared across install/status/maintenance and never runs on native
        // Windows. The worker has no Unity access; bounded callers may defer without quitting.
        private static async Task<ProtonExecutionContext> LinuxContext(string game, TimeSpan budget)
        {
            if (budget <= TimeSpan.Zero) throw new TimeoutException("Proton path initialization exceeded its time budget.");
            Task<ProtonExecutionContext> pending;
            lock (protonGate) {
                if (protonContext == null) {
                    protonGame = Path.GetFullPath(game);
                    protonContext = Task.Run(() => {
                        var context = ProtonExecutionContext.Create(game,
                            ProtonConfiguration.Load(BepInEx.Paths.ConfigPath), TimeSpan.FromSeconds(2));
                        return context;
                    });
                }
                if (!string.Equals(protonGame, Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Proton updater context belongs to another game directory.");
                pending = protonContext;
            }
            if (await Task.WhenAny(pending, Task.Delay(budget)).ConfigureAwait(false) != pending)
                throw new TimeoutException("Proton path initialization exceeded its time budget.");
            return await pending.ConfigureAwait(false);
        }
        internal static async Task Launch(string transaction, string game)
        {
            string assets = Path.Combine(Path.GetDirectoryName(typeof(WorkshopUpdaterLauncher).Assembly.Location), "Workshop");
            string hash = WorkshopInventory.Hash(WorkshopPaths.Inside(transaction, "operations.tsv"));
            string repairHash = File.Exists(WorkshopPaths.Inside(transaction, "supersedes.tsv")) ? WorkshopInventory.Hash(WorkshopPaths.Inside(transaction, "supersedes.tsv")) : "";
            bool wine = WinePathInterop.Instance.IsWine;
            ProcessStartInfo start;
            if (wine)
            {
                var context = await LinuxContext(game, TimeSpan.FromSeconds(2));
                context.ValidateGameIdentity(TimeSpan.FromSeconds(2));
                string script = WorkshopPaths.Inside(assets, "workshop-updater.sh");
                if (!File.Exists(script)) throw new FileNotFoundException("Proton updater script is missing.", script);
                start = context.CreateStartInfo(context.Paths.ToHostExisting(script), new[] {
                    context.Paths.ToHostExisting(game), context.Paths.ToHostExisting(transaction), hash, "--restart", repairHash });
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
                IOException readFailure = null;
                while (watch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    bool confirmed = WorkshopUpdaterReadiness.TryConfirm(ready, hash, out readFailure);
                    // A child can publish readiness and fail before this poll.
                    // Its leftover hash must not authorize closing the game.
                    if (process.HasExited) throw new IOException("Updater rejected the transaction before game exit; inspect " + transaction + "/journal.log. Exit code: " + process.ExitCode, readFailure);
                    if (confirmed) return;
                    await Task.Delay(100);
                }
                Cancel(transaction);
                throw new IOException("Updater readiness was not confirmed; the game remains running. Inspect " + transaction + " before retrying.", readFailure);
            }
        }
        internal static void Cancel(string transaction) => File.WriteAllText(WorkshopPaths.Inside(transaction, "cancel.flag"), "Cancelled before game exit.");
        internal static async Task<WorkshopUpdaterActivity> Activity(string game)
        {
            bool wine = WinePathInterop.Instance.IsWine;
            if (!wine) {
                var read = Task.Run(() => WorkshopUpdaterActivity.ReadWindows(game));
                if (await Task.WhenAny(read, Task.Delay(2000)) != read) return new WorkshopUpdaterActivity { Busy = true };
                return await read;
            }
            var budget = Stopwatch.StartNew();
            string state = WorkshopInstallation.StateRoot(game);
            if (!File.Exists(WorkshopPaths.Inside(state, "apply.lock"))) return new WorkshopUpdaterActivity();
            string assets = Path.Combine(Path.GetDirectoryName(typeof(WorkshopUpdaterLauncher).Assembly.Location), "Workshop");
            ProtonExecutionContext context;
            try { context = await LinuxContext(game, TimeSpan.FromMilliseconds(2000) - budget.Elapsed); }
            catch (TimeoutException) { return new WorkshopUpdaterActivity { Busy = true }; }
            var start = context.CreateStartInfo(context.Paths.ToHostExisting(WorkshopPaths.Inside(assets, "workshop-updater.sh")),
                new[] { context.Paths.ToHostExisting(game), "--status" });
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using (var process = Process.Start(start)) {
                if (process == null) return new WorkshopUpdaterActivity { Busy = true };
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                int remaining = Math.Max(0, 2000 - (int)budget.ElapsedMilliseconds);
                var exited = Task.Run(() => process.WaitForExit(remaining));
                if (!await exited) {
                    try { process.Kill(); } catch { }
                    CloseStatusReaders(process);
                    return new WorkshopUpdaterActivity { Busy = true };
                }
                var streams = Task.WhenAll(output, error);
                remaining = Math.Max(0, 2000 - (int)budget.ElapsedMilliseconds);
                if (await Task.WhenAny(streams, Task.Delay(remaining)) != streams) {
                    CloseStatusReaders(process);
                    return new WorkshopUpdaterActivity { Busy = true };
                }
                string text = (await output).Trim(); await error;
                if (process.ExitCode != 0) return new WorkshopUpdaterActivity { Busy = true };
                if (text == "idle") return new WorkshopUpdaterActivity();
                // The host query has validated flock, /proc identity and pins.
                return WorkshopUpdaterActivity.Parse(text, "linux", (pid, stamp) => true);
            }
        }
        private static void CloseStatusReaders(Process process)
        {
            // A descendant retaining a pipe must not retain our asynchronous readers
            // after the bounded status query has already deferred startup work.
            try { process.StandardOutput.Close(); } catch { }
            try { process.StandardError.Close(); } catch { }
        }
        // Startup history maintenance. The read-only plan check decides whether the
        // external pass could change anything; only then a hidden child runs, bounded
        // by WorkshopMaintenanceRunner.Timeout. Returns false when a child survived
        // its kill and still owns the transaction directory: recovery and staging
        // must then wait for a later start.
        internal static async Task<bool> Maintain(string game)
        {
            if (WorkshopRuntime.IsExternallyManaged) return true;
            try {
                var result = await WorkshopMaintenanceRunner.RunAsync(WorkshopTransaction.StateGate, () => MaintenanceNeeded(game), () => MaintenanceStart(game),
                    WorkshopMaintenanceRunner.Timeout, WorkshopMaintenanceRunner.KillWait);
                switch (result.Exit) {
                    case WorkshopMaintenanceExit.Skipped: WorkshopRuntime.Log("Workshop maintenance: nothing to do; no process started."); return true;
                    case WorkshopMaintenanceExit.StillRunning:
                        WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop maintenance did not exit after its timeout and kill; recovery and installation are deferred until it has exited. " + result.Error);
                        return false;
                    case WorkshopMaintenanceExit.Stopped:
                        WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop maintenance exceeded " + WorkshopMaintenanceRunner.Timeout.TotalSeconds + " s and was stopped; its resumable cleanup continues on a later start or after the next installation.");
                        return true;
                }
                if (result.Output.Length != 0) WorkshopRuntime.Log(result.Output);
                if (result.ExitCode != 0) WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop maintenance deferred; " + result.Error);
            } catch (Exception ex) {
                WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop maintenance deferred; " + ex.Message);
            }
            return true;
        }
        private static bool MaintenanceNeeded(string game)
        {
            try { return WorkshopMaintenancePlan.MayChange(game); }
            catch (Exception ex) {
                WorkshopRuntime.Log("Workshop maintenance pre-check inconclusive; running the external pass: " + ex.Message);
                return true;
            }
        }
        private static ProcessStartInfo MaintenanceStart(string game)
        {
            string assets = Path.Combine(Path.GetDirectoryName(typeof(WorkshopUpdaterLauncher).Assembly.Location), "Workshop");
            bool wine = WinePathInterop.Instance.IsWine;
            ProcessStartInfo start;
            if (wine) {
                var context = LinuxContext(game, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                start = context.CreateStartInfo(context.Paths.ToHostExisting(WorkshopPaths.Inside(assets, "workshop-updater.sh")),
                    new[] { context.Paths.ToHostExisting(game), "--maintain" });
            } else start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(WorkshopPaths.Inside(assets, "workshop-updater.ps1")) + " -GameRoot " + Quote(game) + " -Maintain");
            start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
            return start;
        }
    }
}
