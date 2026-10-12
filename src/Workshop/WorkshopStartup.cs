using System;
using System.Collections.Generic;
using System.Globalization;
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
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr window, string text, string caption, uint flags);
        internal static void Run() => WorkshopStartupGate.Run(RunAsync);
        private static async Task RunAsync()
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            try {
                if (WorkshopRuntime.IsExternallyManaged) { WorkshopRuntime.Log(WorkshopText.External); return; }
                WorkshopRuntime.CheckStartupConditions();
                var catalog = await WorkshopRuntime.GetCatalogAsync();
                var plans = new List<WorkshopInstallPlan>();
                foreach (var item in catalog) {
                    WorkshopAction? action = null;
                    if (!item.IsSubscribed && item.InstalledVersion != null) action = WorkshopAction.Remove;
                    else if (item.Package != null && item.CanInstall) {
                        if (item.InstalledVersion == null) action = WorkshopAction.Install;
                        else if (WorkshopPackageCodec.Version(item.Package.Version) > WorkshopPackageCodec.Version(item.InstalledVersion)) action = WorkshopAction.Update;
                    }
                    if (action == null) continue;
                    try { plans.Add(await WorkshopRuntime.PrepareStartupAsync(item.WorkshopId, action.Value)); }
                    catch (Exception ex) { WorkshopRuntime.Log("Deferred " + item.Title + ": " + ex.Message); }
                }
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
                            WorkshopRuntime.Log("Deferred " + candidate.PackageId + ": " + ex.Message);
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
                            WorkshopRuntime.Log("Deferred " + plan.PackageId + ": " + ex.Message);
                            plans.Remove(plan); changed = true; break;
                        }
                        if (!changed) throw;
                    }
                } while (changed && plans.Count > 0);
                if (plans.Count == 0) return;
                WorkshopRuntime.CheckBatch(plans, retained);
                WorkshopRuntime.CheckStartupConditions();
                string body = string.Join("\n", plans.Select(p => WorkshopStartupSummary.Describe(p, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de") +
                    (p.Diagnostics.Count == 0 ? "" : "\n  " + string.Join("\n  ", p.Diagnostics))));
                body += "\n\n" + WorkshopText.Pick("Yes: apply these changes and restart.\nNo: continue playing without applying them.",
                    "Ja: Diese Änderungen anwenden und neu starten.\nNein: Ohne diese Änderungen weiterspielen.");
                if (MessageBoxW(IntPtr.Zero, body, "APIShared Workshop", 0x4 | 0x30 | 0x100) != 6) {
                    WorkshopRuntime.Log("Workshop startup changes declined; installed files are untouched."); return;
                }
                var operation = await WorkshopRuntime.ApplyStartupAsync(plans, retained);
                WorkshopRuntime.Log("Workshop startup batch " + operation.OperationId + ": " + operation.State + "; " + operation.Diagnostic);
            } catch (Exception ex) { WorkshopRuntime.Log("Workshop startup deferred: " + ex.Message); }
        }
    }
}
