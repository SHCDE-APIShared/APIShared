using APIShared.Workshop.Install;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace APIShared.Workshop
{
    // Steam can initialize more than once. One process-owned attempt prevents repeated prompts;
    // deferred or declined work is rediscovered on the next start. Installation itself happens
    // in APIShared's preloader after the confirmed restart (src/Workshop/Install).
    internal static class WorkshopStartup
    {
        private static int attempted;
        internal static void Run() => WorkshopStartupGate.Run(RunAsync);
        private static async Task RunAsync()
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            bool blocked = false;
            int failures = 0;
            string result = "failed", step = "startup conditions";
            try {
                step = "installation result";
                try { WorkshopRestart.ReportPrevious(BepInEx.Paths.GameRootPath); }
                catch (Exception ex) {
                    WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Could not read the previous Workshop restart status: " + ex.Message);
                }
                failures += ReportPreloaderResult();
                // A failed install was already reported. Avoid re-offering and restarting on
                // the same launch that just recovered or rejected the confirmed changes.
                if (failures != 0) return;
                step = "startup conditions";
                if (WorkshopRuntime.IsExternallyManaged) {
                    blocked = true; result = "blocked";
                    WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop step=startup conditions; Managed by external mod manager");
                    return;
                }
                if (!WorkshopRuntime.InstallerActive) {
                    // No fallback installer: offers are skipped and Mod Options explains why.
                    blocked = true; result = "blocked";
                    WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop step=startup conditions; " + APIShared.Localization.UiText.Get("Workshop.InstallerInactive"));
                    return;
                }
                var policy = WorkshopManagerPolicy.Read(BepInEx.Paths.GameRootPath);
                if (policy.Error != null) WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Error, "Workshop step=external mod manager; " + policy.Error + " Every package is treated as managed until the file is fixed.");
                step = "former updater state";
                string game = BepInEx.Paths.GameRootPath;
                var repairs = WorkshopLocalState.ReadRepairs(game);
                InspectFormerUpdaterState(game, repairs);
                step = "startup conditions";
                int repairCount = repairs.Count;
                var verifiedRepairs = new HashSet<ulong>();
                try { WorkshopPaths.CheckLinks(BepInEx.Paths.GameRootPath); }
                catch (InvalidDataException ex) {
                    blocked = true; result = "blocked";
                    WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop startup blocked by linked installation path; use the canonical installation path or update manually. " + ex.Message);
                    return;
                }
                WorkshopRuntime.CheckStartupConditions();
                step = "catalog";
                var catalog = await WorkshopRuntime.GetCatalogAsync();
                WorkshopDiagnostics.ReportStartupCatalog(WorkshopRuntime.WriteDiagnostic, catalog, out bool catalogBlocked, out int catalogFailures);
                blocked |= catalogBlocked; failures += catalogFailures;
                var plans = new List<WorkshopInstallPlan>();
                foreach (var item in catalog) {
                    if (item.DiagnosticError != null || item.IsSubscribed && (item.Package == null || !item.CanInstall)) continue;
                    string context = WorkshopDiagnostics.Context(item.WorkshopId, item.Title, item.InstalledVersion, item.Package?.Version, "local installation");
                    if (item.ManagedBy != null) {
                        // A change staged before the manager took over is discarded by the preloader.
                        WorkshopRuntime.Log(context + "; managed by external mod manager " + item.ManagedBy + "; APIShared changes nothing.");
                        continue;
                    }
                    if (WorkshopRuntime.InstallationPending(item.WorkshopId)) {
                        // Staged earlier in this or a previous session: the next start applies it.
                        WorkshopRuntime.Log(context + "; staged change waits for the next game start.");
                        continue;
                    }
                    try {
                        var status = WorkshopLocalStatus.Missing;
                        bool offers = WorkshopRuntime.OffersEnabled(item.WorkshopId);
                        if (item.IsSubscribed) {
                            var evidence = await WorkshopRuntime.InspectLocalAsync(item);
                            status = evidence.Status;
                            context = WorkshopDiagnostics.InstallationContext(item, evidence.Bootstrap, "local installation");
                            WorkshopRuntime.Log(context + "; localState=" + status + "; startupOffers=" + offers +
                                (evidence.Difference == null ? "" : "; difference=" + evidence.Difference));
                            WorkshopDiagnostics.ReportOwnership(WorkshopRuntime.WriteDiagnostic, context, evidence.Conflicts);
                        }
                        bool repair = repairs.Contains(item.WorkshopId);
                        bool verified = status == WorkshopLocalStatus.Verified && item.Receipt?.ManifestHash == item.ManifestHash;
                        if (repair && verified) {
                            repairs.Remove(item.WorkshopId);
                            verifiedRepairs.Add(item.WorkshopId);
                        }
                        // An intact old receipt does not prove that a later legacy install finished.
                        var action = WorkshopLocalState.StartupAction(item,
                            repair && !verified && status == WorkshopLocalStatus.Verified ? WorkshopLocalStatus.Modified : status,
                            offers, repair && !verified);
                        if (action != null) plans.Add(await WorkshopRuntime.PrepareStartupAsync(item.WorkshopId, action.Value));
                    }
                    catch (Exception ex) {
                        blocked = true;
                        if (WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Error) failures++;
                        WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic, context, ex);
                    }
                }
                if (repairs.Count != repairCount) WorkshopLocalState.WriteRepairs(game, repairs);
                if (verifiedRepairs.Count != 0) InspectFormerUpdaterState(game, repairs, verifiedRepairs);
                step = "batch validation";
                var retained = catalog.Where(c => c.IsSubscribed && c.Package != null).ToArray();
                // Remove invalid candidates to a fixed point; an unavailable update
                // leaves its currently installed version in the prospective state.
                bool changed;
                do {
                    changed = false;
                    var state = WorkshopBatch.EndState(plans, WorkshopRuntime.InstalledVersions());
                    foreach (var candidate in plans.ToArray()) {
                        try { WorkshopBatch.Dependencies(candidate.Package, state); }
                        catch (Exception ex) {
                            blocked = true;
                            if (WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Error) failures++;
                            WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic, WorkshopDiagnostics.Context(candidate.WorkshopId, candidate.PackageId, candidate.Previous?.Version, candidate.Version, "dependencies"), ex);
                            plans.Remove(candidate); changed = true;
                        }
                    }
                    if (changed) continue;
                    try { WorkshopRuntime.CheckBatch(plans, retained); break; }
                    catch (Exception ex) {
                        foreach (var plan in plans.ToArray()) {
                            // Restore one candidate's installed state only when
                            // that makes the complete remaining batch valid.
                            var without = plans.Where(p => p != plan).ToArray();
                            try { WorkshopRuntime.CheckBatch(without, retained); }
                            catch { continue; }
                            blocked = true;
                            if (WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Error) failures++;
                            WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic, WorkshopDiagnostics.Context(plan.WorkshopId, plan.PackageId, plan.Previous?.Version, plan.Version, "batch validation"), ex);
                            plans.Remove(plan); changed = true; break;
                        }
                        if (!changed) throw;
                    }
                } while (changed && plans.Count > 0);
                if (plans.Count == 0) { result = WorkshopDiagnostics.NoChangesResult(blocked, failures); return; }
                WorkshopRuntime.CheckBatch(plans, retained);
                string body = string.Join("\n", plans.Select(p => WorkshopStartupSummary.Describe(p, APIShared.Localization.GameLocale.Current()) +
                    (p.Diagnostics.Count == 0 ? "" : "\n  " + string.Join("\n  ", p.Diagnostics))));
                body += "\n\n" + APIShared.Localization.UiText.Get("Workshop.YesInstallAndRestart");
                step = "confirmation";
                if (Ask(body, 0x4) != 6) {
                    result = "declined";
                    WorkshopRuntime.Log("Workshop startup changes declined; installed files are untouched.");
                    return;
                }
                step = "staging";
                string batch = await WorkshopRuntime.StageStartupBatchAsync(plans);
                WorkshopRuntime.Log("Workshop startup batch " + batch + " prepared; changes=" + plans.Count + ".");
                step = "restart handoff";
                try {
                    await WorkshopRestart.RequestAsync(BepInEx.Paths.GameRootPath, batch);
                    UnityEngine.Application.Quit();
                    result = "restart requested";
                } catch (Exception ex) {
                    failures++;
                    result = "restart deferred";
                    WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic, "Workshop step=" + step + "; batch=" + batch, ex);
                    Notice(APIShared.Localization.UiText.Get("Workshop.RestartDeferred"));
                }
            } catch (Exception ex) {
                blocked = WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Warning || blocked;
                if (WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Error) failures++;
                result = blocked && failures == 0 ? "blocked" : "failed";
                string context = "Workshop step=" + step;
                if (step == "staging")
                    context += "; preparation failed; installed mod folders were not changed";
                WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic, context, ex);
                if (WorkshopDiagnostics.UserVisible(ex))
                    Notice(APIShared.Localization.UiText.Get("Workshop.WorkshopOperationFailedNoSuccessfulInstallation") + ex.Message);
            } finally { WorkshopDiagnostics.Outcome(WorkshopRuntime.WriteDiagnostic, result, blocked, failures); }
        }

        // Native box owned by the game window with a free, visible cursor (Presentation/NativeMessageBox).
        private static int Ask(string text, uint flags) {
            int answer = APIShared.Presentation.NativeMessageBox.Show(text, APIShared.Localization.UiText.Get("Workshop.DialogTitle"), flags | 0x30 | (flags == 0x4 ? 0x100u : 0u));
            if (answer == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Workshop confirmation could not be displayed.");
            return answer;
        }
        private static void Notice(string text) => Ask(text, 0);

        // The preloader records what it did at this start. Successful installations are logged;
        // a failure is shown once, because the player expected the confirmed change.
        private static int ReportPreloaderResult()
        {
            var lines = InstallRunner.TakeResult(WorkshopRuntime.Installation);
            int errors = 0;
            foreach (var line in lines) {
                if (line.Error) { errors++; WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Error, "Workshop preloader installation: " + line.Text); }
                else WorkshopRuntime.Log("Workshop preloader installation: " + line.Text);
            }
            if (errors != 0)
                Notice(APIShared.Localization.UiText.Get("Workshop.InstallationFailedAtStart") + string.Join("\n", lines.Where(l => l.Error).Select(l => l.Text)));
            return errors;
        }

        // Keep legacy backups until every affected item verifies against the current package.
        // An unreadable transaction cannot establish which installed files may be incomplete.
        private static void InspectFormerUpdaterState(string game, HashSet<ulong> repairs, HashSet<ulong> verified = null)
        {
            string workshop = WorkshopPaths.Inside(game, "_APIShared/Workshop");
            string lockPath = Path.Combine(workshop, "apply.lock");
            var folders = new[] { WorkshopPaths.Inside(game, "_APIShared/T"), WorkshopPaths.Inside(workshop, "Transactions") };
            if (!folders.Any(Directory.Exists)) return;
            Directory.CreateDirectory(workshop);
            FileStream lease;
            try { lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) {
                throw WorkshopDiagnostics.Blocked("The former Workshop updater still holds its lock.");
            }
            using (lease) {
                var discovered = new HashSet<ulong>();
                foreach (string folder in folders.Where(Directory.Exists)) {
                    foreach (string transaction in Directory.GetDirectories(folder)) {
                        WorkshopPaths.CheckLinks(transaction);
                        var ids = WorkshopLocalState.ReadFormerTransaction(transaction);
                        if (ids == null) {
                            WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Unable to classify former Workshop transaction; retained: " + transaction);
                            continue;
                        }
                        if (ids.Count != 0 && verified != null && ids.All(verified.Contains)) InstallTree.Delete(transaction);
                        else discovered.UnionWith(ids.Where(id => verified == null || !verified.Contains(id)));
                    }
                }
                int count = repairs.Count;
                repairs.UnionWith(discovered);
                if (repairs.Count != count) {
                    WorkshopLocalState.WriteRepairs(game, repairs);
                    WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Former Workshop updater was interrupted; retained backups for items " +
                        string.Join(", ", discovered.OrderBy(id => id)) + ".");
                }
            }
        }
    }
}
