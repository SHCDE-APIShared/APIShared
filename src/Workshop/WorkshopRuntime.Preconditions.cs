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
        private static void Prerequisites(WorkshopPackage package, IReadOnlyDictionary<string, Version> endState = null, bool skipDependencies = false)
        {
            if (package == null) return;
            WorkshopDiagnostics.RejectConflicts(FindOwnershipConflicts(package.Roots, package.ModGuids));
            if (WorkshopPackageCodec.Version(APISharedPlugin.PluginVersion) < WorkshopPackageCodec.Version(package.MinimumAPISharedVersion)) throw WorkshopDiagnostics.Blocked("A newer APIShared is required.");
            if (!Chainloader.PluginInfos.TryGetValue("000shcdese", out var extender)) throw WorkshopDiagnostics.Blocked("Script Extender is unavailable.");
            System.Version actual = WorkshopPackageCodec.Version(extender.Metadata.Version.ToString());
            if (package.MinimumScriptExtenderVersion.Length > 0 && actual < WorkshopPackageCodec.Version(package.MinimumScriptExtenderVersion) ||
                package.MaximumScriptExtenderVersion.Length > 0 && actual > WorkshopPackageCodec.Version(package.MaximumScriptExtenderVersion)) throw WorkshopDiagnostics.Blocked("Script Extender version is outside this package's range.");
            if (!skipDependencies) {
                if (endState == null) {
                    var suppliedState = InstalledVersions();
                    foreach (var supplied in package.ModVersions) suppliedState[supplied.Key] = supplied.Value;
                    endState = suppliedState;
                }
                WorkshopBatch.Dependencies(package, endState);
            }
            if (package.Bootstrap != null)
            {
                string info = BootstrapReceiptPath(Paths.GameRootPath, package.Bootstrap);
                if (!File.Exists(info)) {
                    // Preserve FileNotFoundException for callers while classifying the missing bootstrap as a startup blocker.
                    var missing = new FileNotFoundException("Let Script Extender install the bootstrap and restart first.", info);
                    missing.Data["APIShared.Workshop.Blocked"] = true;
                    throw missing;
                }
                string version = ReadBootstrapVersion(info, package.Bootstrap);
                if (version == null || WorkshopPackageCodec.Version(version) < WorkshopPackageCodec.Version(package.Bootstrap.Version))
                    throw WorkshopDiagnostics.Blocked("Let Script Extender install the bootstrap and restart first.");
            }
        }
        internal static Subscription[] CheckStartupConditions(bool allowActiveMission = false)
        {
            RequireSteam();
            var reasons = new List<string>();
            if (IsExternallyManaged) reasons.Add("external management (SCDEModManagerLaunchId)");
            if (startupDownloadPending) reasons.Add("downloads were pending at Steam initialization; another startup is required: " + startupPendingItems);
            if (!allowActiveMission && MissionLifecycleService.HasContext) reasons.Add("active mission");
            if (Directory.Exists(WorkshopPaths.Inside(Paths.GameRootPath, "_SE/.staging"))) reasons.Add("Script Extender staging directory exists");
            if (reasons.Count != 0) throw WorkshopDiagnostics.Blocked(string.Join("; ", reasons));
            var subscriptions = Subscriptions();
            var pending = subscriptions.Where(s => (s.State & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0).ToArray();
            if (pending.Length != 0) throw WorkshopDiagnostics.Blocked("Workshop downloads require another startup: " + string.Join(", ", pending.Select(s => s.Id + " (" + s.State + ")")));
            return subscriptions;
        }
        internal static Dictionary<string, Version> InstalledVersions()
        {
            return WorkshopInstalledVersions.Collect(
                Chainloader.PluginInfos.Select(info => new KeyValuePair<string, string>(info.Key, info.Value.Metadata.Version.ToString())),
                GameAssetModManager.Instance.GetRegisteredAssetDirectories().Select(asset => new KeyValuePair<string, string>(asset.Key.GUID, asset.Key.Version)));
        }
        internal static void CheckBatch(IReadOnlyList<WorkshopInstallPlan> plans, IReadOnlyList<WorkshopCatalogItem> catalog)
        {
            var installed = InstalledVersions();
            var endState = WorkshopBatch.EndState(plans, installed);
            foreach (var plan in plans) Prerequisites(plan.Package, endState);
            var changed = new HashSet<ulong>(plans.Select(p => p.WorkshopId));
            bool changesInstalledVersions = installed.Any(p => !endState.TryGetValue(p.Key, out var next) || next != p.Value);
            foreach (var retained in catalog.Where(c => !changed.Contains(c.WorkshopId)))
                WorkshopBatch.Retained(retained.Package, retained.Receipt, retained.ManifestHash, endState, changesInstalledVersions);
        }
        private static void CheckBatchSubscriptions(IReadOnlyList<WorkshopInstallPlan> plans, Subscription[] original)
        {
            var latest = CheckUnchangedSubscriptions(original);
            foreach (var plan in plans) {
                CheckPlanSubscription(plan, latest);
                if (plan.Package != null) WorkshopSubscriptionOwnership.Verify(latest, plan.WorkshopId, plan.Package);
            }
        }

        private static Subscription[] CheckUnchangedSubscriptions(Subscription[] original, bool allowActiveMission = false)
        {
            var latest = CheckStartupConditions(allowActiveMission);
            if (!latest.Select(s => s.Id).OrderBy(id => id).SequenceEqual(original.Select(s => s.Id).OrderBy(id => id)))
                throw WorkshopDiagnostics.Blocked("Steam subscriptions changed; prepare a new plan.");
            return latest;
        }

        private static Subscription CheckPlanSubscription(WorkshopInstallPlan plan, Subscription[] subscriptions)
        {
            var item = subscriptions.FirstOrDefault(s => s.Id == plan.WorkshopId);
            if (plan.Action == WorkshopAction.Remove ? item != null : item == null || !Stable(item) || Path.GetFullPath(item.Root) != plan.Source)
                throw WorkshopDiagnostics.Blocked("Workshop download state changed: " + plan.PackageId);
            return item;
        }
    }
}
