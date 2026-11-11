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
        internal static async Task<WorkshopOperation> ApplyStartupAsync(IReadOnlyList<WorkshopInstallPlan> plans, IReadOnlyList<WorkshopCatalogItem> catalog, WorkshopStartupAuthorization authorization,
            IReadOnlyList<WorkshopInterruptedTransaction> repair = null, IReadOnlyList<WorkshopInterruptedTransaction> archive = null)
        {
            authorization.Validate();
            CheckStartupConditions(); CheckBatch(plans, catalog);
            var snapshot = Subscriptions();
            CheckBatchSubscriptions(plans, snapshot);
            string id = Guid.NewGuid().ToString("N");
            if (repair != null && repair.Count == 0) repair = null;
            bool completeRepair = repair != null || archive != null && archive.Count != 0;
            // Unreadable transactions are moved aside only for this confirmed repair and
            // returned if it cannot be staged or handed off.
            IReadOnlyList<KeyValuePair<string, string>> archived = null;
            return await ApplyCoordinator.ExecuteAsync(id, async operation => {
                string transaction;
                try {
                    transaction = await Task.Run(() => {
                        foreach (var plan in plans.Where(p => p.Package != null)) {
                            if (plan.Action == WorkshopAction.Update &&
                                WorkshopLocalState.InspectReceipt(plan.GameRoot, plan.Previous).Status != WorkshopLocalStatus.Verified)
                                throw WorkshopDiagnostics.Blocked("Local Workshop installation changed after the startup check: " + plan.PackageId);
                            WorkshopBootstrapValidation.Verify(plan.Package, plan.Source);
                            WorkshopSubscriptionOwnership.Verify(snapshot, plan.WorkshopId, plan.Package);
                        }
                        archived = WorkshopRecovery.Archive(Paths.GameRootPath, archive);
                        return WorkshopTransaction.StageBatch(plans, id, repair);
                    });
                    authorization.Validate();
                } catch { RestoreArchived(archived); throw; }
                var completion = new TaskCompletionSource<WorkshopOperation>();
                if (!UnityMainThreadDispatch.TryRunInlineOrEnqueue(async () => {
                    try {
                        authorization.Validate();
                        CheckBatchSubscriptions(plans, snapshot); CheckBatch(plans, catalog);
                        await WorkshopUpdaterLauncher.Launch(transaction, Paths.GameRootPath);
                        CheckBatchSubscriptions(plans, snapshot); CheckBatch(plans, catalog);
                        operation.Set(WorkshopOperationState.AwaitingRestart, "Confirmed startup batch; receipts commit after game exit.");
                        authorization.ConfirmHandoff(transaction);
                        authorization.Quit(Log, UnityEngine.Application.Quit, completeRepair ? "confirmed complete repair" : "confirmed startup update"); completion.TrySetResult(operation);
                    } catch (Exception ex) {
                        operation.Failure = ex;
                        WorkshopDiagnostics.Failure(WriteDiagnostic, "Workshop step=batch updater handoff; operation=" + operation.OperationId, ex);
                        try { WorkshopUpdaterLauncher.Cancel(transaction); } catch (Exception cancel) { operation.Failure = new AggregateException(ex, cancel); WorkshopDiagnostics.Failure(WriteDiagnostic, "Workshop step=batch handoff cancellation; operation=" + operation.OperationId, cancel); }
                        RestoreArchived(archived);
                        operation.Set(WorkshopOperationState.Failed, ex.Message); completion.TrySetResult(operation);
                    }
                })) { RestoreArchived(archived); throw new InvalidOperationException("Unity dispatcher unavailable; batch was not applied."); }
                return await completion.Task;
            });
        }
        private static void RestoreArchived(IReadOnlyList<KeyValuePair<string, string>> moved)
        {
            try { WorkshopRecovery.Restore(moved); }
            catch (Exception ex) { WorkshopDiagnostics.Failure(WriteDiagnostic, "Workshop step=restore archived transactions", ex); }
        }
        internal static Task<WorkshopOperation> ApplyAsync(string owner, WorkshopInstallPlan plan) =>
            ApplyCoreAsync(owner, plan, false);

        internal static Task<WorkshopOperation> ApplySettingsAsync(WorkshopInstallPlan plan) =>
            ApplyCoreAsync(APISharedPlugin.PluginGuid, plan, true);

        private static async Task<WorkshopOperation> ApplyCoreAsync(string owner, WorkshopInstallPlan plan, bool fromSettings)
        {
            RequireSteam();
            if (plan == null || plan.Owner != owner || plan.GameRoot != Path.GetFullPath(Paths.GameRootPath)) throw new ArgumentException("This client does not own the plan.");
            if (IsExternallyManaged) throw new InvalidOperationException(APIShared.Localization.UiText.Get("Workshop.ExternallyManaged"));
            if (startupDownloadPending || Directory.Exists(WorkshopPaths.Inside(Paths.GameRootPath, "_SE/.staging"))) throw new InvalidOperationException("Extender startup work takes precedence. Restart before applying this plan.");
            if (!fromSettings && MissionLifecycleService.HasContext) throw new InvalidOperationException("Return to the menu before applying Workshop changes.");
            var subscriptions = Subscriptions(); var subscription = subscriptions.FirstOrDefault(x => x.Id == plan.WorkshopId);
            if (subscriptions.Any(x => (x.State & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0)) throw new InvalidOperationException("Let Extender finish pending downloads and restart first.");
            if (plan.Action == WorkshopAction.Remove ? subscription != null : subscription == null || !Stable(subscription) || Path.GetFullPath(subscription.Root) != plan.Source)
                throw new InvalidOperationException("Steam state changed; prepare again.");
            Prerequisites(plan.Package);
            return await ApplyCoordinator.ExecuteAsync(plan.OperationId, async operation =>
            {
                if (fromSettings) {
                    WorkshopTransaction.ValidateManifest(plan);
                    SettingsHandoff.Queue(() => LaunchOnShutdown(plan, subscriptions, operation));
                    operation.Set(WorkshopOperationState.AwaitingRestart, "Plan queued for validation on manual game exit.", plan.WorkshopId);
                    Log("Workshop installation plan queued for manual game exit; item=" + plan.WorkshopId +
                        "; version=" + plan.Version + "; operation=" + operation.OperationId + ".");
                    return operation;
                }
                string transaction = await Task.Run(() => StageInstallation(plan, subscriptions));
                var completion = new TaskCompletionSource<WorkshopOperation>();
                if (!UnityMainThreadDispatch.TryEnqueue(async () => {
                    try {
                        if (IsExternallyManaged || startupDownloadPending || MissionLifecycleService.HasContext ||
                            Directory.Exists(WorkshopPaths.Inside(Paths.GameRootPath, "_SE/.staging")))
                            throw new InvalidOperationException("Installation conditions changed.");
                        var latest = Subscriptions();
                        if (latest.Any(x => (x.State & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0)) throw new InvalidOperationException("Extender download work began while staging.");
                        if (!latest.Select(x => x.Id).OrderBy(x => x).SequenceEqual(subscriptions.Select(x => x.Id).OrderBy(x => x))) throw new InvalidOperationException("Subscription list changed while staging; prepare again.");
                        var current = latest.FirstOrDefault(x => x.Id == plan.WorkshopId);
                        if (plan.Action == WorkshopAction.Remove ? current != null : current == null || !Stable(current) || Path.GetFullPath(current.Root) != plan.Source)
                            throw new InvalidOperationException("Steam state changed while staging; prepare again.");
                        Prerequisites(plan.Package);
                        await WorkshopUpdaterLauncher.Launch(transaction, plan.GameRoot);
                        var beforeExit = Subscriptions();
                        if (IsExternallyManaged || MissionLifecycleService.HasContext || Directory.Exists(WorkshopPaths.Inside(Paths.GameRootPath, "_SE/.staging")) ||
                            beforeExit.Any(x => (x.State & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0) ||
                            !beforeExit.Select(x => x.Id).OrderBy(x => x).SequenceEqual(latest.Select(x => x.Id).OrderBy(x => x))) {
                            // The catch below cancels the handoff and reports a cancellation failure.
                            throw new InvalidOperationException("Installation conditions changed before game exit.");
                        }
                        operation.Set(WorkshopOperationState.AwaitingRestart, "Updater started; installation is committed only after game exit.", plan.WorkshopId);
                        Log("Workshop quit reason=confirmed consumer update; transaction=" + transaction + "; handoff=confirmed.");
                        UnityEngine.Application.Quit();
                        completion.TrySetResult(operation);
                    } catch (Exception ex) {
                        WorkshopDiagnostics.Failure(WriteDiagnostic, "Workshop step=updater handoff; operation=" + operation.OperationId, ex);
                        try { WorkshopUpdaterLauncher.Cancel(transaction); } catch (Exception cancelFailure) { WorkshopDiagnostics.Failure(WriteDiagnostic, "Workshop step=handoff cancellation; operation=" + operation.OperationId, cancelFailure); }
                        operation.Set(WorkshopOperationState.Failed, ex.Message); completion.TrySetResult(operation);
                    }
                })) throw new InvalidOperationException("Unity dispatcher unavailable; staged transaction was not applied.");
                return await completion.Task;
            });
        }

        private static string StageInstallation(WorkshopInstallPlan plan, Subscription[] subscriptions)
        {
            if (plan.Package != null) {
                WorkshopBootstrapValidation.Verify(plan.Package, plan.Source);
                WorkshopSubscriptionOwnership.Verify(subscriptions, plan.WorkshopId, plan.Package);
            }
            return WorkshopTransaction.Stage(plan);
        }
    }
}
