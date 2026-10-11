using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace APIShared.Workshop
{
    /// <summary>Owner-bound Workshop service, independent of native capability readiness. Steam entry points require the Unity thread and initialized Steamworks.
    /// File preparation runs on workers. Plans are owner-bound; Apply and Publish are explicit consent boundaries. There is no automatic file repair.</summary>
    public sealed class WorkshopClient
    {
        private readonly string owner;
        internal WorkshopClient(string guid) { owner = guid; }
        /// <summary>External managed launches disable all installation side effects; upload and inspection remain available.</summary>
        public bool IsExternallyManaged => WorkshopRuntime.IsExternallyManaged;
        /// <summary>Generates a complete schema-1 manifest from author declarations and Payload on a worker. No source or installed files are changed.
        /// Pass the same draft to upload preparation, or save the returned text in the prepared folder yourself.</summary>
        public Task<string> CreateManifestAsync(string directory, WorkshopPackageDraft draft) => Task.Run(() => {
            string json = WorkshopPackageBuilder.Manifest(directory, draft);
            WorkshopBootstrapValidation.Verify(WorkshopPackageCodec.Read(json), directory); return json;
        });
        /// <summary>Validates a schema-1 package and its complete Payload inventory on a worker without changing it.</summary>
        public Task<WorkshopPackage> ValidatePackageAsync(string directory) => Task.Run(() => {
            var package = WorkshopPackageCodec.Read(System.IO.File.ReadAllText(WorkshopPaths.Inside(directory, WorkshopPackage.ManifestFileName)));
            WorkshopPaths.Verify(package, directory); WorkshopBootstrapValidation.Verify(package, directory); return package;
        });
        /// <summary>Captures the authoritative Steam subscription list on the Unity thread, then reads manifests/receipts on a worker.
        /// Incomplete content is reported, never interpreted as an unsubscribe. Exceptions indicate an unavailable catalog.</summary>
        public Task<IReadOnlyList<WorkshopCatalogItem>> GetCatalogAsync() => WorkshopRuntime.GetCatalogAsync();
        /// <summary>Prepares an immutable installation preview. Source must be the installed folder Steam reports for WorkshopId; removal requires a valid receipt.</summary>
        public Task<WorkshopInstallPlan> PrepareInstallAsync(ulong workshopId, WorkshopAction action) => WorkshopRuntime.PrepareAsync(owner, workshopId, action);
        /// <summary>Explicitly approves this owner's plan. Revalidates Steam state and prerequisites, stages on a worker and dispatches updater launch on Unity.
        /// Requires an idle game and no Extender startup restart. Successful staging does not mean installation succeeded; consult the receipt after restart.</summary>
        public Task<WorkshopOperation> ApplyAsync(WorkshopInstallPlan plan) => WorkshopRuntime.ApplyAsync(owner, plan);
        /// <summary>Creates a complete local immutable upload snapshot on a worker. Nothing is uploaded and no existing install is read as a release source.</summary>
        public Task<WorkshopUploadPlan> PrepareUploadAsync(WorkshopUploadOptions options) => Task.Run(() => {
            var plan = WorkshopPublishing.Prepare(owner, WorkshopRuntime.StateRoot, options);
            WorkshopBootstrapValidation.Verify(plan.Package, plan.Snapshot); return plan;
        });
        /// <summary>Explicitly submits this owner's snapshot. Must be called on Unity. Existing item ownership/app and remote package version are checked first.
        /// Returned operation survives view closure. Notifications occur on Unity; consumers unsubscribe their own view handlers when appropriate.</summary>
        public WorkshopOperation Publish(WorkshopUploadPlan plan) => WorkshopSteamUpload.Publish(owner, plan);
        /// <summary>Queries this account's published SHCDE items on Unity; it does not subscribe, upload or change any item.</summary>
        public Task<IReadOnlyList<WorkshopCatalogItem>> GetOwnedItemsAsync() => WorkshopSteamUpload.OwnedItems();
    }
}
