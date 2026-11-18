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
    // in APIShared's preloader at the next start (src/Workshop/Install); this workflow only
    // reports its outcome, asks for consent and stages the confirmed changes.
    internal static class WorkshopStartup
    {
        private static int attempted;
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MessageBoxW(IntPtr window, string text, string caption, uint flags);
        [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
        [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr rect);
        [DllImport("user32.dll")] private static extern bool ClipCursor(ref Rect rect);
        [DllImport("user32.dll")] private static extern bool GetClipCursor(out Rect rect);
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindowsProc callback, IntPtr state);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        internal static void Run() => WorkshopStartupGate.Run(RunAsync);
        private static async Task RunAsync()
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            bool blocked = false;
            int failures = 0;
            string result = "failed", step = "startup conditions";
            try {
                step = "installation result";
                failures += ReportPreloaderResult();
                step = "former updater state";
                RemoveFormerUpdaterState(BepInEx.Paths.GameRootPath);
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
                var repairs = WorkshopLocalState.ReadRepairs(BepInEx.Paths.GameRootPath);
                int repairCount = repairs.Count;
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
                        // Repaired (or no longer subscribed): the interrupted former transaction is settled.
                        if (repair && (!item.IsSubscribed || status == WorkshopLocalStatus.Verified)) repairs.Remove(item.WorkshopId);
                        var action = WorkshopLocalState.StartupAction(item, status, offers, repair);
                        if (action != null) plans.Add(await WorkshopRuntime.PrepareStartupAsync(item.WorkshopId, action.Value));
                    }
                    catch (Exception ex) {
                        blocked = true;
                        if (WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Error) failures++;
                        WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic, context, ex);
                    }
                }
                repairs.IntersectWith(catalog.Select(c => c.WorkshopId));
                if (repairs.Count != repairCount) WorkshopLocalState.WriteRepairs(BepInEx.Paths.GameRootPath, repairs);
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
                body += "\n\n" + APIShared.Localization.UiText.Get("Workshop.YesInstallAtNextStart");
                step = "confirmation";
                if (Ask(body, 0x4) != 6) {
                    result = "declined";
                    WorkshopRuntime.Log("Workshop startup changes declined; installed files are untouched.");
                    return;
                }
                step = "staging";
                // Staging (copy and hash check) continues on a worker after this gate returns, so
                // the game start is not delayed; the preloader applies it at the next start.
                var operation = WorkshopRuntime.StageStartupBatch(plans);
                WorkshopRuntime.Log("Workshop startup batch " + operation.OperationId + " confirmed; staging " + plans.Count + " change(s) for the next game start.");
                result = "staging";
            } catch (Exception ex) {
                blocked = WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Warning || blocked;
                if (WorkshopDiagnostics.Level(ex) == WorkshopDiagnosticLevel.Error) failures++;
                result = blocked && failures == 0 ? "blocked" : "failed";
                WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic, "Workshop step=" + step, ex);
                if (WorkshopDiagnostics.UserVisible(ex))
                    Notice(APIShared.Localization.UiText.Get("Workshop.WorkshopOperationFailedNoSuccessfulInstallation") + ex.Message);
            } finally { WorkshopDiagnostics.Outcome(WorkshopRuntime.WriteDiagnostic, result, blocked, failures); }
        }

        // Runs on the game's main thread, which hides the system cursor (ShowCursor counter is per
        // thread) and confines it to the game window. Without an owner the box also opened behind
        // the game, so neither it nor other programs were reachable with the mouse. The box is
        // owned by the game window (stays above it, application-modal only: other programs stay
        // usable) and cursor visibility and confinement are released while it is open and then
        // restored for the game.
        private static int Ask(string text, uint flags) {
            bool clipped = GetClipCursor(out Rect clip);
            int shown = 0;
            try {
                ClipCursor(IntPtr.Zero);
                while (ShowCursor(true) < 0 && shown < 64) shown++;
                shown++;
                // MB_SETFOREGROUND (0x10000) brings the box to the front when it opens.
                int answer = MessageBoxW(GameWindow(), text, APIShared.Localization.UiText.Get("Workshop.DialogTitle"), flags | 0x30 | 0x10000 | (flags == 0x4 ? 0x100u : 0u));
                if (answer == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Workshop confirmation could not be displayed.");
                return answer;
            } finally {
                for (int i = 0; i < shown; i++) ShowCursor(false);
                // GetClipCursor also reports a rectangle when nothing was clipped; restoring that
                // could confine a multi-monitor player to one screen. Only a real clip returns.
                if (clipped && !(clip.Left == GetSystemMetrics(76) && clip.Top == GetSystemMetrics(77) && // SM_X/YVIRTUALSCREEN
                    clip.Right - clip.Left == GetSystemMetrics(78) && clip.Bottom - clip.Top == GetSystemMetrics(79))) // SM_CX/CYVIRTUALSCREEN
                    ClipCursor(ref clip);
            }
        }

        // Visible top-level window of the calling (main) thread, i.e. the game window; zero
        // before it exists, which keeps the former unowned box.
        private static IntPtr GameWindow() {
            IntPtr found = IntPtr.Zero;
            EnumThreadWindows(GetCurrentThreadId(), (window, _) => {
                if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero) return true; // GW_OWNER
                found = window;
                return false;
            }, IntPtr.Zero);
            return found;
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

        // Leftovers of the former external updater (removed in favour of the preloader swap):
        // transaction folders, its lock and retention files. Skipped while a former updater still
        // holds its lock. Items of a transaction that was interrupted while applying may be half
        // written, possibly without a receipt (first installation); they are recorded in
        // _APIShared/I/repair.txt and offered as a clean installation at every start until their
        // files match the Workshop package again (StartupAction, repair).
        internal static void RemoveFormerUpdaterState(string game)
        {
            string workshop = WorkshopPaths.Inside(game, "_APIShared/Workshop");
            string lockPath = Path.Combine(workshop, "apply.lock");
            var folders = new[] { WorkshopPaths.Inside(game, "_APIShared/T"), Path.Combine(workshop, "Transactions"), Path.Combine(workshop, "Unrecoverable") };
            var files = new[] { lockPath, Path.Combine(workshop, "cleanup.pending.tsv") };
            if (!folders.Any(Directory.Exists) && !files.Any(File.Exists)) return;
            if (File.Exists(lockPath)) {
                try { using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (IOException) { WorkshopRuntime.Log("Workshop former updater still holds its lock; its leftovers are removed at a later start."); return; }
            }
            var interrupted = new HashSet<ulong>();
            foreach (string folder in folders.Take(2).Where(Directory.Exists))
                foreach (string transaction in Directory.GetDirectories(folder))
                    interrupted.UnionWith(WorkshopLocalState.InterruptedItems(transaction));
            if (interrupted.Count > 0) {
                var repairs = WorkshopLocalState.ReadRepairs(game);
                repairs.UnionWith(interrupted);
                WorkshopLocalState.WriteRepairs(game, repairs);
                WorkshopRuntime.WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop former updater was interrupted while installing item(s) " +
                    string.Join(", ", interrupted.OrderBy(id => id)) + "; a clean installation is offered at startup.");
            }
            foreach (string folder in folders) {
                if (!Directory.Exists(folder)) continue;
                WorkshopPaths.CheckLinks(folder);
                InstallTree.Delete(folder);
            }
            foreach (string file in files) InstallTree.DeleteFileIfExists(file);
            WorkshopRuntime.Log("Workshop leftovers of the former updater removed.");
        }
    }
}
