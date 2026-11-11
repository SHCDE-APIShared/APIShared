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
        // Only a running or successful initialization is shared: a failed attempt (for example
        // its own time budget during a busy game start) is retried by the next caller.
        private static async Task<ProtonExecutionContext> LinuxContext(string game, TimeSpan budget)
        {
            if (budget <= TimeSpan.Zero) throw new TimeoutException("Proton path initialization exceeded its time budget.");
            Task<ProtonExecutionContext> pending;
            lock (protonGate) {
                if (protonContext == null || protonContext.IsFaulted || protonContext.IsCanceled) {
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
        internal static async Task Launch(string transaction, string game, WorkshopInstallPlan manualPlan = null)
        {
            string assets = Path.Combine(Path.GetDirectoryName(typeof(WorkshopUpdaterLauncher).Assembly.Location), "Workshop");
            string hash = WorkshopInventory.Hash(WorkshopPaths.Inside(transaction, "operations.tsv"));
            string repairHash = File.Exists(WorkshopPaths.Inside(transaction, "supersedes.tsv")) ? WorkshopInventory.Hash(WorkshopPaths.Inside(transaction, "supersedes.tsv")) : "";
            string ready = WorkshopPaths.Inside(transaction, "ready.flag");
            string sourceManifest = manualPlan == null ? null : WorkshopPaths.Inside(manualPlan.Source, WorkshopPackage.ManifestFileName);
            if (WinePathInterop.Instance.IsWine)
            {
                var context = await LinuxContext(game, TimeSpan.FromSeconds(2));
                context.ValidateGameIdentity(TimeSpan.FromSeconds(2));
                string script = WorkshopPaths.Inside(assets, "workshop-updater.sh");
                if (!File.Exists(script)) throw new FileNotFoundException("Proton updater script is missing.", script);
                string[] arguments = manualPlan == null
                    ? new[] { context.Paths.ToHostExisting(game), context.Paths.ToHostExisting(transaction), hash, "--restart", repairHash }
                    : new[] { context.Paths.ToHostExisting(game), context.Paths.ToHostExisting(transaction), hash, "--manual", repairHash,
                        context.Paths.ToHostExisting(sourceManifest), manualPlan.ManifestHash };
                if (File.Exists(ready)) throw new InvalidOperationException("This transaction already has an updater handoff.");
                // No process handle exists under Wine (see HostRun): the exit record replaces HasExited.
                using (var run = context.StartScript(context.Paths.ToHostExisting(script), arguments))
                    await AwaitReadiness(transaction, ready, hash, () => run.TryGetExit(out int code) ? code : (int?)null);
                return;
            }
            string windowsScript = WorkshopPaths.Inside(assets, "workshop-updater.ps1");
            if (!File.Exists(windowsScript)) throw new FileNotFoundException("Windows updater script is missing.", windowsScript);
            string launchOptions = manualPlan == null ? " -RestartSteam" :
                " -RequireRunningGame -SourceManifest " + Quote(sourceManifest) + " -SourceManifestHash " + manualPlan.ManifestHash;
            var start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(windowsScript) + " -GameRoot " + Quote(game) + " -Transaction " + Quote(transaction) + " -OperationsHash " + hash + launchOptions + (repairHash.Length == 0 ? "" : " -SupersedesHash " + repairHash));
            start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
            if (File.Exists(ready)) throw new InvalidOperationException("This transaction already has an updater handoff.");
            using (var process = Process.Start(start))
            {
                if (process == null) throw new IOException("Updater process could not be started.");
                await AwaitReadiness(transaction, ready, hash, () => process.HasExited ? process.ExitCode : (int?)null);
            }
        }
        // exitCode returns null while the updater still runs. A child can publish readiness
        // and fail before this poll; its leftover hash must not authorize closing the game.
        private static async Task AwaitReadiness(string transaction, string ready, string hash, Func<int?> exitCode)
        {
            var watch = Stopwatch.StartNew();
            IOException readFailure = null;
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                bool confirmed = WorkshopUpdaterReadiness.TryConfirm(ready, hash, out readFailure);
                int? exited = exitCode();
                if (exited.HasValue) throw new IOException("Updater rejected the transaction before game exit; inspect " + transaction + "/journal.log. Exit code: " + exited.Value, readFailure);
                if (confirmed) return;
                await Task.Delay(100);
            }
            Cancel(transaction);
            throw new IOException("Updater readiness was not confirmed; the game remains running. Inspect " + transaction + " before retrying.", readFailure);
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
            string script = context.Paths.ToHostExisting(WorkshopPaths.Inside(assets, "workshop-updater.sh"));
            string[] arguments = { context.Paths.ToHostExisting(game), "--status" };
            var remaining = TimeSpan.FromMilliseconds(2000) - budget.Elapsed;
            if (remaining <= TimeSpan.Zero) return new WorkshopUpdaterActivity { Busy = true };
            string text;
            // A slow or failed query defers startup work; an unfinished query keeps running
            // detached and only reads the lock (see HostRun).
            try { text = (await Task.Run(() => context.RunScript(script, arguments, remaining))).Trim(); }
            catch (TimeoutException) { return new WorkshopUpdaterActivity { Busy = true }; }
            catch (IOException) { return new WorkshopUpdaterActivity { Busy = true }; }
            if (text == "idle") return new WorkshopUpdaterActivity();
            // The host query has validated flock, /proc identity and pins.
            return WorkshopUpdaterActivity.Parse(text, "linux", (pid, stamp) => true);
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
                var result = WinePathInterop.Instance.IsWine ? await MaintainOnHost(game) :
                    await WorkshopMaintenanceRunner.RunAsync(WorkshopTransaction.StateGate, () => MaintenanceNeeded(game), () => MaintenanceStart(game),
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
            var start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(WorkshopPaths.Inside(assets, "workshop-updater.ps1")) + " -GameRoot " + Quote(game) + " -Maintain");
            start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
            return start;
        }
        // Linux counterpart of WorkshopMaintenanceRunner.RunAsync. Without a process handle
        // (see HostRun) the pass cannot be killed: after the timeout the state gate stays
        // owned until the pass publishes its exit record, and the caller defers recovery
        // and staging exactly as for a child that survived its kill. The pass itself also
        // holds the host apply.lock.
        private static async Task<WorkshopMaintenanceResult> MaintainOnHost(string game)
        {
            var gate = WorkshopTransaction.StateGate;
            if (!await gate.WaitAsync(WorkshopMaintenanceRunner.GateWait))
                return new WorkshopMaintenanceResult { Exit = WorkshopMaintenanceExit.StillRunning, Error = "Transaction state is still owned by another operation." };
            bool release = true;
            try {
                if (!MaintenanceNeeded(game)) return new WorkshopMaintenanceResult { Exit = WorkshopMaintenanceExit.Skipped };
                string assets = Path.Combine(Path.GetDirectoryName(typeof(WorkshopUpdaterLauncher).Assembly.Location), "Workshop");
                var context = await LinuxContext(game, TimeSpan.FromSeconds(2));
                var run = context.StartScript(context.Paths.ToHostExisting(WorkshopPaths.Inside(assets, "workshop-updater.sh")),
                    new[] { context.Paths.ToHostExisting(game), "--maintain" });
                var watch = Stopwatch.StartNew();
                int code;
                while (!run.TryGetExit(out code)) {
                    if (watch.Elapsed >= WorkshopMaintenanceRunner.Timeout) {
                        release = false;
                        _ = Task.Run(async () => {
                            try { int ignored; while (!run.TryGetExit(out ignored)) await Task.Delay(1000).ConfigureAwait(false); }
                            catch (IOException) { } // An unreadable exit record still ends the pass.
                            finally { run.Dispose(); gate.Release(); }
                        });
                        return new WorkshopMaintenanceResult { Exit = WorkshopMaintenanceExit.StillRunning,
                            Error = "The Linux pass cannot be stopped without a process handle; it keeps apply.lock until it exits." };
                    }
                    await Task.Delay(100);
                }
                using (run)
                    return new WorkshopMaintenanceResult { Exit = WorkshopMaintenanceExit.Completed, ExitCode = code, Output = run.Output.Trim(), Error = run.Error.Trim() };
            } finally {
                if (release) gate.Release();
            }
        }
    }
}
