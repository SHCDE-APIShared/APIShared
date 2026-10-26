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
        private static string startupPendingItems;
        private static int steamFailureReported;
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
                    var pending = InitialSubscriptions().Where(x => (x.State & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0).ToArray();
                    startupDownloadPending = pending.Length != 0;
                    startupPendingItems = string.Join(", ", pending.Select(x => x.Id + " (" + x.State + ")"));
                    Log("Workshop startup gate active on Steam initialization; external management=" + IsExternallyManaged + ".");
                    WorkshopStartup.Run();
                    UnityMainThreadDispatch.TryEnqueue(() => Log("Workshop runtime active after startup cleanup; external management=" + IsExternallyManaged + "."));
                } catch (Exception ex) {
                    if (System.Threading.Interlocked.Exchange(ref steamFailureReported, 1) != 0) return;
                    WorkshopDiagnostics.Failure(WriteDiagnostic, "Workshop step=Steam initialization", ex);
                    WorkshopDiagnostics.Outcome(WriteDiagnostic, "failed", false, 1);
                }
            });
        }
        internal static void Log(string text) => NativeApiLog.Info(logger, text);
        internal static void WriteDiagnostic(WorkshopDiagnosticLevel level, string text)
        {
            // Failure reports are stamped by RuntimeHealth; an own unbracketed stamp would
            // make every identical repeat a new cause and defeat its repeat throttling.
            if (level == WorkshopDiagnosticLevel.Error) NativeApiLog.Error(logger, text);
            else if (level == WorkshopDiagnosticLevel.Warning) NativeApiLog.Warning(logger, text);
            else NativeApiLog.Info(logger, text);
        }
        internal static void RequireSteam()
        {
            if (!UnityMainThreadDispatch.IsMainThread) throw new InvalidOperationException("Workshop Steam calls require the Unity thread.");
            var type = typeof(SteamManager);
            object instance = type.GetField("s_instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            if (instance == null || !(type.GetField("m_bInitialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance) is bool ready) || !ready)
                throw new InvalidOperationException("Steamworks is not initialized.");
        }
        internal sealed class Subscription { internal ulong Id; internal string Root; internal EItemState State; }
        // Startup runs once per session: a list that changes while Steam is still
        // enumerating it must not disable startup Workshop handling for the session.
        private static Subscription[] InitialSubscriptions()
        {
            for (int attempt = 1; ; attempt++) {
                try { return Subscriptions(); }
                catch (IOException) when (attempt < 3) { }
            }
        }
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
                catch (Exception ex) { catalog.Add(new WorkshopCatalogItem { Status = APIShared.Localization.UiText.Get("Workshop.Catalog.ReceiptDiagnostic") + ex.Message, Title = APIShared.Localization.UiText.Get("Workshop.Catalog.InstallationState"), DiagnosticError = ex }); }
                foreach (var item in snapshot)
                {
                    receipts.TryGetValue(item.Id, out var receipt);
                    string manifest = item.Root == null ? null : WorkshopPaths.Inside(item.Root, WorkshopPackage.ManifestFileName);
                    if (manifest != null && Stable(item) && !File.Exists(manifest) && receipt == null) continue; // SE-only maps stay outside our catalog.
                    var entry = new WorkshopCatalogItem { WorkshopId = item.Id, Title = receipt?.PackageId ?? item.Id.ToString(), ContentRoot = item.Root,
                        InstalledVersion = receipt?.Version, Receipt = receipt, IsSubscribed = true, Status = APIShared.Localization.UiText.Get("Workshop.Catalog.DownloadOrInstallationInformationUnavailable") };
                    try {
                        if (Stable(item) && manifest != null && File.Exists(manifest)) {
                            byte[] bytes = File.ReadAllBytes(manifest);
                            using (var sha = System.Security.Cryptography.SHA256.Create())
                                entry.ManifestHash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                            using (var reader = new StreamReader(new MemoryStream(bytes))) entry.Package = WorkshopPackageCodec.Read(reader.ReadToEnd());
                            entry.Title = entry.Package.PackageId;
                            entry.CanInstall = !IsExternallyManaged;
                            entry.Status = IsExternallyManaged ? APIShared.Localization.UiText.Get("Workshop.ExternallyManaged") : receipt == null ? APIShared.Localization.UiText.Get("Workshop.Catalog.InstallationAdoptionAvailable") :
                                WorkshopPackageCodec.Version(entry.Package.Version) > WorkshopPackageCodec.Version(receipt.Version) ? APIShared.Localization.UiText.Get("Workshop.Catalog.UpdateAvailable") : APIShared.Localization.UiText.Get("Workshop.Catalog.ReceiptCurrentLocalFilesAreNot");
                        }
                    } catch (Exception ex) { entry.Status = ex.Message; entry.DiagnosticError = ex; }
                    catalog.Add(entry); receipts.Remove(item.Id);
                }
                foreach (var receipt in receipts.Values) catalog.Add(new WorkshopCatalogItem { WorkshopId = WorkshopPackageCodec.Id(receipt.WorkshopId), Title = receipt.PackageId,
                    InstalledVersion = receipt.Version, Status = IsExternallyManaged ? APIShared.Localization.UiText.Get("Workshop.ExternallyManaged") : APIShared.Localization.UiText.Get("Workshop.Catalog.UnsubscribedRemovalAvailable"), IsSubscribed = false });
                return catalog.AsReadOnly();
            });
        }
        internal static Task<WorkshopInstallPlan> PrepareAsync(string owner, ulong id, WorkshopAction action) => PrepareCoreAsync(owner, id, action, false);
        internal static Task<WorkshopInstallPlan> PrepareStartupAsync(ulong id, WorkshopAction action) => PrepareCoreAsync(APISharedPlugin.PluginGuid, id, action, true);
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
        private static async Task<WorkshopInstallPlan> PrepareCoreAsync(string owner, ulong id, WorkshopAction action, bool startup)
        {
            var snapshot = Subscriptions(); var subscription = snapshot.FirstOrDefault(x => x.Id == id);
            if (action == WorkshopAction.Remove ? subscription != null : subscription == null || !Stable(subscription))
                throw WorkshopDiagnostics.Blocked("Subscription/download state does not allow this action.");
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
            var conflicts = new List<WorkshopOwnershipConflict>();
            foreach (string guid in package.ModGuids) {
                if (Chainloader.PluginInfos.TryGetValue(guid, out var current)) {
                    var conflict = WorkshopDiagnostics.Conflict(Paths.GameRootPath, package.Roots, guid, current.Location, false);
                    if (conflict != null) conflicts.Add(conflict);
                }
                if (GameAssetModManager.Instance.TryGetRegisteredDirectory(guid, out string assetDirectory)) {
                    var conflict = WorkshopDiagnostics.Conflict(Paths.GameRootPath, package.Roots, guid, assetDirectory, true);
                    if (conflict != null) conflicts.Add(conflict);
                }
            }
            WorkshopDiagnostics.RejectConflicts(conflicts);
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
                string info = WorkshopPaths.Inside(Paths.GameRootPath, "_SE/" + Path.GetFileNameWithoutExtension(package.Bootstrap.MapFile) + ".json");
                if (!File.Exists(info)) {
                    // Preserve FileNotFoundException for callers while classifying the missing bootstrap as a startup blocker.
                    var missing = new FileNotFoundException("Let Script Extender install the bootstrap and restart first.", info);
                    missing.Data["APIShared.Workshop.Blocked"] = true;
                    throw missing;
                }
                var row = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(info)));
                if (WorkshopPackageCodec.Text(row, "GUID") != package.Bootstrap.Guid || WorkshopPackageCodec.Version(WorkshopPackageCodec.Text(row, "Version")) < WorkshopPackageCodec.Version(package.Bootstrap.Version))
                    throw WorkshopDiagnostics.Blocked("Let Script Extender install the bootstrap and restart first.");
            }
        }
        internal static void CheckStartupConditions()
        {
            RequireSteam();
            var reasons = new List<string>();
            if (IsExternallyManaged) reasons.Add("external management (SCDEModManagerLaunchId)");
            if (startupDownloadPending) reasons.Add("downloads were pending at Steam initialization; another startup is required: " + startupPendingItems);
            if (MissionLifecycleService.HasContext) reasons.Add("active mission");
            if (Directory.Exists(WorkshopPaths.Inside(Paths.GameRootPath, "_SE/.staging"))) reasons.Add("Script Extender staging directory exists");
            if (reasons.Count != 0) throw WorkshopDiagnostics.Blocked(string.Join("; ", reasons));
            var pending = Subscriptions().Where(s => (s.State & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0).ToArray();
            if (pending.Length != 0) throw WorkshopDiagnostics.Blocked("Workshop downloads require another startup: " + string.Join(", ", pending.Select(s => s.Id + " (" + s.State + ")")));
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
                throw WorkshopDiagnostics.Blocked("Steam subscriptions changed during batch preparation.");
            foreach (var plan in plans) {
                var item = latest.FirstOrDefault(s => s.Id == plan.WorkshopId);
                if (plan.Action == WorkshopAction.Remove ? item != null : item == null || !Stable(item) || Path.GetFullPath(item.Root) != plan.Source)
                    throw WorkshopDiagnostics.Blocked("Workshop download state changed: " + plan.PackageId);
                if (plan.Package != null) WorkshopSubscriptionOwnership.Verify(latest, plan.WorkshopId, plan.Package);
            }
        }
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
        internal static async Task<WorkshopOperation> ApplyAsync(string owner, WorkshopInstallPlan plan)
        {
            RequireSteam();
            if (plan == null || plan.Owner != owner || plan.GameRoot != Path.GetFullPath(Paths.GameRootPath)) throw new ArgumentException("This client does not own the plan.");
            if (IsExternallyManaged) throw new InvalidOperationException(APIShared.Localization.UiText.Get("Workshop.ExternallyManaged"));
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
    }
}
