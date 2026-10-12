using APIShared.Internal;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.API.Components.ModManager;
using SHCDESE.EventAPI;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Version = System.Version;

namespace APIShared.Workshop
{
    internal static class WorkshopRuntime
    {
        private static IDisposable steamSubscription;
        private static ManualLogSource logger;
        private static bool startupDownloadPending;
        private static readonly WorkshopApplyCoordinator ApplyCoordinator = new WorkshopApplyCoordinator();
        internal static string StateRoot => WorkshopInstallation.StateRoot(Paths.GameRootPath);
        internal static bool IsExternallyManaged => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCDEModManagerLaunchId"));
        internal static void Initialize(BaseUnityPlugin host, ManualLogSource log)
        {
            logger = log;
            WorkshopPublisherContext.RequireSteam = RequireSteam;
            WorkshopPublisherContext.Enqueue = UnityMainThreadDispatch.TryEnqueue;
            WorkshopPublisherContext.Log = Log;
            WorkshopPublisherContext.StateRoot = StateRoot;
            steamSubscription = SteamworksR3EventHooks.OnSteamworksInitialized.Observable.Subscribe(e => {
                if (e.Phase != EventHookPhase.Post) return;
                try {
                    startupDownloadPending = Subscriptions().Any(x => (x.State & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0);
                    Log("Workshop startup gate active on Steam initialization; external management=" + IsExternallyManaged + ".");
                    WorkshopStartup.Run();
                    UnityMainThreadDispatch.TryEnqueue(() => Log("Workshop runtime active after startup cleanup; external management=" + IsExternallyManaged + "."));
                } catch (Exception ex) { Log("Steam catalog unavailable: " + ex.Message); }
            });
        }
        internal static void Log(string text) => logger?.LogInfo(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text);
        internal static void RequireSteam()
        {
            if (!UnityMainThreadDispatch.IsMainThread) throw new InvalidOperationException("Workshop Steam calls require the Unity thread.");
            var type = typeof(SteamManager);
            object instance = type.GetField("s_instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            if (instance == null || !(type.GetField("m_bInitialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance) is bool ready) || !ready)
                throw new InvalidOperationException("Steamworks is not initialized.");
        }
        internal sealed class Subscription { internal ulong Id; internal string Root; internal EItemState State; }
        internal static Subscription[] Subscriptions()
        {
            RequireSteam(); uint count = SteamUGC.GetNumSubscribedItems();
            var ids = new PublishedFileId_t[count];
            if (SteamUGC.GetSubscribedItems(ids, count) != count || SteamUGC.GetNumSubscribedItems() != count) throw new IOException("Steam subscription list changed; retry.");
            return ids.Select(id => {
                var state = (EItemState)SteamUGC.GetItemState(id);
                string folder = null;
                if ((state & EItemState.k_EItemStateInstalled) != 0 && SteamUGC.GetItemInstallInfo(id, out var _, out string root, 4096, out var _)) folder = root;
                return new Subscription { Id = id.m_PublishedFileId, Root = folder, State = state };
            }).ToArray();
        }
        internal static bool Stable(Subscription item) => item.Root != null && Directory.Exists(item.Root) &&
            (item.State & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending | EItemState.k_EItemStateDisabledLocally)) == 0;
        internal static Task<IReadOnlyList<WorkshopCatalogItem>> GetCatalogAsync()
        {
            var snapshot = Subscriptions(); string game = Paths.GameRootPath;
            return Task.Run<IReadOnlyList<WorkshopCatalogItem>>(() => {
                var catalog = new List<WorkshopCatalogItem>();
                var receipts = new Dictionary<ulong, WorkshopReceipt>();
                try { foreach (var receipt in WorkshopInstallation.Receipts(game)) receipts.Add(WorkshopPackageCodec.Id(receipt.WorkshopId), receipt); }
                catch (Exception ex) { catalog.Add(new WorkshopCatalogItem { Status = "Receipt diagnostic: " + ex.Message, Title = "Installation state" }); }
                foreach (var item in snapshot)
                {
                    receipts.TryGetValue(item.Id, out var receipt);
                    string manifest = item.Root == null ? null : WorkshopPaths.Inside(item.Root, WorkshopPackage.ManifestFileName);
                    if (manifest != null && Stable(item) && !File.Exists(manifest) && receipt == null) continue; // SE-only maps stay outside our catalog.
                    var entry = new WorkshopCatalogItem { WorkshopId = item.Id, Title = receipt?.PackageId ?? item.Id.ToString(), ContentRoot = item.Root,
                        InstalledVersion = receipt?.Version, Receipt = receipt, IsSubscribed = true, Status = "Download or installation information unavailable" };
                    try {
                        if (Stable(item) && manifest != null && File.Exists(manifest)) {
                            byte[] bytes = File.ReadAllBytes(manifest);
                            using (var sha = System.Security.Cryptography.SHA256.Create())
                                entry.ManifestHash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                            using (var reader = new StreamReader(new MemoryStream(bytes))) entry.Package = WorkshopPackageCodec.Read(reader.ReadToEnd());
                            entry.Title = entry.Package.PackageId;
                            entry.CanInstall = !IsExternallyManaged;
                            entry.Status = IsExternallyManaged ? WorkshopText.External : receipt == null ? "Installation / adoption available" :
                                WorkshopPackageCodec.Version(entry.Package.Version) > WorkshopPackageCodec.Version(receipt.Version) ? "Update available" : "Receipt current; local files are not inspected";
                        }
                    } catch (Exception ex) { entry.Status = ex.Message; }
                    catalog.Add(entry); receipts.Remove(item.Id);
                }
                foreach (var receipt in receipts.Values) catalog.Add(new WorkshopCatalogItem { WorkshopId = WorkshopPackageCodec.Id(receipt.WorkshopId), Title = receipt.PackageId,
                    InstalledVersion = receipt.Version, Status = IsExternallyManaged ? WorkshopText.External : "Unsubscribed; removal available", IsSubscribed = false });
                return catalog.AsReadOnly();
            });
        }
        internal static Task<WorkshopInstallPlan> PrepareAsync(string owner, ulong id, WorkshopAction action) => PrepareCoreAsync(owner, id, action, false);
        internal static Task<WorkshopInstallPlan> PrepareStartupAsync(ulong id, WorkshopAction action) => PrepareCoreAsync(APISharedPlugin.PluginGuid, id, action, true);
        private static async Task<WorkshopInstallPlan> PrepareCoreAsync(string owner, ulong id, WorkshopAction action, bool startup)
        {
            var snapshot = Subscriptions(); var subscription = snapshot.FirstOrDefault(x => x.Id == id);
            if (action == WorkshopAction.Remove ? subscription != null : subscription == null || !Stable(subscription))
                throw new InvalidOperationException("Subscription/download state does not allow this action.");
            string game = Paths.GameRootPath, source = subscription?.Root;
            var plan = await Task.Run(() => {
                var candidate = WorkshopInstallation.Prepare(owner, game, id, source, action);
                if (candidate.Package != null) {
                    WorkshopBootstrapValidation.Verify(candidate.Package, source);
                    WorkshopSubscriptionOwnership.Verify(snapshot, id, candidate.Package);
                }
                return candidate;
            });
            var completion = new TaskCompletionSource<WorkshopInstallPlan>();
            if (!UnityMainThreadDispatch.TryRunInlineOrEnqueue(() => {
                try { Prerequisites(plan.Package, null, startup); completion.SetResult(plan); } catch (Exception ex) { completion.SetException(ex); }
            })) throw new InvalidOperationException("Unity dispatcher unavailable; plan prerequisites could not be checked.");
            return await completion.Task;
        }
        private static void Prerequisites(WorkshopPackage package, IReadOnlyDictionary<string, Version> endState = null, bool skipDependencies = false)
        {
            if (package == null) return;
            foreach (string guid in package.ModGuids) {
                if (Chainloader.PluginInfos.TryGetValue(guid, out var current)) {
                    string path = Path.GetFullPath(current.Location);
                    if (!package.Roots.Any(root => path.StartsWith(WorkshopPaths.Inside(Paths.GameRootPath, root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("An installed plugin outside this payload already owns GUID: " + guid);
                }
                if (GameAssetModManager.Instance.TryGetRegisteredDirectory(guid, out string assetDirectory)) {
                    string path = Path.GetFullPath(assetDirectory).TrimEnd(Path.DirectorySeparatorChar);
                    if (!package.Roots.Any(root => path.Equals(WorkshopPaths.Inside(Paths.GameRootPath, root), StringComparison.OrdinalIgnoreCase) || path.StartsWith(WorkshopPaths.Inside(Paths.GameRootPath, root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("An installed asset mod outside this payload already owns GUID: " + guid);
                }
            }
            if (WorkshopPackageCodec.Version(APISharedPlugin.PluginVersion) < WorkshopPackageCodec.Version(package.MinimumAPISharedVersion)) throw new InvalidOperationException("A newer APIShared is required.");
            if (!Chainloader.PluginInfos.TryGetValue("000shcdese", out var extender)) throw new InvalidOperationException("Script Extender is unavailable.");
            System.Version actual = WorkshopPackageCodec.Version(extender.Metadata.Version.ToString());
            if (package.MinimumScriptExtenderVersion.Length > 0 && actual < WorkshopPackageCodec.Version(package.MinimumScriptExtenderVersion) ||
                package.MaximumScriptExtenderVersion.Length > 0 && actual > WorkshopPackageCodec.Version(package.MaximumScriptExtenderVersion)) throw new InvalidOperationException("Script Extender version is outside this package's range.");
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
                string info = WorkshopPaths.Inside(Paths.GameRootPath, "_SE/" + Path.GetFileNameWithoutExtension(package.Bootstrap.MapFile) + ".json");
                var row = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(info)));
                if (WorkshopPackageCodec.Text(row, "GUID") != package.Bootstrap.Guid || WorkshopPackageCodec.Version(WorkshopPackageCodec.Text(row, "Version")) < WorkshopPackageCodec.Version(package.Bootstrap.Version))
                    throw new InvalidOperationException("Let Script Extender install the bootstrap and restart first.");
            }
        }
        internal static void CheckStartupConditions()
        {
            RequireSteam();
            if (IsExternallyManaged || startupDownloadPending || MissionLifecycleService.HasContext || Directory.Exists(WorkshopPaths.Inside(Paths.GameRootPath, "_SE/.staging")))
                throw new InvalidOperationException("External management, an active mission or Extender startup work takes precedence.");
            if (Subscriptions().Any(s => (s.State & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0))
                throw new InvalidOperationException("Workshop downloads require another startup.");
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
            CheckStartupConditions();
            var latest = Subscriptions();
            if (!latest.Select(s => s.Id).OrderBy(id => id).SequenceEqual(original.Select(s => s.Id).OrderBy(id => id)))
                throw new InvalidOperationException("Steam subscriptions changed during batch preparation.");
            foreach (var plan in plans) {
                var item = latest.FirstOrDefault(s => s.Id == plan.WorkshopId);
                if (plan.Action == WorkshopAction.Remove ? item != null : item == null || !Stable(item) || Path.GetFullPath(item.Root) != plan.Source)
                    throw new InvalidOperationException("Workshop download state changed: " + plan.PackageId);
                if (plan.Package != null) WorkshopSubscriptionOwnership.Verify(latest, plan.WorkshopId, plan.Package);
            }
        }
        internal static async Task<WorkshopOperation> ApplyStartupAsync(IReadOnlyList<WorkshopInstallPlan> plans, IReadOnlyList<WorkshopCatalogItem> catalog)
        {
            CheckStartupConditions(); CheckBatch(plans, catalog);
            var snapshot = Subscriptions();
            CheckBatchSubscriptions(plans, snapshot);
            string id = Guid.NewGuid().ToString("N");
            return await ApplyCoordinator.ExecuteAsync(id, async operation => {
                string transaction = await Task.Run(() => {
                    foreach (var plan in plans.Where(p => p.Package != null)) {
                        WorkshopBootstrapValidation.Verify(plan.Package, plan.Source);
                        WorkshopSubscriptionOwnership.Verify(snapshot, plan.WorkshopId, plan.Package);
                    }
                    return WorkshopTransaction.StageBatch(plans, id);
                });
                var completion = new TaskCompletionSource<WorkshopOperation>();
                if (!UnityMainThreadDispatch.TryRunInlineOrEnqueue(async () => {
                    try {
                        CheckBatchSubscriptions(plans, snapshot); CheckBatch(plans, catalog);
                        await WorkshopUpdaterLauncher.Launch(transaction, Paths.GameRootPath);
                        CheckBatchSubscriptions(plans, snapshot); CheckBatch(plans, catalog);
                        operation.Set(WorkshopOperationState.AwaitingRestart, "Confirmed startup batch; receipts commit after game exit.");
                        UnityEngine.Application.Quit(); completion.TrySetResult(operation);
                    } catch (Exception ex) {
                        try { WorkshopUpdaterLauncher.Cancel(transaction); } catch (Exception cancel) { Log("Batch cancellation failed: " + cancel.Message); }
                        operation.Set(WorkshopOperationState.Failed, ex.Message); completion.TrySetResult(operation);
                    }
                })) throw new InvalidOperationException("Unity dispatcher unavailable; batch was not applied.");
                return await completion.Task;
            });
        }
        internal static async Task<WorkshopOperation> ApplyAsync(string owner, WorkshopInstallPlan plan)
        {
            RequireSteam();
            if (plan == null || plan.Owner != owner || plan.GameRoot != Path.GetFullPath(Paths.GameRootPath)) throw new ArgumentException("This client does not own the plan.");
            if (IsExternallyManaged) throw new InvalidOperationException(WorkshopText.External);
            if (startupDownloadPending || Directory.Exists(WorkshopPaths.Inside(Paths.GameRootPath, "_SE/.staging"))) throw new InvalidOperationException("Extender startup work takes precedence. Restart before applying this plan.");
            if (MissionLifecycleService.HasContext) throw new InvalidOperationException("Return to the menu before applying Workshop changes.");
            var subscriptions = Subscriptions(); var subscription = subscriptions.FirstOrDefault(x => x.Id == plan.WorkshopId);
            if (subscriptions.Any(x => (x.State & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0)) throw new InvalidOperationException("Let Extender finish pending downloads and restart first.");
            if (plan.Action == WorkshopAction.Remove ? subscription != null : subscription == null || !Stable(subscription) || Path.GetFullPath(subscription.Root) != plan.Source)
                throw new InvalidOperationException("Steam state changed; prepare again.");
            Prerequisites(plan.Package);
            return await ApplyCoordinator.ExecuteAsync(plan.OperationId, async operation =>
            {
                string transaction = await Task.Run(() => {
                    if (plan.Package != null) {
                        WorkshopBootstrapValidation.Verify(plan.Package, plan.Source);
                        WorkshopSubscriptionOwnership.Verify(subscriptions, plan.WorkshopId, plan.Package);
                    }
                    return WorkshopTransaction.Stage(plan);
                });
                var completion = new TaskCompletionSource<WorkshopOperation>();
                if (!UnityMainThreadDispatch.TryEnqueue(async () => {
                    try {
                        if (IsExternallyManaged || startupDownloadPending || MissionLifecycleService.HasContext || Directory.Exists(WorkshopPaths.Inside(Paths.GameRootPath, "_SE/.staging"))) throw new InvalidOperationException("Installation conditions changed.");
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
                            WorkshopUpdaterLauncher.Cancel(transaction); throw new InvalidOperationException("Installation conditions changed before game exit.");
                        }
                        operation.Set(WorkshopOperationState.AwaitingRestart, "Updater started; installation is committed only after game exit.", plan.WorkshopId);
                        UnityEngine.Application.Quit();
                        completion.TrySetResult(operation);
                    } catch (Exception ex) {
                        try { WorkshopUpdaterLauncher.Cancel(transaction); } catch (Exception cancelFailure) { Log("Updater handoff cancellation failed: " + cancelFailure.Message); }
                        operation.Set(WorkshopOperationState.Failed, ex.Message); completion.TrySetResult(operation);
                    }
                })) throw new InvalidOperationException("Unity dispatcher unavailable; staged transaction was not applied.");
                return await completion.Task;
            });
        }
    }
}
