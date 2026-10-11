using APIShared.Internal;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace APIShared.Workshop
{
    // Steam call-results are process-rooted, not tied to a view or BaseUnityPlugin component.
    internal static class WorkshopSteamUpload
    {
        private const uint AppId = 3024040;
        internal static WorkshopSteamBackend Backend = new WorkshopSteamBackend();
        private static readonly Dictionary<string, Upload> Active = new Dictionary<string, Upload>();
        private static readonly List<object> Queries = new List<object>();
        private sealed class Upload
        {
            internal WorkshopUploadPlan Plan;
            internal WorkshopOperation Operation;
            internal ulong Id;
            internal UGCUpdateHandle_t Handle;
            internal WorkshopSteamResult<SteamUGCQueryCompleted_t> Query;
            internal WorkshopSteamResult<CreateItemResult_t> Create;
            internal WorkshopSteamResult<SubmitItemUpdateResult_t> Submit;
            internal WorkshopSteamResult<AddUGCDependencyResult_t> Dependency;
            internal WorkshopSteamResult<RemoveUGCDependencyResult_t> RemoveDependency;
            internal Queue<ulong> Dependencies;
            internal Queue<ulong> RemovedDependencies;
            internal ulong[] PreviousDependencies = new ulong[0];
            internal ulong[] ExistingChildren = new ulong[0];
            internal bool PendingCreation;
            internal readonly List<FileStream> SnapshotLocks = new List<FileStream>();
        }
        internal static WorkshopOperation Publish(string owner, WorkshopUploadPlan plan)
        {
            WorkshopPublisherContext.RequireSteam();
            if (plan == null || plan.Owner != owner || plan.Published) throw new ArgumentException("Invalid, foreign or already submitted upload plan.");
            if (Active.Values.Any(x => x.Plan.PackageId.Equals(plan.PackageId, StringComparison.OrdinalIgnoreCase) || plan.WorkshopId != 0 && x.Id == plan.WorkshopId)) throw new InvalidOperationException("This package or Workshop item is already being published.");
            plan.Published = true;
            var upload = new Upload { Plan = plan, Id = plan.Options.WorkshopId, Operation = new WorkshopOperation(plan.OperationId) };
            Active.Add(plan.OperationId, upload);
            upload.Operation.Set(WorkshopOperationState.Preparing, "Checking immutable upload snapshot.", upload.Id);
            Worker(upload, () => {
                foreach (string file in WorkshopPaths.Enumerate(plan.Snapshot).Concat(new[] { plan.Options.PreviewFile }))
                    upload.SnapshotLocks.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (WorkshopPublishing.Fingerprint(plan.Snapshot) != plan.Fingerprint) throw new InvalidOperationException("Upload snapshot changed; prepare again.");
                WorkshopPublishing.VerifySnapshot(plan);
                string history = WorkshopPublishing.HistoryPath(WorkshopPublisherContext.StateRoot, plan.PackageId);
                if (upload.Id != 0 && File.Exists(history)) {
                    var row = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(history)));
                    upload.PendingCreation = row.TryGetValue("CreatedByPublisher", out var created) && created is bool flag && flag &&
                        WorkshopPackageCodec.Text(row, "WorkshopId") == upload.Id.ToString(CultureInfo.InvariantCulture) && WorkshopPackageCodec.Text(row, "Version", true).Length == 0;
                }
            }, () => {
                if (upload.Id == 0) Create(upload);
                else QueryExisting(upload);
            });
            return upload.Operation;
        }
        private static void Guard(Upload upload, Action action)
        {
            try { action(); }
            catch (Exception ex) { ReleaseSnapshot(upload); upload.Operation.Set(WorkshopOperationState.Failed, ex.Message, upload.Id); Active.Remove(upload.Plan.OperationId); WorkshopPublisherContext.Log("Upload failed: " + ex); }
        }
        private static void ReleaseSnapshot(Upload upload) { foreach (var file in upload.SnapshotLocks) file.Dispose(); upload.SnapshotLocks.Clear(); }
        private static void Require(bool result, string step) { if (!result) throw new IOException("Steam rejected " + step + "."); }
        private static void RequireCall(SteamAPICall_t call, string step) { if (call.m_SteamAPICall == 0) throw new IOException("Steam could not start " + step + "."); }
        private static void RequireQuery(UGCQueryHandle_t query) { if (query.m_UGCQueryHandle == ulong.MaxValue) throw new IOException("Steam could not allocate the item query."); }
        private static void Worker(Upload upload, Action work, Action next)
        {
            Task.Run(work).ContinueWith(task => {
                if (!WorkshopPublisherContext.Enqueue(() => Guard(upload, () => {
                    if (task.IsFaulted) throw task.Exception.GetBaseException();
                    next();
                }))) WorkshopPublisherContext.Log("Workshop operation cannot continue: Unity dispatcher unavailable: " + upload.Plan.OperationId);
            });
        }
        private static void QueryExisting(Upload upload)
        {
            upload.Operation.Step = "QueryExisting";
            UGCQueryHandle_t query = Backend.CreateQueryUGCDetailsRequest(new[] { new PublishedFileId_t(upload.Id) }, 1);
            RequireQuery(query);
            bool handedOff = false;
            try {
            Require(Backend.SetReturnMetadata(query, true), "query metadata");
            Require(Backend.SetReturnChildren(query, true), "query dependencies");
            upload.Query = Backend.CreateResult<SteamUGCQueryCompleted_t>((result, io) => Guard(upload, () => {
                Action validate = null;
                try {
                    if (io || result.m_eResult != EResult.k_EResultOK || result.m_unNumResultsReturned != 1) throw new IOException("Cannot verify existing item: " + result.m_eResult);
                    Require(Backend.GetQueryUGCResult(query, 0, out SteamUGCDetails_t details), "item details");
                    if (details.m_eResult != EResult.k_EResultOK || details.m_nPublishedFileId.m_PublishedFileId != upload.Id || details.m_nConsumerAppID.m_AppId != AppId || details.m_ulSteamIDOwner != Backend.GetSteamID().m_SteamID)
                        throw new InvalidOperationException("The selected item does not belong to this Steam account and SHCDE.");
                    if (!upload.Plan.Options.MetadataOnly) {
                        var children = new PublishedFileId_t[details.m_unNumChildren];
                        if (children.Length > 0) Require(Backend.GetQueryUGCChildren(query, 0, children, details.m_unNumChildren), "existing dependencies");
                        upload.ExistingChildren = children.Select(child => child.m_PublishedFileId).ToArray();
                        validate = RemoteValidation(upload, query);
                    }
                } finally { Require(Backend.ReleaseQueryUGCRequest(query), "release item query"); }
                if (validate == null) Update(upload); else Worker(upload, validate, () => Update(upload));
            }));
            var call = Backend.SendQueryUGCRequest(query); RequireCall(call, "item query"); upload.Query.Set(call);
            handedOff = true;
            } finally { if (!handedOff) Require(Backend.ReleaseQueryUGCRequest(query), "release failed item query"); }
        }
        private static Action RemoteValidation(Upload upload, UGCQueryHandle_t query)
        {
            if (Backend.GetQueryUGCMetadata(query, 0, out string json, 8192) && !string.IsNullOrEmpty(json))
            {
                return () => {
                var row = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(json));
                if (WorkshopPackageCodec.Text(row, "PackageId") != upload.Plan.PackageId) throw new InvalidDataException("Existing item's package identity differs.");
                string version = WorkshopPackageCodec.Text(row, "Version"), fingerprint = WorkshopPackageCodec.Text(row, "Fingerprint");
                if (fingerprint != upload.Plan.Fingerprint && WorkshopPackageCodec.Version(upload.Plan.Version) <= WorkshopPackageCodec.Version(version))
                    throw new InvalidOperationException("Changed content requires a higher remote package version.");
                // Keep historical ownership in remote metadata so a retry can finish
                // removing dependencies after content submission already succeeded.
                if ((row.TryGetValue("ManagedDependencies", out var ids) || row.TryGetValue("Dependencies", out ids)) && ids is System.Collections.IList list)
                    upload.PreviousDependencies = list.Cast<object>().Select(id => WorkshopPackageCodec.Id(id as string)).Distinct().ToArray();
                };
            }
            // A locally recorded CreateItem success can still be an empty item after
            // a failed first upload. Ownership/app were already verified by Steam.
            if (upload.PendingCreation) return null;
            // An old .map item has no APIShared metadata. The author must have its
            // Steam-installed content available so its actual version can be checked.
            if (!Backend.GetItemInstallInfo(new PublishedFileId_t(upload.Id), out var _, out string folder, 4096, out var _))
                throw new InvalidOperationException("Subscribe to the existing legacy item and wait for its download before migrating it.");
            var state = (EItemState)Backend.GetItemState(new PublishedFileId_t(upload.Id));
            if ((state & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0)
                throw new InvalidOperationException("Legacy item is still downloading.");
            return () => {
            string manifest = WorkshopPaths.Inside(folder, WorkshopPackage.ManifestFileName);
            if (File.Exists(manifest)) {
                var package = WorkshopPackageCodec.Read(File.ReadAllText(manifest)); WorkshopPaths.Verify(package, folder);
                if (package.PackageId != upload.Plan.PackageId || WorkshopPackageCodec.Version(upload.Plan.Version) <= WorkshopPackageCodec.Version(package.Version))
                    throw new InvalidOperationException("Existing package identity/version could not be verified through Steam metadata.");
                upload.PreviousDependencies = package.Dependencies.Where(d => d.WorkshopId.Length > 0).Select(d => WorkshopPackageCodec.Id(d.WorkshopId)).Distinct().ToArray();
                return;
            }
            string[] maps = Directory.GetFiles(folder, "*.map");
            if (maps.Length != 1 || !upload.Plan.MapOnly && (upload.Plan.Package.Bootstrap == null || Path.GetFileName(maps[0]) != upload.Plan.Package.Bootstrap.MapFile))
                throw new InvalidOperationException("Legacy migration must retain the existing single map filename.");
            using (var archive = new WorkshopMapArchive(maps[0]))
            {
                if (WorkshopPackageCodec.Text(archive.Info, "GUID") != upload.Plan.PackageId ||
                    WorkshopPackageCodec.Version(upload.Plan.Version) <= WorkshopPackageCodec.Version(WorkshopPackageCodec.Text(archive.Info, "Version")) ||
                    !upload.Plan.MapOnly && WorkshopPackageCodec.Version(upload.Plan.Package.Bootstrap.Version) <= WorkshopPackageCodec.Version(WorkshopPackageCodec.Text(archive.Info, "Version")))
                    throw new InvalidOperationException("Legacy identity/version or bootstrap migration version is not valid.");
            }
            };
        }
        private static void Create(Upload upload)
        {
            upload.Operation.Step = "CreateItem";
            upload.Create = Backend.CreateResult<CreateItemResult_t>((result, io) => Guard(upload, () => {
                if (io || result.m_eResult != EResult.k_EResultOK) throw new IOException("CreateItem failed: " + result.m_eResult);
                upload.Id = result.m_nPublishedFileId.m_PublishedFileId;
                if (upload.Id == 0) throw new IOException("CreateItem returned no Workshop ID.");
                WorkshopPublishing.Save(WorkshopPublisherContext.StateRoot, upload.Plan, upload.Id, false);
                upload.Operation.Set(WorkshopOperationState.Submitted, "Workshop ID saved; uploading content.", upload.Id);
                if (result.m_bUserNeedsToAcceptWorkshopLegalAgreement) Backend.ActivateGameOverlayToWebPage("steam://url/CommunityFilePage/" + upload.Id);
                Update(upload);
            }));
            var call = Backend.CreateItem(new AppId_t(AppId), EWorkshopFileType.k_EWorkshopFileTypeCommunity);
            RequireCall(call, "CreateItem"); upload.Create.Set(call);
        }
        private static void Update(Upload upload)
        {
            Worker(upload, () => {
                if (WorkshopInventory.Hash(upload.Plan.Options.PreviewFile) != upload.Plan.PreviewHash || WorkshopPublishing.Fingerprint(upload.Plan.Snapshot) != upload.Plan.Fingerprint)
                    throw new InvalidOperationException("Upload snapshot or preview changed after preparation; prepare again.");
            }, () => SubmitUpdate(upload));
        }
        private static void SubmitUpdate(Upload upload)
        {
            upload.Operation.Step = "PrepareItemUpdate";
            var options = upload.Plan.Options;
            ulong[] dependencies = upload.Plan.Package.Dependencies.Where(x => x.WorkshopId.Length > 0).Select(x => WorkshopPackageCodec.Id(x.WorkshopId)).Distinct().ToArray();
            if (dependencies.Contains(upload.Id)) throw new InvalidDataException("A Workshop item cannot depend on itself.");
            upload.Handle = Backend.StartItemUpdate(new AppId_t(AppId), new PublishedFileId_t(upload.Id));
            if (upload.Handle.m_UGCUpdateHandle == ulong.MaxValue) throw new IOException("Steam rejected StartItemUpdate.");
            if (!upload.Plan.PreservePresentation || upload.Plan.Options.WorkshopId == 0) {
            Require(Backend.SetItemTitle(upload.Handle, options.Title), "title");
            Require(Backend.SetItemDescription(upload.Handle, options.Description), "description");
            Require(Backend.SetItemTags(upload.Handle, options.Tags), "tags");
            Require(Backend.SetItemPreview(upload.Handle, options.PreviewFile.Replace('\\', '/')), "preview");
            ERemoteStoragePublishedFileVisibility visibility = options.Visibility == "Public" ? ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic :
                options.Visibility == "FriendsOnly" ? ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly :
                options.Visibility == "Unlisted" ? ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted : ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate;
            Require(Backend.SetItemVisibility(upload.Handle, visibility), "visibility");
            }
            if (!options.MetadataOnly) {
                Require(Backend.SetItemContent(upload.Handle, upload.Plan.Snapshot.Replace('\\', '/')), "content");
                Require(Backend.SetItemMetadata(upload.Handle, WorkshopPackageCodec.Write(new {
                    PackageId = upload.Plan.PackageId, Version = upload.Plan.Version, Fingerprint = upload.Plan.Fingerprint,
                    Dependencies = dependencies.Select(d => d.ToString(CultureInfo.InvariantCulture)).ToArray(),
                    ManagedDependencies = upload.PreviousDependencies.Union(dependencies).Select(d => d.ToString(CultureInfo.InvariantCulture)).ToArray()
                })), "package metadata");
            }
            upload.Submit = Backend.CreateResult<SubmitItemUpdateResult_t>((result, io) => Guard(upload, () => {
                if (io || result.m_eResult != EResult.k_EResultOK) throw new IOException("SubmitItemUpdate failed: " + result.m_eResult);
                upload.Submit = null; // Progress handle is no longer a running upload while dependency callbacks continue.
                if (!options.MetadataOnly) WorkshopPublishing.Save(WorkshopPublisherContext.StateRoot, upload.Plan, upload.Id, true);
                if (result.m_bUserNeedsToAcceptWorkshopLegalAgreement) Backend.ActivateGameOverlayToWebPage("https://steamcommunity.com/sharedfiles/workshoplegalagreement");
                upload.Dependencies = new Queue<ulong>(options.MetadataOnly ? new ulong[0] : dependencies.Except(upload.ExistingChildren));
                upload.RemovedDependencies = new Queue<ulong>(options.MetadataOnly ? new ulong[0] : upload.PreviousDependencies.Except(dependencies).Intersect(upload.ExistingChildren));
                AddNextDependency(upload);
            }));
            upload.Operation.Step = "SubmitItemUpdate";
            var call = Backend.SubmitItemUpdate(upload.Handle, options.ChangeNote); RequireCall(call, "SubmitItemUpdate"); upload.Submit.Set(call);
            upload.Operation.Set(WorkshopOperationState.Submitted, "Uploading to Steam.", upload.Id);
        }
        private static void AddNextDependency(Upload upload)
        {
            if (upload.RemovedDependencies.Count > 0) {
                upload.Operation.Step = "RemoveDependency";
                ulong removed = upload.RemovedDependencies.Dequeue();
                upload.RemoveDependency = Backend.CreateResult<RemoveUGCDependencyResult_t>((result, io) => Guard(upload, () => {
                    if (io || result.m_eResult != EResult.k_EResultOK) throw new IOException("Content published, but removing an old dependency failed: " + result.m_eResult);
                    AddNextDependency(upload);
                }));
                var removal = Backend.RemoveDependency(new PublishedFileId_t(upload.Id), new PublishedFileId_t(removed)); RequireCall(removal, "remove dependency"); upload.RemoveDependency.Set(removal); return;
            }
            if (upload.Dependencies.Count == 0) {
                ReleaseSnapshot(upload);
                upload.Operation.Set(WorkshopOperationState.Succeeded, "Published successfully.", upload.Id);
                Backend.ActivateGameOverlayToWebPage("steam://url/CommunityFilePage/" + upload.Id);
                Active.Remove(upload.Plan.OperationId); return;
            }
            ulong child = upload.Dependencies.Dequeue();
            upload.Operation.Step = "AddDependency";
            if (child == upload.Id) throw new InvalidDataException("A Workshop item cannot depend on itself.");
            upload.Dependency = Backend.CreateResult<AddUGCDependencyResult_t>((result, io) => Guard(upload, () => {
                if (io || result.m_eResult != EResult.k_EResultOK) throw new IOException("Content published, but dependency linking failed: " + result.m_eResult);
                AddNextDependency(upload);
            }));
            var call = Backend.AddDependency(new PublishedFileId_t(upload.Id), new PublishedFileId_t(child)); RequireCall(call, "dependency linking"); upload.Dependency.Set(call);
        }
        internal static void RefreshProgress()
        {
            WorkshopPublisherContext.RequireSteam();
            foreach (var upload in Active.Values.ToArray()) {
                if (upload.Submit == null || upload.Operation.State != WorkshopOperationState.Submitted) continue;
                var status = Backend.GetItemUpdateProgress(upload.Handle, out ulong processed, out ulong total);
                upload.Operation.Set(WorkshopOperationState.Submitted, status + ": " + processed + " / " + total + " bytes", upload.Id);
            }
        }
        internal static Task<IReadOnlyList<WorkshopCatalogItem>> OwnedItems()
        {
            WorkshopPublisherContext.RequireSteam();
            var completion = new TaskCompletionSource<IReadOnlyList<WorkshopCatalogItem>>();
            var items = new List<WorkshopCatalogItem>();
            QueryOwnedPage(1, 0, items, completion); return completion.Task;
        }
        private static void QueryOwnedPage(uint page, uint received, List<WorkshopCatalogItem> items, TaskCompletionSource<IReadOnlyList<WorkshopCatalogItem>> completion)
        {
            UGCQueryHandle_t query = Backend.CreateQueryUserUGCRequest(Backend.GetSteamID().GetAccountID(), EUserUGCList.k_EUserUGCList_Published,
                EUGCMatchingUGCType.k_EUGCMatchingUGCType_Items, EUserUGCListSortOrder.k_EUserUGCListSortOrder_CreationOrderDesc, new AppId_t(AppId), new AppId_t(AppId), page);
            RequireQuery(query);
            WorkshopSteamResult<SteamUGCQueryCompleted_t> callback = null;
            callback = Backend.CreateResult<SteamUGCQueryCompleted_t>((result, io) => {
                bool released = false;
                try {
                    if (io || result.m_eResult != EResult.k_EResultOK) throw new IOException("Owned items query failed: " + result.m_eResult);
                    for (uint i = 0; i < result.m_unNumResultsReturned; i++) {
                        Require(Backend.GetQueryUGCResult(query, i, out var details), "owned item details");
                        if (details.m_eResult != EResult.k_EResultOK) throw new IOException("Owned item details unavailable: " + details.m_eResult);
                        if (details.m_nConsumerAppID.m_AppId == AppId && details.m_ulSteamIDOwner == Backend.GetSteamID().m_SteamID)
                            items.Add(new WorkshopCatalogItem { WorkshopId = details.m_nPublishedFileId.m_PublishedFileId, Title = details.m_rgchTitle });
                    }
                    bool releaseResult = Backend.ReleaseQueryUGCRequest(query); released = true; Require(releaseResult, "release owned query");
                    if (result.m_unNumResultsReturned > 0 && received + result.m_unNumResultsReturned < result.m_unTotalMatchingResults) QueryOwnedPage(page + 1, received + result.m_unNumResultsReturned, items, completion);
                    else completion.TrySetResult(items.AsReadOnly());
                } catch (Exception ex) { if (!released && !Backend.ReleaseQueryUGCRequest(query)) WorkshopPublisherContext.Log("Failed to release owned item query."); completion.TrySetException(ex); }
                finally { Queries.Remove(callback); }
            });
            Queries.Add(callback);
            var call = Backend.SendQueryUGCRequest(query);
            if (call.m_SteamAPICall == 0) { Backend.ReleaseQueryUGCRequest(query); Queries.Remove(callback); completion.TrySetException(new IOException("Owned items query could not start.")); }
            else callback.Set(call);
        }
    }
}
