using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace APIShared.Workshop
{
    // Steam can initialize more than once. One process-owned attempt prevents
    // repeated prompts; deferred/declined work is rediscovered on the next start.
    internal static class WorkshopStartup
    {
        private static int attempted;
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MessageBoxW(IntPtr window, string text, string caption, uint flags);
        internal static void Run() => WorkshopStartupGate.Run(RunAsync);
        private static async Task RunAsync()
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            bool blocked = false;
            int failures = 0;
            string result = "failed", step = "startup conditions";
            try {
                if (WorkshopRuntime.IsExternallyManaged) {
                    blocked = true; result = "blocked";
                    WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop step=startup conditions; Managed by external mod manager");
                    return;
                }
                try { WorkshopPaths.CheckLinks(BepInEx.Paths.GameRootPath); }
                catch (System.IO.InvalidDataException ex) {
                    blocked = true; result = "blocked";
                    WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop startup blocked by linked installation path; use the canonical installation path or update manually. " + ex.Message);
                    return;
                }
                step = "active updater check";
                var activity = await WorkshopUpdaterLauncher.Activity(BepInEx.Paths.GameRootPath);
                if (activity.Busy) {
                    blocked = true; result = "blocked";
                    if (activity.Active) {
                        Notice(APIShared.Localization.UiText.Get("Workshop.APreviouslyConfirmedWorkshopUpdateIs"));
                        var currentActivity = await WorkshopUpdaterLauncher.Activity(BepInEx.Paths.GameRootPath);
                        if (!currentActivity.Active || currentActivity.Transaction != activity.Transaction || currentActivity.OperationsHash != activity.OperationsHash) {
                            WorkshopRuntime.Log("Workshop updater activity changed while the notice was open; no game exit requested and no startup changes planned.");
                            return;
                        }
                        var authorization = WorkshopStartupGate.AuthorizeConfirmedUpdate();
                        authorization.ConfirmHandoff(activity.Transaction);
                        authorization.Quit(WorkshopRuntime.Log, UnityEngine.Application.Quit, "previous confirmed updater still active; phase=" + activity.Phase);
                    } else WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop updater lock is busy or its status is unverified; startup planning and recovery deferred. Close the game and inspect _APIShared/Workshop/apply.lock if this persists.");
                    return;
                }
                WorkshopRuntime.CheckStartupConditions();
                step = "history maintenance";
                if (!await WorkshopUpdaterLauncher.Maintain(BepInEx.Paths.GameRootPath)) {
                    // A maintenance child survived its kill and still owns the
                    // transaction directory; recovery and staging wait for a later start.
                    blocked = true; result = "blocked"; return;
                }
                step = "recovery inspection";
                if (await RecoverAsync()) { result = "recovery offered"; return; }
                step = "catalog";
                var catalog = await WorkshopRuntime.GetCatalogAsync();
                var plans = new List<WorkshopInstallPlan>();
                foreach (var item in catalog) {
                    var diagnostic = WorkshopDiagnostics.Catalog(WorkshopRuntime.WriteDiagnostic, item);
                    if (diagnostic == WorkshopDiagnosticLevel.Error) failures++;
                    else if (diagnostic == WorkshopDiagnosticLevel.Warning) blocked = true;
                    WorkshopAction? action = null;
                    if (!item.IsSubscribed && item.InstalledVersion != null) action = WorkshopAction.Remove;
                    else if (item.Package != null && item.CanInstall) {
                        if (item.InstalledVersion == null) action = WorkshopAction.Install;
                        else if (WorkshopPackageCodec.Version(item.Package.Version) > WorkshopPackageCodec.Version(item.InstalledVersion)) action = WorkshopAction.Update;
                    }
                    if (action == null) {
                        if (item.Package != null && item.CanInstall && item.DiagnosticError == null)
                            WorkshopRuntime.Log(WorkshopDiagnostics.Context(item.WorkshopId, item.Title, item.InstalledVersion, item.Package.Version, "version comparison") + "; no newer release; no automatic reinstall or downgrade.");
                        continue;
                    }
                    try { plans.Add(await WorkshopRuntime.PrepareStartupAsync(item.WorkshopId, action.Value)); }
                    catch (Exception ex) {
                        blocked = true;
                        if (WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Error) failures++;
                        WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic,
                            WorkshopDiagnostics.Context(item.WorkshopId, item.Title, item.InstalledVersion, item.Package?.Version, "prepare " + action), ex);
                    }
                }
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
                WorkshopRuntime.CheckStartupConditions();
                string body = string.Join("\n", plans.Select(p => WorkshopStartupSummary.Describe(p, APIShared.Localization.GameLocale.Current()) +
                    (p.Diagnostics.Count == 0 ? "" : "\n  " + string.Join("\n  ", p.Diagnostics))));
                body += "\n\n" + APIShared.Localization.UiText.Get("Workshop.YesApplyTheseChangesAndRestart");
                step = "confirmation";
                int decision = MessageBoxW(IntPtr.Zero, body, APIShared.Localization.UiText.Get("Workshop.DialogTitle"), 0x4 | 0x30 | 0x100);
                if (decision == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Workshop confirmation could not be displayed.");
                if (decision != 6) {
                    result = "declined";
                    WorkshopRuntime.Log("Workshop startup changes declined; installed files are untouched."); return;
                }
                step = "updater handoff";
                var operation = await WorkshopRuntime.ApplyStartupAsync(plans, retained, WorkshopStartupGate.AuthorizeConfirmedUpdate());
                WorkshopRuntime.Log("Workshop startup batch " + operation.OperationId + ": " + operation.State + "; " + operation.Diagnostic);
                result = operation.State == WorkshopOperationState.AwaitingRestart ? "awaiting restart" : "failed";
                if (operation.State != WorkshopOperationState.AwaitingRestart) {
                    if (operation.Failure != null && WorkshopDiagnostics.Level(operation.Failure) == WorkshopDiagnosticLevel.Warning) { blocked = true; result = "blocked"; }
                    else {
                        failures++;
                        WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Error, "Workshop step=" + step + "; operation=" + operation.OperationId + "; " + operation.Diagnostic);
                        Notice(APIShared.Localization.UiText.Get("Workshop.WorkshopUpdateCouldNotBeStarted") + operation.Diagnostic);
                    }
                }
            } catch (Exception ex) {
                blocked = WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Warning || blocked;
                if (WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Error) failures++;
                result = blocked && failures == 0 ? "blocked" : "failed";
                WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic, "Workshop step=" + step, ex);
                if (WorkshopDiagnostics.UserVisible(ex))
                    Notice(APIShared.Localization.UiText.Get("Workshop.WorkshopOperationFailedNoSuccessfulInstallation") + ex.Message);
            } finally { WorkshopDiagnostics.Outcome(WorkshopRuntime.WriteDiagnostic, result, blocked, failures); }
        }

        private static int Ask(string text, uint flags) {
            int answer = MessageBoxW(IntPtr.Zero, text, APIShared.Localization.UiText.Get("Workshop.DialogTitle"), flags | 0x30 | (flags == 0x3 ? 0x200u : flags == 0x4 ? 0x100u : 0u));
            if (answer == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Workshop confirmation could not be displayed.");
            return answer;
        }
        private static void Notice(string text) => Ask(text, 0);

        // Recovery is separate from public ApplyAsync: neither discovery nor a
        // third-party caller silently consents to installation or rollback.
        private static async Task<bool> RecoverAsync()
        {
            string game = BepInEx.Paths.GameRootPath;
            var entries = await Task.Run(() => WorkshopRecovery.Inspect(game));
            string root = WorkshopPaths.Inside(WorkshopInstallation.StateRoot(game), "Transactions");
            bool german = APIShared.Localization.GameLocale.Current() == "de-DE";
            // Unfinished transactions are logged in full; of the retained finished history
            // only the newest outcome, so the log does not repeat it on every start.
            if (Directory.Exists(root)) {
                foreach (var interrupted in entries)
                    WorkshopRuntime.Log("Workshop unfinished transaction=" + interrupted.Directory + "; " + WorkshopRecovery.Describe(interrupted, false));
                string latest = Directory.EnumerateDirectories(root).Where(WorkshopRecovery.Finished).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
                if (latest != null)
                    WorkshopRuntime.Log("Workshop latest transaction=" + latest + "; outcome=" + WorkshopRecovery.Journal(latest).Last(x => x == "committed" || x == "restored"));
            }
            if (entries.Count == 0) return false;
            string details = string.Join("\n\n", entries.Select(x => x.Directory + "\n" + APIShared.Localization.UiText.Get("Workshop.Recovery.Items", "Ids", x.Items.Length == 0 ? "?" : string.Join(", ", x.Items)) + "\n" +
                WorkshopRecovery.Describe(x, APIShared.Localization.GameLocale.Current()) + (x.CanRollback ? "" : "\n" + APIShared.Localization.UiText.Get("Workshop.Recovery.NoRollback", "Reason", x.RollbackError))));
            // Show a failed external recovery/replacement attempt too: its
            // failure belongs to the request journal, not the original journal.
            bool attemptFailed = false;
            foreach (string directory in Directory.EnumerateDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc)) {
                if (WorkshopRecovery.Finished(directory)) continue;
                string references = WorkshopPaths.Inside(directory, "supersedes.tsv");
                if (!File.Exists(references)) references = WorkshopPaths.Inside(directory, "operations.tsv");
                if (!File.Exists(references) || !File.ReadAllLines(references).Any(line => entries.Any(e => line.StartsWith(Path.GetFileName(e.Directory) + "\t", StringComparison.Ordinal)))) continue;
                string failure = WorkshopRecovery.Journal(directory).LastOrDefault(line => line.StartsWith("failure ", StringComparison.Ordinal));
                if (failure == null) continue;
                details += "\n\n" + APIShared.Localization.UiText.Get("Workshop.PreviousAttempt") + directory + "\n" + failure;
                attemptFailed = true;
                break;
            }
            // A rollback that already failed is not offered again: the clean reinstallation
            // below is the fallback that is always available.
            bool rollback = !attemptFailed && entries.All(x => x.CanRollback);
            if (rollback) {
                string choices = APIShared.Localization.UiText.Get("Workshop.YesRepairRestoreFirstRestartAnd");
                int decision = Ask(APIShared.Localization.UiText.Get("Workshop.AnInterruptedWorkshopInstallationNeedsAttention") + details + "\n\n" + choices, 0x3u);
                var choice = WorkshopRecovery.Decide(true, decision);
                if (choice == WorkshopRecovery.Choice.Later) { WorkshopRuntime.Log("Workshop recovery postponed; retained " + details); return true; }
                await WorkshopRuntime.RollbackAsync(entries, choice == WorkshopRecovery.Choice.Repair, WorkshopStartupGate.AuthorizeConfirmedUpdate());
                return true;
            }
            return await CompleteRepairAsync(game, entries, details, german);
        }

        // Clean reinstallation from the Steam downloads (WorkshopRepairPlanner). It needs no
        // backup of the interrupted transaction and no readable history, so it is always
        // possible once Steam provides the subscribed packages. Every outcome is shown.
        private static async Task<bool> CompleteRepairAsync(string game, IReadOnlyList<WorkshopInterruptedTransaction> entries, string details, bool german)
        {
            WorkshopRuntime.CleanRepair repair;
            try { repair = await WorkshopRuntime.PrepareCleanRepairAsync(entries); }
            catch (Exception ex) {
                WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic, "Workshop step=complete repair preparation", ex);
                Notice(APIShared.Localization.UiText.Get("Workshop.AnInterruptedWorkshopInstallationMustBe") + ex.Message + "\n\n" +
                    APIShared.Localization.UiText.Get("Workshop.NothingWasChangedTheRepairIs") + "\n\n" + details);
                return true;
            }
            if (repair.Plans.Count == 0) {
                // Only unreadable history and no APIShared-managed package installed or subscribed.
                var moved = await Task.Run(() => WorkshopRecovery.Archive(game, repair.Archived));
                WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop unreadable transactions archived; no installed package is affected: " + string.Join(", ", moved.Select(x => x.Value)));
                return false;
            }
            string plans = string.Join("\n", repair.Plans.Select(p => WorkshopStartupSummary.Describe(p, APIShared.Localization.GameLocale.Current()) + (p.RemovedFiles.Count == 0 ? "" :
                APIShared.Localization.UiText.Get("Workshop.RemovedFiles", "Count", p.RemovedFiles.Count)) +
                (p.Diagnostics.Count == 0 ? "" : "\n  " + string.Join("\n  ", p.Diagnostics))));
            string body = APIShared.Localization.UiText.Get("Workshop.AnInterruptedWorkshopInstallationCannotBe") + details + "\n\n" + plans + "\n\n" +
                APIShared.Localization.UiText.Get("Workshop.YesAfterTheGameClosesReinstall");
            if (WorkshopRecovery.Decide(false, Ask(body, 0x4)) != WorkshopRecovery.Choice.CompleteRepair) {
                WorkshopRuntime.Log("Workshop complete repair postponed; retained " + details);
                return true;
            }
            var operation = await WorkshopRuntime.ApplyStartupAsync(repair.Plans, repair.Retained, WorkshopStartupGate.AuthorizeConfirmedUpdate(), repair.Superseded, repair.Archived);
            if (operation.State != WorkshopOperationState.AwaitingRestart) throw operation.Failure ?? new IOException(operation.Diagnostic);
            return true;
        }
    }
}
