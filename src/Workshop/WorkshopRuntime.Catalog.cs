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
                            entry.ManifestHash = Sha256Hex.Of(bytes).ToLowerInvariant();
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
        internal static async Task<WorkshopInstallPlan> PrepareSettingsAsync(WorkshopCatalogItem item)
        {
            if (SettingsInstallPending)
                throw WorkshopDiagnostics.Blocked(Localization.UiText.Get("Workshop.Options.RestartPending"));
            CheckStartupConditions(allowActiveMission: true);
            return await PrepareAsync(APISharedPlugin.PluginGuid, item.WorkshopId, WorkshopLocalState.ManualAction(item));
        }

        internal static async Task<(WorkshopLocalStatus Status, string Difference, string Bootstrap, IReadOnlyList<WorkshopOwnershipConflict> Conflicts)> InspectLocalAsync(WorkshopCatalogItem item)
        {
            string game = Paths.GameRootPath;
            var evidence = await Task.Run(() => {
                var state = WorkshopLocalState.Inspect(game, item);
                var receipt = item.Receipt;
                var roots = receipt == null ? item.Package?.Roots : WorkshopLocalState.ReceiptRoots(receipt);
                var guids = receipt?.ModGuids ?? (item.Package == null ? Array.Empty<string>() : WorkshopPaths.ReadDeclaredGuids(item.Package, item.ContentRoot));
                return (State: state, Roots: roots, Guids: guids, Bootstrap: BootstrapVersion(game, item.Package?.Bootstrap));
            });
            if (!UnityMainThreadDispatch.IsMainThread)
                throw new InvalidOperationException("Registered Workshop paths must be inspected on the Unity thread.");
            var conflicts = evidence.Roots == null ? Array.Empty<WorkshopOwnershipConflict>() : FindOwnershipConflicts(evidence.Roots, evidence.Guids);
            var status = conflicts.Count > 0 ? WorkshopLocalStatus.Modified : evidence.State.Status;
            return (status, evidence.State.Difference, evidence.Bootstrap, conflicts);
        }

        private static string BootstrapVersion(string game, WorkshopBootstrap bootstrap)
        {
            if (bootstrap == null) return "not-declared";
            try {
                string path = BootstrapReceiptPath(game, bootstrap);
                if (!File.Exists(path)) return "not-confirmed";
                return ReadBootstrapVersion(path, bootstrap) ?? "not-confirmed";
            } catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException ||
                                         ex is ArgumentException || ex is FormatException) {
                return "unreadable (" + ex.GetType().Name + ")";
            }
        }

        private static string BootstrapReceiptPath(string game, WorkshopBootstrap bootstrap) =>
            WorkshopPaths.Inside(game, "_SE/" + Path.GetFileNameWithoutExtension(bootstrap.MapFile) + ".json");

        private static string ReadBootstrapVersion(string path, WorkshopBootstrap bootstrap)
        {
            var row = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(path)));
            if (WorkshopPackageCodec.Text(row, "GUID") != bootstrap.Guid) return null;
            string version = WorkshopPackageCodec.Text(row, "Version");
            WorkshopPackageCodec.Version(version);
            return version;
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
        private static IReadOnlyList<WorkshopOwnershipConflict> FindOwnershipConflicts(IReadOnlyList<string> roots, IReadOnlyList<string> guids)
        {
            var conflicts = new List<WorkshopOwnershipConflict>();
            foreach (string guid in guids) {
                if (Chainloader.PluginInfos.TryGetValue(guid, out var current)) {
                    var conflict = WorkshopDiagnostics.Conflict(Paths.GameRootPath, roots, guid, current.Location, false);
                    if (conflict != null) conflicts.Add(conflict);
                }
                if (GameAssetModManager.Instance.TryGetRegisteredDirectory(guid, out string assetDirectory)) {
                    var conflict = WorkshopDiagnostics.Conflict(Paths.GameRootPath, roots, guid, assetDirectory, true);
                    if (conflict != null) conflicts.Add(conflict);
                }
            }
            return conflicts;
        }
    }
}
