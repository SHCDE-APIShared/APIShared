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
    internal static class WorkshopText
    {
        internal static string Pick(string en, string de) => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? de : en;
        internal static string External => Pick("Managed by external mod manager", "Durch externen Modmanager verwaltet");
    }
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
                    WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop step=startup conditions; " + WorkshopText.External);
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
                        Notice(WorkshopText.Pick("A previously confirmed Workshop update is still running. This game start will close when you press OK. Wait for the updater to restart the game; do not start it again manually.",
                            "Ein zuvor bestätigtes Workshop-Update läuft noch. Dieser Spielstart wird mit OK beendet. Bitte den Neustart durch den Updater abwarten und das Spiel nicht erneut manuell starten."));
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
                if (!await WorkshopUpdaterLauncher.Maintain(BepInEx.Paths.GameRootPath)) { blocked = true; result = "blocked"; return; }
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
                string body = string.Join("\n", plans.Select(p => WorkshopStartupSummary.Describe(p, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de") +
                    (p.Diagnostics.Count == 0 ? "" : "\n  " + string.Join("\n  ", p.Diagnostics))));
                body += "\n\n" + WorkshopText.Pick("Yes: apply these changes and restart.\nNo: continue playing without applying them.",
                    "Ja: Diese Änderungen anwenden und neu starten.\nNein: Ohne diese Änderungen weiterspielen.");
                step = "confirmation";
                int decision = MessageBoxW(IntPtr.Zero, body, "APIShared Workshop", 0x4 | 0x30 | 0x100);
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
                    if (operation.Failure == null || WorkshopDiagnostics.UserVisible(operation.Failure)) Notice(WorkshopText.Pick("Workshop update could not be started.\n", "Workshop-Update konnte nicht gestartet werden.\n") + operation.Diagnostic);
                    if (operation.Failure != null && WorkshopDiagnostics.Level(operation.Failure) == WorkshopDiagnosticLevel.Warning) { blocked = true; result = "blocked"; }
                    else {
                        failures++;
                        WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Error, "Workshop step=" + step + "; operation=" + operation.OperationId + "; " + operation.Diagnostic);
                    }
                }
            } catch (Exception ex) {
                blocked = WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Warning || blocked;
                if (WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Error) failures++;
                result = blocked && failures == 0 ? "blocked" : "failed";
                WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic, "Workshop step=" + step, ex);
                if (WorkshopDiagnostics.UserVisible(ex)) Notice(WorkshopText.Pick("Workshop operation failed. No successful installation was recorded.\n", "Workshop-Vorgang fehlgeschlagen. Es wurde keine erfolgreiche Installation bestätigt.\n") + ex.Message);
            } finally { WorkshopDiagnostics.Outcome(WorkshopRuntime.WriteDiagnostic, result, blocked, failures); }
        }

        private static int Ask(string text, uint flags) {
            int answer = MessageBoxW(IntPtr.Zero, text, "APIShared Workshop", flags | 0x30 | (flags == 0x3 ? 0x200u : flags == 0x4 ? 0x100u : 0u));
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
            if (Directory.Exists(root)) foreach (string directory in Directory.EnumerateDirectories(root)) {
                var journal = WorkshopRecovery.Journal(directory);
                if (journal.Length == 0) continue;
                var interrupted = entries.FirstOrDefault(x => x.Directory == directory);
                WorkshopRuntime.Log("Workshop previous transaction=" + directory + "; outcome=" + (interrupted != null
                    ? WorkshopRecovery.Describe(interrupted, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de")
                    : WorkshopRecovery.Finished(directory) ? journal.Last(x => x == "committed" || x == "restored") : journal.Last()));
            }
            if (entries.Count == 0) return false;
            bool rollback = entries.All(x => x.CanRollback);
            string details = string.Join("\n\n", entries.Select(x => x.Directory + "\nWorkshop: " + string.Join(", ", x.Items) + "\n" + WorkshopRecovery.Describe(x, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de") +
                (x.CanRollback ? "" : "\nRollback: " + x.RollbackError)));
            // Show a failed external recovery/replacement attempt too: its
            // failure belongs to the request journal, not the original journal.
            foreach (string directory in Directory.EnumerateDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc)) {
                if (WorkshopRecovery.Finished(directory)) continue;
                string references = WorkshopPaths.Inside(directory, "supersedes.tsv");
                if (!File.Exists(references)) references = WorkshopPaths.Inside(directory, "operations.tsv");
                if (!File.Exists(references) || !File.ReadAllLines(references).Any(line => entries.Any(e => line.StartsWith(Path.GetFileName(e.Directory) + "\t", StringComparison.Ordinal)))) continue;
                string failure = WorkshopRecovery.Journal(directory).LastOrDefault(line => line.StartsWith("failure ", StringComparison.Ordinal));
                if (failure == null) continue;
                details += "\n\n" + WorkshopText.Pick("Previous attempt: ", "Vorheriger Versuch: ") + directory + "\n" + failure;
                break;
            }
            string choices = rollback ? WorkshopText.Pick(
                "Yes: repair (restore first, restart and offer the current update).\nNo: rollback to the previous state and close the game. The next start offers updates again.\nCancel: later.",
                "Ja: Reparieren (zuerst wiederherstellen, neu starten und aktuelles Update anbieten).\nNein: Rollback auf den vorherigen Zustand und Spiel schließen. Beim nächsten Start werden Updates wieder angeboten.\nAbbrechen: Später.") : WorkshopText.Pick(
                "Rollback is unavailable for the reasons above.\nYes: prepare a complete repair.\nNo: later.",
                "Rollback ist aus den oben genannten Gründen nicht verfügbar.\nJa: Vollständige Reparatur vorbereiten.\nNein: Später.");
            int decision = Ask(WorkshopText.Pick("An interrupted Workshop installation needs attention.\n\n", "Eine unterbrochene Workshop-Installation benötigt eine Reparatur.\n\n") + details + "\n\n" + choices, rollback ? 0x3u : 0x4u);
            var choice = WorkshopRecovery.Decide(rollback, decision);
            if (choice == WorkshopRecovery.Choice.Later) { WorkshopRuntime.Log("Workshop recovery postponed; retained " + details); return true; }
            if (rollback) {
                await WorkshopRuntime.RollbackAsync(entries, choice == WorkshopRecovery.Choice.Repair, WorkshopStartupGate.AuthorizeConfirmedUpdate());
                return true;
            }
            // No backup is trusted implicitly. A full package repair is a second,
            // explicit consent boundary and preserves unrelated/obsolete files.
            var plans = new List<WorkshopInstallPlan>();
            foreach (ulong id in entries.SelectMany(x => x.Items).Distinct()) plans.Add(await WorkshopRuntime.PrepareRepairAsync(id));
            if (plans.Count == 0) throw new InvalidOperationException("Cannot identify affected Workshop packages. Keep the transaction directory for manual repair: " + details);
            await Task.Run(() => WorkshopRecovery.AssertRepairCoverage(game, entries, plans));
            var catalog = await WorkshopRuntime.GetCatalogAsync();
            var retained = catalog.Where(x => x.IsSubscribed && x.Package != null).ToArray();
            WorkshopRuntime.CheckBatch(plans, retained);
            string body = WorkshopText.Pick("Rollback is unavailable. Install the complete current packages after closing the game? Existing affected files will be backed up; settings and unknown files remain.\n\n",
                "Rollback ist nicht möglich. Nach dem Schließen des Spiels die vollständigen aktuellen Pakete installieren? Vorhandene betroffene Dateien werden zusätzlich gesichert; Einstellungen und unbekannte Dateien bleiben erhalten.\n\n") +
                string.Join("\n", plans.Select(p => p.PackageId + " " + p.Version + (p.Diagnostics.Count == 0 ? "" : "\n" + string.Join("\n", p.Diagnostics)))) + "\n\n" + details;
            foreach (var plan in plans) foreach (string diagnostic in plan.Diagnostics) WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop complete repair; " + diagnostic);
            if (Ask(body, 0x4) != 6) return true;
            var operation = await WorkshopRuntime.ApplyStartupAsync(plans, retained, WorkshopStartupGate.AuthorizeConfirmedUpdate(), entries);
            if (operation.State != WorkshopOperationState.AwaitingRestart) throw operation.Failure ?? new IOException(operation.Diagnostic);
            return true;
        }
    }
}
