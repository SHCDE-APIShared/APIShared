using APIShared.Internal;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.API.Components.ModManager;
using SHCDESE.EventAPI;
using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Version = System.Version;

namespace APIShared.Workshop
{
    internal static partial class WorkshopRuntime
    {
        internal sealed class CleanRepair
        {
            internal IReadOnlyList<WorkshopInstallPlan> Plans;
            internal IReadOnlyList<WorkshopCatalogItem> Retained;
            internal IReadOnlyList<WorkshopInterruptedTransaction> Superseded, Archived;
        }
        // Plans the clean reinstallation that replaces every interrupted transaction when
        // rollback is unavailable (see WorkshopRepairPlanner). Affected packages come from the
        // interrupted receipts and plugin folders; if any operation list is unreadable, every
        // APIShared-managed package is affected. Subscribed packages are reinstalled from their
        // Steam download, the others are removed. Throws only for conditions the player can
        // resolve (download still pending, prerequisite missing), never for damaged history.
        internal static async Task<CleanRepair> PrepareCleanRepairAsync(IReadOnlyList<WorkshopInterruptedTransaction> entries)
        {
            string game = Paths.GameRootPath;
            var readable = entries.Where(x => x.Readable).ToArray();
            var unreadable = entries.Where(x => !x.Readable).ToArray();
            var scope = await Task.Run(() => WorkshopRepairPlanner.Collect(game, readable));
            var catalog = await GetCatalogAsync();
            var items = new HashSet<ulong>(scope.Items);
            if (unreadable.Length != 0)
                foreach (var item in catalog.Where(c => c.WorkshopId != 0 && (c.Package != null || c.InstalledVersion != null))) items.Add(item.WorkshopId);
            foreach (string root in scope.RootTargets.Keys) {
                var owner = catalog.FirstOrDefault(c => c.IsSubscribed && c.Package != null && c.Package.Roots.Contains(root, StringComparer.OrdinalIgnoreCase));
                if (owner != null) items.Add(owner.WorkshopId);
            }
            var plans = new List<WorkshopInstallPlan>();
            foreach (ulong id in items.OrderBy(x => x)) {
                var item = catalog.FirstOrDefault(c => c.WorkshopId == id);
                if (item != null && item.IsSubscribed) {
                    if (item.Package == null)
                        throw WorkshopDiagnostics.Blocked(APIShared.Localization.UiText.Get("Workshop.WorkshopItemIsNotReadyIn") +
                            item.Title + " (" + item.Status + ")");
                    WorkshopReceipt receipt = null;
                    try { receipt = WorkshopInstallation.ReadReceipt(game, id); }
                    catch (InvalidDataException) { }
                    var plan = await PrepareStartupAsync(id, receipt == null ? WorkshopAction.Install : WorkshopAction.Reinstall);
                    plans.Add(plan);
                } else plans.Add(await Task.Run(() => WorkshopRepairPlanner.Removal(APISharedPlugin.PluginGuid, game, id)));
            }
            await Task.Run(() => {
                WorkshopRepairPlanner.Complete(game, plans, scope);
                if (readable.Length != 0) WorkshopRecovery.AssertRepairCoverage(game, readable, plans);
            });
            return new CleanRepair { Plans = plans.AsReadOnly(), Retained = catalog.Where(c => c.IsSubscribed && c.Package != null).ToArray(),
                Superseded = readable, Archived = unreadable };
        }
        internal static async Task RollbackAsync(IReadOnlyList<WorkshopInterruptedTransaction> entries, bool restart, WorkshopStartupAuthorization authorization)
        {
            authorization.Validate();
            CheckStartupConditions();
            string transaction = await Task.Run(() => WorkshopRecovery.StageRollback(Paths.GameRootPath, entries, restart));
            try {
                authorization.Validate();
                CheckStartupConditions();
                await WorkshopUpdaterLauncher.Launch(transaction, Paths.GameRootPath);
                CheckStartupConditions();
                authorization.ConfirmHandoff(transaction);
                authorization.Quit(Log, UnityEngine.Application.Quit, restart ? "confirmed recovery with restart" : "confirmed rollback and stop");
            } catch { WorkshopUpdaterLauncher.Cancel(transaction); throw; }
        }
    }
}
