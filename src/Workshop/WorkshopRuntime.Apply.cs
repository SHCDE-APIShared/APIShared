using APIShared.Workshop.Install;
using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace APIShared.Workshop
{
    // Applying a confirmed plan stages a verified copy (src/Workshop/Install) and leaves a
    // request; APIShared's preloader swaps it into BepInEx/plugins at the next game start,
    // before any plugin is loaded. Only WorkshopStartup can request an automatic restart.
    internal static partial class WorkshopRuntime
    {
        // Serializes staging; each item has exactly one staging container.
        private static readonly SemaphoreSlim StagingGate = new SemaphoreSlim(1, 1);
        internal static InstallPaths Installation => new InstallPaths(Paths.GameRootPath);

        // True when APIShared's preloader (DependencyBootstrap) ran its installation pass in this
        // process; only then does the next start apply a staged change.
        internal static bool InstallerActive => AppDomain.CurrentDomain.GetData(InstallRunner.ActiveKey) is bool active && active;

        // The startup gate awaits the published batch before authorizing a restart.
        internal static Task<string> StageStartupBatchAsync(IReadOnlyList<WorkshopInstallPlan> plans)
        {
            CheckStageable(plans);
            return Task.Run(() => StagePlansAsync(plans));
        }

        // Public consumer path (WorkshopClient.ApplyAsync): the plan is the explicit consent.
        internal static async Task<WorkshopOperation> ApplyAsync(string owner, WorkshopInstallPlan plan)
        {
            RequireSteam();
            if (plan == null || plan.Owner != owner || plan.GameRoot != Path.GetFullPath(Paths.GameRootPath)) throw new ArgumentException("This client does not own the plan.");
            if (IsExternallyManaged) throw new InvalidOperationException(APIShared.Localization.UiText.Get("Workshop.ExternallyManaged"));
            CheckStageable(new[] { plan });
            Prerequisites(plan.Package);
            var operation = new WorkshopOperation(plan.OperationId);
            try {
                await Task.Run(() => StagePlansAsync(new[] { plan }));
                operation.Set(WorkshopOperationState.AwaitingRestart, "Staged; applied at the next game start.", plan.WorkshopId);
            } catch (Exception ex) {
                operation.Failure = ex;
                WorkshopDiagnostics.Failure(WriteDiagnostic, "Workshop step=staging; operation=" + operation.OperationId, ex);
                operation.Set(WorkshopOperationState.Failed, ex.Message, plan.WorkshopId);
            }
            return operation;
        }

        // Rechecked right before staging (caller's thread): the preloader is active, the startup
        // conditions still hold (downloads, Script Extender staging, external management; an active
        // mission does not matter because nothing changes in this session) and every plan still
        // matches its Steam subscription and owns its folders.
        internal static void CheckInstallerActive()
        {
            if (!InstallerActive) throw WorkshopDiagnostics.Blocked(APIShared.Localization.UiText.Get("Workshop.InstallerInactive"));
        }

        private static void CheckStageable(IReadOnlyList<WorkshopInstallPlan> plans)
        {
            CheckInstallerActive();
            var policy = WorkshopManagerPolicy.Read(Paths.GameRootPath);
            foreach (var plan in plans)
                if (policy.Manages(plan.WorkshopId)) throw WorkshopDiagnostics.Blocked(APIShared.Localization.UiText.Get("Workshop.ManagedByManager", "Manager", policy.Manager));
            var latest = CheckStartupConditions(allowActiveMission: true);
            foreach (var plan in plans) {
                CheckPlanSubscription(plan, latest);
                if (plan.Package != null) WorkshopSubscriptionOwnership.Verify(latest, plan.WorkshopId, plan.Package);
            }
        }

        // Every container is staged first. One request then publishes the complete set.
        private static async Task<string> StagePlansAsync(IReadOnlyList<WorkshopInstallPlan> plans)
        {
            await StagingGate.WaitAsync().ConfigureAwait(false);
            try {
                var paths = Installation;
                var staged = new List<ulong>();
                try {
                    foreach (var plan in plans) {
                        WorkshopStaging.StagePlan(paths, plan, Log, request: false);
                        staged.Add(plan.WorkshopId);
                    }
                    return InstallStaging.PublishBatch(paths, staged);
                } catch {
                    foreach (ulong id in staged) {
                        try { InstallStaging.Withdraw(paths, id); }
                        catch (Exception withdraw) { WriteDiagnostic(WorkshopDiagnosticLevel.Error, "Workshop staged change of item " + id + " could not be withdrawn after a failed batch: " + withdraw.Message); }
                    }
                    throw;
                }
            } finally { StagingGate.Release(); }
        }

        // Mod Options: the confirmed preview is the consent; the next start applies it.
        internal static Task StageConfirmedAsync(WorkshopInstallPlan plan)
        {
            CheckStageable(new[] { plan });
            return Task.Run(() => StagePlansAsync(new[] { plan }));
        }

        internal static bool InstallationPending(ulong id) => InstallStaging.Pending(Installation, id);

        // Withdraws a staged but not yet applied change (Mod Options).
        internal static bool WithdrawInstallation(ulong id)
        {
            // A UI command must not wait on a worker that is still preparing a request.
            if (!StagingGate.Wait(0)) throw new InvalidOperationException("A Workshop installation is being prepared.");
            try { return InstallStaging.Withdraw(Installation, id); }
            finally { StagingGate.Release(); }
        }
    }

}
