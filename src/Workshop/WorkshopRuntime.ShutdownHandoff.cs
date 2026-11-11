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
        internal static bool SettingsInstallPending => SettingsHandoff.Pending;

        private static Subscription[] CheckShutdownPlan(WorkshopInstallPlan plan, Subscription[] original, bool compareCompetingDownloads = false)
        {
            RequireSteam();
            if (!SteamUser.BLoggedOn())
                throw WorkshopDiagnostics.Blocked("Steam is offline; the queued Workshop version could not be verified. Confirm a new preview when Steam is online.");
            var latest = CheckUnchangedSubscriptions(original, allowActiveMission: true);
            var item = CheckPlanSubscription(plan, latest);
            if (item.InstalledAt != original.Single(previous => previous.Id == item.Id).InstalledAt)
                throw WorkshopDiagnostics.Blocked("Steam replaced the queued Workshop download; open Mod Options and confirm a new preview.");
            if (compareCompetingDownloads && latest.Any(current => {
                var previous = original.Single(entry => entry.Id == current.Id);
                return current.InstalledAt != previous.InstalledAt || current.State != previous.State || current.Root != previous.Root;
            }))
                throw WorkshopDiagnostics.Blocked("Steam Workshop downloads changed during handoff; confirm a new preview.");
            WorkshopTransaction.ValidateManifest(plan);
            Prerequisites(plan.Package);
            return latest;
        }

        private static void LaunchOnShutdown(WorkshopInstallPlan plan, Subscription[] confirmed, WorkshopOperation operation)
        {
            string transaction = null;
            try {
                var latest = CheckShutdownPlan(plan, confirmed);
                if (Task.Run(() => WorkshopUpdaterLauncher.Activity(plan.GameRoot)).GetAwaiter().GetResult().Busy)
                    throw WorkshopDiagnostics.Blocked("Another Workshop updater owns the installation lock; confirm a new preview once it finishes.");
                transaction = Task.Run(() => StageInstallation(plan, latest)).GetAwaiter().GetResult();
                CheckShutdownPlan(plan, latest, compareCompetingDownloads: true);
                // The process must still be alive when the updater takes ownership.
                // Its readiness loop runs on a worker and never waits on Unity.
                Task.Run(() => WorkshopUpdaterLauncher.Launch(transaction, plan.GameRoot, manualPlan: plan)).GetAwaiter().GetResult();
                CheckShutdownPlan(plan, latest, compareCompetingDownloads: true);
                Log("Workshop manual exit handoff confirmed; item=" + plan.WorkshopId + "; version=" + plan.Version +
                    "; transaction=" + transaction + ".");
            } catch (Exception ex) {
                string context = WorkshopDiagnostics.Context(plan.WorkshopId, plan.PackageId, plan.Previous?.Version, plan.Version, "manual exit handoff") +
                    "; operation=" + operation.OperationId + "; steamSource=" + plan.Source;
                WorkshopDiagnostics.Failure(WriteDiagnostic, context, ex);
                if (transaction != null) {
                    try { WorkshopUpdaterLauncher.Cancel(transaction); }
                    catch (Exception cancelFailure) { WorkshopDiagnostics.Failure(WriteDiagnostic, context + "; cancellation", cancelFailure); }
                }
                operation.Failure = ex;
                operation.Set(WorkshopOperationState.Failed, ex.Message, plan.WorkshopId);
            }
        }
    }
}
