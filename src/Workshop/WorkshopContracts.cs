using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace APIShared.Workshop
{
    /// <summary>Explicit package actions. Automatic discovery only offers newer releases; repair and downgrade require a separate selection.</summary>
    public enum WorkshopAction { /// <summary>Install or explicitly adopt an unreceipted package.</summary>
        Install, /// <summary>Apply a newer receipted release.</summary>
        Update, /// <summary>Explicitly replace delivered files regardless of local modifications.</summary>
        Reinstall, /// <summary>Explicitly choose an older release.</summary>
        Downgrade, /// <summary>Remove unchanged receipt-owned files after unsubscribe.</summary>
        Remove }

    /// <summary>Process-owned operation state. Closing a view does not cancel an already submitted Steam request.</summary>
    public enum WorkshopOperationState { /// <summary>Preparing or checking the operation.</summary>
        Preparing, /// <summary>A preview awaits explicit consent.</summary>
        Ready, /// <summary>A Steam request is in flight.</summary>
        Submitted, /// <summary>The remote operation completed.</summary>
        Succeeded, /// <summary>The diagnostic explains the failed step.</summary>
        Failed, /// <summary>The updater awaits game exit; inspect its journal and receipt after restart.</summary>
        AwaitingRestart }

    /// <summary>A delivered immutable file. Path is relative to the game root, uses forward slashes and is restricted to declared plugin folders.</summary>
    public sealed class WorkshopFile
    {
        /// <summary>Creates a file record; full validation occurs when preparing a package.</summary>
        public WorkshopFile(string path, long size, string sha256) { Path = path; Size = size; Sha256 = sha256; }
        /// <summary>Relative forward-slash delivered path under a declared plugin root.</summary>
        public string Path { get; }
        /// <summary>Delivered byte count.</summary>
        public long Size { get; }
        /// <summary>Lowercase SHA-256 of the delivered bytes.</summary>
        public string Sha256 { get; }
    }

    /// <summary>An installed dependency. WorkshopId is a navigation hint, never evidence that the dependency is installed.</summary>
    public sealed class WorkshopDependency
    {
        /// <summary>Declares a required GUID with optional inclusive version bounds and a decimal Steam item hint.</summary>
        public WorkshopDependency(string guid, string minimumVersion = "", string maximumVersion = "", string workshopId = "") { Guid = guid; MinimumVersion = minimumVersion; MaximumVersion = maximumVersion; WorkshopId = workshopId; }
        internal WorkshopDependency() { }
        /// <summary>Stable mod GUID; independent of a Workshop folder name.</summary>
        public string Guid { get; internal set; }
        /// <summary>Inclusive minimum numeric version, or empty for no bound.</summary>
        public string MinimumVersion { get; internal set; }
        /// <summary>Inclusive maximum numeric version, or empty for no bound.</summary>
        public string MaximumVersion { get; internal set; }
        /// <summary>Steam-issued item identity; zero or empty means unavailable.</summary>
        public string WorkshopId { get; internal set; }
    }

    /// <summary>The SE-owned warning package in a mixed item. Its target folder is excluded from APIShared ownership.</summary>
    public sealed class WorkshopBootstrap
    {
        /// <summary>Declares a separately built warning map. Full preparation also inspects its archive contents.</summary>
        public WorkshopBootstrap(string mapFile, string guid, string version, string pluginFolder) { MapFile = mapFile; Guid = guid; Version = version; PluginFolder = pluginFolder; }
        internal WorkshopBootstrap() { }
        /// <summary>Retained top-level .map filename managed exclusively by Script Extender.</summary>
        public string MapFile { get; internal set; }
        /// <summary>Stable mod GUID; independent of a Workshop folder name.</summary>
        public string Guid { get; internal set; }
        /// <summary>Three- or four-part numeric release version.</summary>
        public string Version { get; internal set; }
        /// <summary>Exclusive BepInEx/plugins folder reserved for the bootstrap.</summary>
        public string PluginFolder { get; internal set; }
    }

    /// <summary>Audited historical delivery used only for hash-matching obsolete files during migration.</summary>
    public sealed class WorkshopLegacyRelease
    {
        /// <summary>Declares an audited historical delivery; callers must supply hashes from verified released bytes.</summary>
        public WorkshopLegacyRelease(string version, IReadOnlyList<WorkshopFile> files) { Version = version; Files = new List<WorkshopFile>(files).AsReadOnly(); }
        internal WorkshopLegacyRelease() { }
        /// <summary>Three- or four-part numeric release version.</summary>
        public string Version { get; internal set; }
        /// <summary>Complete immutable delivered file inventory; settings and unknown local files are excluded.</summary>
        public IReadOnlyList<WorkshopFile> Files { get; internal set; }
    }

    /// <summary>Validated schema-1 declaration. Files are the complete contents below Payload; the manifest is separate from each mod's info.json.</summary>
    public sealed class WorkshopPackage
    {
        internal IReadOnlyDictionary<string, Version> ModVersions;
        /// <summary>Top-level package declaration filename; distinct from mod info.json and older inventories.</summary>
        public const string ManifestFileName = "apishared-workshop.json";
        /// <summary>Stable package identity retained across releases and migration.</summary>
        public string PackageId { get; internal set; }
        /// <summary>Three- or four-part numeric release version.</summary>
        public string Version { get; internal set; }
        /// <summary>Inclusive minimum APIShared version.</summary>
        public string MinimumAPISharedVersion { get; internal set; }
        /// <summary>Inclusive minimum Extender version, or empty.</summary>
        public string MinimumScriptExtenderVersion { get; internal set; }
        /// <summary>Inclusive maximum Extender version, or empty.</summary>
        public string MaximumScriptExtenderVersion { get; internal set; }
        /// <summary>Exclusive target plugin folders relative to the game root.</summary>
        public IReadOnlyList<string> Roots { get; internal set; }
        /// <summary>Complete immutable delivered file inventory; settings and unknown local files are excluded.</summary>
        public IReadOnlyList<WorkshopFile> Files { get; internal set; }
        /// <summary>Required installed GUIDs and inclusive version ranges; Steam links are only hints.</summary>
        public IReadOnlyList<WorkshopDependency> Dependencies { get; internal set; }
        /// <summary>Distinct GUIDs from delivered info.json files, populated by full payload validation.</summary>
        public IReadOnlyList<string> ModGuids { get; internal set; }
        /// <summary>Verified historical inventories used only to remove hash-matching obsolete files.</summary>
        public IReadOnlyList<WorkshopLegacyRelease> LegacyReleases { get; internal set; }
        /// <summary>Optional exclusively Extender-owned bootstrap declaration.</summary>
        public WorkshopBootstrap Bootstrap { get; internal set; }
    }

    /// <summary>One Steam subscription or installed receipt. WorkshopId comes from Steam, not the directory name. Status explains unavailable content and external ownership.</summary>
    public sealed class WorkshopCatalogItem
    {
        /// <summary>Steam-issued item identity; zero or empty means unavailable.</summary>
        public ulong WorkshopId { get; internal set; }
        /// <summary>Human-readable item or upload title.</summary>
        public string Title { get; internal set; }
        /// <summary>Installed content folder reported by Steam, or null when unavailable.</summary>
        public string ContentRoot { get; internal set; }
        /// <summary>Remote package declaration, or null when unavailable.</summary>
        public WorkshopPackage Package { get; internal set; }
        /// <summary>Successfully applied receipt version, or null without a receipt.</summary>
        public string InstalledVersion { get; internal set; }
        /// <summary>Discovery diagnostic; unavailable content never implies unsubscribe.</summary>
        public string Status { get; internal set; }
        /// <summary>Whether the authoritative Steam subscription snapshot contains the item.</summary>
        public bool IsSubscribed { get; internal set; }
        /// <summary>Whether discovered content is ready for planning and external ownership permits installation.</summary>
        public bool CanInstall { get; internal set; }
    }

    /// <summary>Owner-bound immutable preview; only its creating Workshop client may apply it. Revalidation occurs before staging. Nothing is installed by preparation.</summary>
    public sealed class WorkshopInstallPlan
    {
        internal string Owner, GameRoot, Source, ManifestHash, ReceiptHash;
        internal WorkshopReceipt Previous;
        internal WorkshopPackage Package;
        internal IReadOnlyDictionary<string, string> TargetHashes;
        internal WorkshopInstallPlan() { }
        /// <summary>Unique local operation identity used for snapshots, transactions and diagnostics.</summary>
        public string OperationId { get; internal set; }
        /// <summary>Steam-issued item identity; zero or empty means unavailable.</summary>
        public ulong WorkshopId { get; internal set; }
        /// <summary>Explicitly selected installation action.</summary>
        public WorkshopAction Action { get; internal set; }
        /// <summary>Stable package identity retained across releases and migration.</summary>
        public string PackageId { get; internal set; }
        /// <summary>Three- or four-part numeric release version.</summary>
        public string Version { get; internal set; }
        /// <summary>Preparation warnings to show before asking the user to apply.</summary>
        public IReadOnlyList<string> Diagnostics { get; internal set; }
        /// <summary>Delivered files to replace after confirmation; installed versions are backed up first.</summary>
        public IReadOnlyList<WorkshopFile> Files { get; internal set; }
        /// <summary>Known obsolete files eligible for hash-guarded removal; unknown and modified files are preserved.</summary>
        public IReadOnlyList<string> RemovedFiles { get; internal set; }
    }

    /// <summary>Observable process-lifetime operation. Changed notifications are dispatched on the Unity thread; callbacks are isolated and are not replayed.</summary>
    public sealed class WorkshopOperation
    {
        private readonly object gate = new object();
        private WorkshopOperationState state;
        private string diagnostic;
        private ulong itemId;
        internal string Step = "Preparation";
        internal WorkshopOperation(string id) { OperationId = id; state = WorkshopOperationState.Preparing; }
        /// <summary>Unique local operation identity used for snapshots, transactions and diagnostics.</summary>
        public string OperationId { get; }
        /// <summary>Latest thread-safe operation state.</summary>
        public WorkshopOperationState State { get { lock (gate) return state; } }
        /// <summary>Latest thread-safe step or failure description.</summary>
        public string Diagnostic { get { lock (gate) return diagnostic ?? ""; } }
        /// <summary>Steam-issued item identity; zero or empty means unavailable.</summary>
        public ulong WorkshopId { get { lock (gate) return itemId; } }
        /// <summary>Unity-thread notifications without replay; observer exceptions are isolated. Unsubscribe view-owned observers when no longer needed.</summary>
        public event Action<WorkshopOperation> Changed;
        internal void Set(WorkshopOperationState value, string message, ulong id = 0)
        {
            lock (gate) { state = value; diagnostic = message; if (id != 0) itemId = id; }
            Notify();
        }
        internal void Notify()
        {
            var handlers = Changed;
            if (handlers == null) return;
            foreach (Action<WorkshopOperation> handler in handlers.GetInvocationList())
                try { handler(this); } catch { /* One consumer must not stop another observer. */ }
        }
    }

    /// <summary>Upload form. Copying into a prepared snapshot freezes these values; changing this object afterwards does not change the upload.</summary>
    public sealed class WorkshopUploadOptions
    {
        /// <summary>Optional declarations for a new package or regenerated inventory. Null uses the existing complete manifest.</summary>
        public WorkshopPackageDraft Draft { get; set; }
        /// <summary>Prepared folder containing the package manifest and complete Payload.</summary>
        public string SourceDirectory { get; set; }
        /// <summary>Steam-issued item identity; zero or empty means unavailable.</summary>
        public ulong WorkshopId { get; set; }
        /// <summary>Human-readable item or upload title.</summary>
        public string Title { get; set; }
        /// <summary>Workshop description supplied by the publisher.</summary>
        public string Description { get; set; }
        /// <summary>Local preview image copied into the immutable snapshot.</summary>
        public string PreviewFile { get; set; }
        /// <summary>Steam update note; empty is allowed.</summary>
        public string ChangeNote { get; set; }
        /// <summary>Public, FriendsOnly, Private or Unlisted.</summary>
        public string Visibility { get; set; } = "Private";
        /// <summary>Steam tags copied during preparation; defaults to Mod.</summary>
        public string[] Tags { get; set; } = new[] { "Mod" };
        /// <summary>Update title, description, preview and visibility without replacing content or its release version.</summary>
        public bool MetadataOnly { get; set; }
    }

    /// <summary>Immutable author declarations. Preparation generates Files from Payload without changing the selected source or an installed mod.</summary>
    public sealed class WorkshopPackageDraft
    {
        /// <summary>Creates author declarations; lists are copied and full validation occurs during preparation.</summary>
        public WorkshopPackageDraft(string packageId, string version, string minimumAPISharedVersion, string minimumScriptExtenderVersion = "", string maximumScriptExtenderVersion = "",
            IReadOnlyList<WorkshopDependency> dependencies = null, WorkshopBootstrap bootstrap = null, IReadOnlyList<WorkshopLegacyRelease> legacyReleases = null)
        {
            PackageId = packageId; Version = version; MinimumAPISharedVersion = minimumAPISharedVersion;
            MinimumScriptExtenderVersion = minimumScriptExtenderVersion; MaximumScriptExtenderVersion = maximumScriptExtenderVersion;
            Dependencies = new List<WorkshopDependency>(dependencies ?? new WorkshopDependency[0]).AsReadOnly(); Bootstrap = bootstrap;
            LegacyReleases = new List<WorkshopLegacyRelease>(legacyReleases ?? new WorkshopLegacyRelease[0]).AsReadOnly();
        }
        /// <summary>Stable package identity.</summary>
        public string PackageId { get; }
        /// <summary>Numeric release version.</summary>
        public string Version { get; }
        /// <summary>Inclusive manager minimum.</summary>
        public string MinimumAPISharedVersion { get; }
        /// <summary>Inclusive Extender minimum, or empty.</summary>
        public string MinimumScriptExtenderVersion { get; }
        /// <summary>Inclusive Extender maximum, or empty.</summary>
        public string MaximumScriptExtenderVersion { get; }
        /// <summary>Required mod declarations.</summary>
        public IReadOnlyList<WorkshopDependency> Dependencies { get; }
        /// <summary>Optional independently packaged warning bootstrap.</summary>
        public WorkshopBootstrap Bootstrap { get; }
        /// <summary>Verified previous deliveries for conservative migration cleanup.</summary>
        public IReadOnlyList<WorkshopLegacyRelease> LegacyReleases { get; }
    }

    /// <summary>Frozen upload snapshot. Preparation is local; Publish is the only method that sends content to Steam.</summary>
    public sealed class WorkshopUploadPlan
    {
        internal bool MapOnly;
        internal bool PreservePresentation;
        internal string Owner, Snapshot, Fingerprint, PreviewHash;
        internal WorkshopPackage Package;
        internal WorkshopUploadOptions Options;
        internal bool Published;
        /// <summary>Unique local operation identity used for snapshots, transactions and diagnostics.</summary>
        public string OperationId { get; internal set; }
        /// <summary>Stable package identity retained across releases and migration.</summary>
        public string PackageId => Package.PackageId;
        /// <summary>Three- or four-part numeric release version.</summary>
        public string Version => Package.Version;
        /// <summary>Steam-issued item identity; zero or empty means unavailable.</summary>
        public ulong WorkshopId => Options.WorkshopId;
        /// <summary>Complete immutable delivered file inventory; settings and unknown local files are excluded.</summary>
        public IReadOnlyList<string> Files { get; internal set; }
    }

    internal sealed class WorkshopReceipt
    {
        public int SchemaVersion { get; set; } = 1;
        /// <summary>Steam-issued item identity; zero or empty means unavailable.</summary>
        public string WorkshopId { get; set; }
        /// <summary>Stable package identity retained across releases and migration.</summary>
        public string PackageId { get; set; }
        /// <summary>Three- or four-part numeric release version.</summary>
        public string Version { get; set; }
        public string ManifestHash { get; set; }
        public string TransactionId { get; set; }
        /// <summary>Complete immutable delivered file inventory; settings and unknown local files are excluded.</summary>
        public IReadOnlyList<WorkshopFile> Files { get; set; }
        public IReadOnlyList<string> ModGuids { get; set; }
    }
}
