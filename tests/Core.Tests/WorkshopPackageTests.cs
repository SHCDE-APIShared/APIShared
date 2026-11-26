using APIShared.Workshop;
using APIShared.Workshop.Install;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

// Package planning and the complete installation path: plan -> WorkshopStaging (plugin) ->
// InstallSwap (preloader) -> receipt. Core swap/recovery invariants: WorkshopSafeInstallTests.
[TestClass]
public sealed class WorkshopPackageTests
{
    private string fixture = null!;
    private string Game => Path.Combine(fixture, "game");
    private string Source => Path.Combine(fixture, "source");
    private const string Root = "BepInEx/plugins/Example";
    [TestInitialize] public void Initialize() { fixture = Path.Combine(Path.GetTempPath(), "ApiLooseWorkshop-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Game); }
    [TestCleanup] public void Cleanup() { Assert.IsTrue(Path.GetFullPath(fixture).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)); Directory.Delete(fixture, true); }
    private void Write(string directory, string path, string text) { string target = WorkshopPaths.Inside(directory, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, text); }
    private string Read(string path) { string target = WorkshopPaths.Inside(Game, path); return File.Exists(target) ? File.ReadAllText(target) : "<missing>"; }
    private WorkshopPackage Package(string version = "1.0.0", params string[] extras) =>
        PackageWithId("ExamplePackage", version, extras);

    private WorkshopPackage PackageWithId(string packageId, string version, params string[] extras)
    {
        Write(Source, "Payload/" + Root + "/info.json", "{\"GUID\":\"ExampleGuid\",\"Version\":\"" + version + "\",\"Manifest\":1}");
        Write(Source, "Payload/" + Root + "/Example.dll", "payload " + version);
        foreach (string extra in extras) Write(Source, "Payload/" + Root + "/" + extra, "shipped " + extra);
        var files = WorkshopPaths.Enumerate(Path.Combine(Source, "Payload")).Select(path => new WorkshopFile(path.Substring(Path.Combine(Source, "Payload").Length + 1).Replace('\\', '/'), new FileInfo(path).Length, WorkshopInventory.Hash(path))).ToArray();
        Write(Source, WorkshopPackage.ManifestFileName, WorkshopPackageCodec.Write(new { SchemaVersion = 1, PackageId = packageId, Version = version, MinimumAPISharedVersion = "0.7.1", Roots = new[] { Root }, Files = files }));
        var package = WorkshopPackageCodec.Read(File.ReadAllText(Path.Combine(Source, WorkshopPackage.ManifestFileName))); WorkshopPaths.Verify(package, Source); return package;
    }
    // Removes a shipped file from the source payload (the next Package() call rebuilds the manifest).
    private void Drop(string relative) => File.Delete(WorkshopPaths.Inside(Source, "Payload/" + Root + "/" + relative));
    // What the plugin (staging) and the next start's preloader (swap) do for a confirmed plan.
    private string Install(WorkshopInstallPlan plan)
    {
        var paths = new InstallPaths(Game);
        WorkshopStaging.StagePlan(paths, plan, _ => { });
        Assert.IsTrue(InstallStaging.Pending(paths, plan.WorkshopId));
        return InstallSwap.Apply(paths, plan.WorkshopId, _ => { });
    }
    private WorkshopInstallPlan Plan(WorkshopAction action, ulong id = 123) => WorkshopInstallation.Prepare("owner", Game, id, action == WorkshopAction.Remove ? null! : Source, action);

    [TestMethod]
    public void StartupStagingRejectsANewerLocalVersionAfterTheOffer()
    {
        var package = PackageWithId("ExampleGuid", "1.0.38");
        Install(Plan(WorkshopAction.Install));
        var receipt = WorkshopInstallation.ReadReceipt(Game, 123);
        Write(Game, Root + "/Example.dll", "changed payload");
        var item = new WorkshopCatalogItem { Package = package, Receipt = receipt, IsSubscribed = true, CanInstall = true };
        var offer = WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(Game, item).Status,
            true, localVersion: WorkshopLocalState.ReadInstalledPackageVersion(Game, package));
        Assert.AreEqual(WorkshopAction.Reinstall, offer);
        var plan = Plan(offer!.Value);
        Write(Game, Root + "/info.json", WorkshopPackageCodec.Write(new { GUID = "ExampleGuid", Version = "1.0.39" }));

        var paths = new InstallPaths(Game);
        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => WorkshopStaging.StagePlan(paths, plan, _ => { }, request: false, startup: true));
        Assert.AreEqual(WorkshopDiagnosticLevel.Warning, WorkshopDiagnostics.Level(error));
        StringAssert.Contains(error.Message, "local=1.0.39");
        Assert.IsFalse(Directory.Exists(paths.Container(123)));
        Assert.IsFalse(InstallStaging.Pending(paths, 123));
        Assert.AreEqual("1.0.38", WorkshopInstallation.ReadReceipt(Game, 123).Version);

        WorkshopStaging.StagePlan(paths, plan, _ => { });
        Assert.IsTrue(InstallStaging.Pending(paths, 123), "an explicit manual replacement keeps its existing behavior");
    }

    [TestMethod] public void LegacyReleasesShareValidationButStillRejectUnsafeEntries()
    {
        // Repeated legacy paths and hashes reuse one successful validation; rejections are unchanged.
        var file = new WorkshopFile(Root + "/info.json", 1, new string('A', 64));
        string Manifest(WorkshopFile[] legacy) => WorkshopPackageCodec.Write(new { SchemaVersion = 1, PackageId = "ExamplePackage", Version = "1.1.0",
            MinimumAPISharedVersion = "0.7.1", Roots = new[] { Root }, Files = new[] { file },
            LegacyReleases = new[] { new { Version = "1.0.0", Files = legacy }, new { Version = "0.9.0", Files = new[] { file } } } });
        var package = WorkshopPackageCodec.Read(Manifest(new[] { file }));
        Assert.AreEqual(2, package.LegacyReleases.Count);
        Assert.AreEqual(file.Path, package.LegacyReleases[1].Files[0].Path);
        Assert.AreEqual(new string('a', 64), package.LegacyReleases[1].Files[0].Sha256, "a remembered hash keeps its normalized form");
        Assert.ThrowsExactly<InvalidDataException>(() => WorkshopPackageCodec.Read(Manifest(new[] { file, new WorkshopFile(Root + "/config/x.json", 1, new string('a', 64)) })));
        Assert.ThrowsExactly<InvalidDataException>(() => WorkshopPackageCodec.Read(Manifest(new[] { new WorkshopFile(Root + "/info.json", 1, new string('z', 64)) })));
    }

    [TestMethod] public void FirstInstallationWritesPackageAndReceiptAtTheNextStart()
    {
        Package();
        var plan = Plan(WorkshopAction.Install);
        var paths = new InstallPaths(Game);
        WorkshopStaging.StagePlan(paths, plan, _ => { });
        Assert.AreEqual("<missing>", Read(Root + "/Example.dll"), "staging never touches BepInEx/plugins");
        Assert.IsNull(WorkshopInstallation.ReadReceipt(Game, 123));
        Assert.AreEqual("install", InstallSwap.Apply(paths, 123, _ => { }));
        Assert.AreEqual("payload 1.0.0", Read(Root + "/Example.dll"));
        Assert.AreEqual("1.0.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
        Assert.IsFalse(InstallStaging.Pending(paths, 123));
        Assert.IsFalse(Directory.Exists(paths.Container(123)));
    }

    [TestMethod] public void ReceiptControlsUpdatesDespiteChangedOrMissingDll()
    {
        Package(); Install(Plan(WorkshopAction.Install)); Write(Game, Root + "/Example.dll", "local changes");
        Assert.ThrowsExactly<InvalidOperationException>(() => Plan(WorkshopAction.Update));
        File.Delete(WorkshopPaths.Inside(Game, Root + "/Example.dll"));
        Assert.ThrowsExactly<InvalidOperationException>(() => Plan(WorkshopAction.Update));
        var plan = Plan(WorkshopAction.Reinstall);
        Assert.AreEqual(2, plan.Files.Count);
        Install(plan);
        Assert.AreEqual("payload 1.0.0", Read(Root + "/Example.dll"));
    }

    // Plan semantics of the folder swap: everything in the root that the new package neither
    // delivers nor keeps disappears, including files a player added without a keep entry.
    [TestMethod] public void UpdateRemovesUnlistedFilesAndKeepsListedPlayerFiles()
    {
        Package("1.0.0", "obsolete.dll", "apishared-keep.txt"); Write(Source, "Payload/" + Root + "/apishared-keep.txt", "config/\n");
        Package("1.0.0", "obsolete.dll");
        Install(Plan(WorkshopAction.Install));
        Write(Game, Root + "/unknown.txt", "unknown"); Write(Game, Root + "/config/settings.json", "player settings");
        Drop("obsolete.dll"); Package("1.1.0");
        var plan = Plan(WorkshopAction.Update);
        CollectionAssert.AreEquivalent(new[] { Root + "/obsolete.dll", Root + "/unknown.txt" }, plan.RemovedFiles.ToArray());
        Install(plan);
        Assert.AreEqual("payload 1.1.0", Read(Root + "/Example.dll"));
        Assert.AreEqual("<missing>", Read(Root + "/obsolete.dll"));
        Assert.AreEqual("<missing>", Read(Root + "/unknown.txt"));
        Assert.AreEqual("player settings", Read(Root + "/config/settings.json"));
        Assert.AreEqual("1.1.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
    }

    // A release that moves its plugin into another root: the old root must not stay installed
    // (it would keep loading), and the preview names its files.
    [TestMethod] public void UpdateThatDropsARootRemovesItAndPreviewsIt()
    {
        Package(); Install(Plan(WorkshopAction.Install));
        const string moved = "BepInEx/plugins/ExampleMoved";
        Directory.Delete(Path.Combine(Source, "Payload"), true);
        Write(Source, "Payload/" + moved + "/info.json", "{\"GUID\":\"ExampleGuid\",\"Version\":\"1.1.0\",\"Manifest\":1}");
        Write(Source, "Payload/" + moved + "/Example.dll", "payload 1.1.0");
        var files = WorkshopPaths.Enumerate(Path.Combine(Source, "Payload")).Select(path => new WorkshopFile(path.Substring(Path.Combine(Source, "Payload").Length + 1).Replace('\\', '/'), new FileInfo(path).Length, WorkshopInventory.Hash(path))).ToArray();
        Write(Source, WorkshopPackage.ManifestFileName, WorkshopPackageCodec.Write(new { SchemaVersion = 1, PackageId = "ExamplePackage", Version = "1.1.0", MinimumAPISharedVersion = "0.7.1", Roots = new[] { moved }, Files = files }));
        var plan = Plan(WorkshopAction.Update);
        CollectionAssert.AreEquivalent(new[] { Root }, WorkshopStaging.DroppedRoots(plan).ToArray());
        CollectionAssert.AreEquivalent(new[] { Root + "/Example.dll", Root + "/info.json" }, plan.RemovedFiles.ToArray());
        Install(plan);
        Assert.IsFalse(Directory.Exists(WorkshopPaths.Inside(Game, Root)));
        Assert.AreEqual("payload 1.1.0", Read(moved + "/Example.dll"));
        Assert.AreEqual("1.1.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
    }

    // A shipped default listed in the keep list belongs to the player after installation; the
    // receipt omits it so a changed setting never makes the installation look damaged.
    [TestMethod] public void KeptShippedFilesAreNotPartOfTheReceiptCheck()
    {
        Package("1.0.0", "settings.json", "apishared-keep.txt"); Write(Source, "Payload/" + Root + "/apishared-keep.txt", "settings.json\n");
        var package = Package("1.0.0", "settings.json");
        Install(Plan(WorkshopAction.Install));
        Write(Game, Root + "/settings.json", "player choice");
        var receipt = WorkshopInstallation.ReadReceipt(Game, 123);
        Assert.IsFalse(receipt.Files.Any(f => f.Path.EndsWith("/settings.json")));
        Assert.AreEqual(WorkshopLocalStatus.Verified, WorkshopLocalState.InspectReceipt(Game, receipt).Status);
        Assert.AreEqual(package.Files.Count - 1, receipt.Files.Count);
    }

    [TestMethod] public void ChangedPayloadInvalidatesPlanAndNothingIsStaged()
    {
        Package(); var plan = Plan(WorkshopAction.Install);
        Write(Source, "Payload/" + Root + "/Example.dll", "different");
        Assert.ThrowsExactly<InvalidDataException>(() => WorkshopStaging.StagePlan(new InstallPaths(Game), plan, _ => { }));
        Assert.IsFalse(InstallStaging.Pending(new InstallPaths(Game), 123));
        Assert.AreEqual("<missing>", Read(Root + "/Example.dll"));
    }

    [TestMethod]
    public void AQueuedPlanCannotStageAReplacedSteamDownloadAtTheSamePath()
    {
        Package("1.0.0"); Install(Plan(WorkshopAction.Install));
        Package("1.1.0");
        var plan = Plan(WorkshopAction.Update);
        WorkshopStaging.ValidateManifest(plan);
        Package("1.2.0");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopStaging.StagePlan(new InstallPaths(Game), plan, _ => { }));
        Assert.AreEqual(WorkshopDiagnosticLevel.Warning, WorkshopDiagnostics.Level(error));
        Assert.IsFalse(InstallStaging.Pending(new InstallPaths(Game), 123));
        Assert.AreEqual("1.0.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
        Assert.AreEqual("payload 1.0.0", Read(Root + "/Example.dll"));
    }

    [TestMethod] public void RetainedDependenciesUseReceiptsAndDeferUnknownEvidenceSafely()
    {
        var package = Package();
        package.Dependencies = new[] { new WorkshopDependency("Uninstalled", "2.0.0") };
        var state = new Dictionary<string, Version> { ["Installed"] = new Version(1, 0, 0, 0) };
        WorkshopBatch.Retained(package, null!, null!, state, true);
        var receipt = new WorkshopReceipt { PackageId=package.PackageId, Version=package.Version, ManifestHash="a" };
        WorkshopBatch.Retained(package, receipt, "b", state, false);
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopBatch.Retained(package, receipt, "b", state, true));
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopBatch.Retained(package, receipt, "a", state, true));
        receipt.Dependencies = new[] { new WorkshopDependency("Installed", "1.0.0", "1.0.0") };
        WorkshopBatch.Retained(package, receipt, "b", state, true);
        state["Installed"] = new Version(2, 0, 0, 0);
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopBatch.Retained(package, receipt, "b", state, true));
    }

    [TestMethod] public void BatchInstallsTwoPackagesAndKeepsDependencyEndState()
    {
        Package();
        var first = Plan(WorkshopAction.Install);
        string secondSource = Source + "-second";
        Directory.Move(Source, secondSource);
        foreach (string path in Directory.EnumerateFiles(secondSource, "*", SearchOption.AllDirectories)) {
            string relative = path.Substring(secondSource.Length + 1).Replace('\\', '/');
            Write(Source, relative.Replace("Example", "Second"), File.ReadAllText(path).Replace("Example", "Second"));
        }
        Write(Source, WorkshopPackage.ManifestFileName, WorkshopPackageBuilder.Manifest(Source, new WorkshopPackageDraft("SecondPackage", "1.0.0", "0.7.1")));
        Directory.Move(Source, Source + "-saved"); Directory.Move(secondSource, Source);
        var second = WorkshopInstallation.Prepare("owner", Game, 456, Source + "-saved", WorkshopAction.Install);
        Install(first); Install(second);
        Assert.AreEqual("1.0.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
        Assert.AreEqual("1.0.0", WorkshopInstallation.ReadReceipt(Game, 456).Version);
        Assert.IsNotNull(WorkshopInstallation.ReadReceipt(Game, 456).Dependencies);
        var state = WorkshopBatch.EndState(new[] { first, second }, new Dictionary<string, Version>());
        Assert.AreEqual(new Version(1, 0, 0, 0), state["SecondGuid"]);
        var dependent = WorkshopPackageCodec.Read(File.ReadAllText(Path.Combine(Source, WorkshopPackage.ManifestFileName)));
        dependent.Dependencies = new[] { new WorkshopDependency("SecondGuid", "1.0.0") };
        WorkshopBatch.Dependencies(dependent, state);
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopBatch.Dependencies(dependent, new Dictionary<string, Version>()));
    }

    // Uninstallation removes the whole plugin folder and the receipt; only persist entries are
    // parked and come back when the package is installed again.
    [TestMethod] public void RemovalDeletesTheRootAndParksPersistEntriesForReinstallation()
    {
        Package("1.0.0", "apishared-keep.txt"); Write(Source, "Payload/" + Root + "/apishared-keep.txt", "config/ persist\nnotes.txt\n");
        Package("1.0.0");
        Install(Plan(WorkshopAction.Install));
        Write(Game, Root + "/config/settings.json", "player settings"); Write(Game, Root + "/notes.txt", "player notes");
        var removal = Plan(WorkshopAction.Remove);
        CollectionAssert.Contains(removal.RemovedFiles.ToArray(), Root + "/notes.txt");
        Assert.AreEqual("remove", Install(removal));
        Assert.IsFalse(Directory.Exists(WorkshopPaths.Inside(Game, Root)));
        Assert.IsNull(WorkshopInstallation.ReadReceipt(Game, 123));
        Install(Plan(WorkshopAction.Install));
        Assert.AreEqual("player settings", Read(Root + "/config/settings.json"));
        Assert.AreEqual("<missing>", Read(Root + "/notes.txt"));
        Assert.IsFalse(Directory.Exists(new InstallPaths(Game).Park("ExamplePackage", Root)), "parking is cleared after reinstallation");
    }

    [TestMethod] public void OwnershipOfAnotherInstalledPackageIsRejected()
    {
        Package(); Install(Plan(WorkshopAction.Install));
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopInstallation.Prepare("owner", Game, 456, Source, WorkshopAction.Install));
        Directory.Move(WorkshopPaths.Inside(Source, "Payload/" + Root), WorkshopPaths.Inside(Source, "Payload/BepInEx/plugins/Different"));
        Write(Source, WorkshopPackage.ManifestFileName, File.ReadAllText(Path.Combine(Source, WorkshopPackage.ManifestFileName)).Replace(Root, "BepInEx/plugins/Different").Replace("ExamplePackage", "DifferentPackage"));
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopInstallation.Prepare("owner", Game, 456, Source, WorkshopAction.Install));
    }

    [TestMethod] public void SnapshotExcludesOldArchivesAndPersistsFirstUploadId()
    {
        Package(); Write(Source, "preview.png", "preview"); Write(Source, "old.zip", "stale");
        var options = new WorkshopUploadOptions { SourceDirectory = Source, PreviewFile = Path.Combine(Source, "preview.png"), Title = "Example", Description = "Example release" };
        string state = WorkshopInstallation.StateRoot(Game); var plan = WorkshopPublishing.Prepare("owner", state, options);
        Assert.IsFalse(plan.Files.Contains("old.zip")); Assert.IsFalse(plan.Files.Contains("preview.png"));
        WorkshopPublishing.Save(state, plan, 321, false); var retry = WorkshopPublishing.Prepare("owner", state, options); Assert.AreEqual(321UL, retry.WorkshopId);
        WorkshopPublishing.Save(state, retry, 321, true); File.AppendAllText(Path.Combine(Source, WorkshopPackage.ManifestFileName), " ");
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopPublishing.Prepare("owner", state, options));
        options.WorkshopId = 321; options.MetadataOnly = true; Assert.IsTrue(WorkshopPublishing.Prepare("owner", state, options).Options.MetadataOnly);
    }

    [TestMethod]
    [DataRow("../escape")][DataRow("BepInEx/plugins/APIShared_Serp/a.dll")][DataRow("BepInEx/plugins/000shcdese/a.dll")][DataRow("BepInEx/plugins/Example/config/a.json")][DataRow("BepInEx/plugins/Example/a.dll:stream")]
    public void UnsafeOrInfrastructureTargetsAreRejected(string path) => Assert.ThrowsExactly<InvalidDataException>(() => WorkshopPaths.DeliveredPath(path));

    [TestMethod] public void CorruptOwnReceiptAllowsAnExplicitCleanInstallation()
    {
        Package(); Write(Game, Root + "/unknown.dll", "stale"); Write(Game, "_APIShared/Workshop/Receipts/123.json", "broken JSON");
        Assert.ThrowsExactly<InvalidDataException>(() => Plan(WorkshopAction.Update));
        var plan = Plan(WorkshopAction.Install);
        Assert.IsTrue(plan.Diagnostics.Any(d => d.Contains("Invalid receipt")));
        CollectionAssert.Contains(plan.RemovedFiles.ToArray(), Root + "/unknown.dll");
        Install(plan);
        Assert.AreEqual("<missing>", Read(Root + "/unknown.dll"));
        Assert.AreEqual("1.0.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
    }

    [TestMethod] public void NewAuthorDraftBuildsAnInventoryWithoutChangingSource()
    {
        Package(); File.Delete(Path.Combine(Source, WorkshopPackage.ManifestFileName)); Write(Source, "preview.png", "preview");
        var draft = new WorkshopPackageDraft("NewAuthor", "2.0.0", "0.7.1");
        var plan = WorkshopPublishing.Prepare("new-author", WorkshopInstallation.StateRoot(Game), new WorkshopUploadOptions {
            SourceDirectory = Source, PreviewFile = Path.Combine(Source, "preview.png"), Title = "New package", Description = "New author", Draft = draft });
        Assert.AreEqual("NewAuthor", plan.PackageId); Assert.IsFalse(File.Exists(Path.Combine(Source, WorkshopPackage.ManifestFileName)));
        Assert.IsTrue(File.Exists(Path.Combine(plan.Snapshot, WorkshopPackage.ManifestFileName)));
    }

    [TestMethod] public void ExtraPayloadAndUndeclaredMapAreRejected()
    {
        var package = Package(); Write(Source, "Payload/" + Root + "/extra.dll", "extra"); Assert.ThrowsExactly<InvalidDataException>(() => WorkshopPaths.Verify(package, Source));
        File.Delete(WorkshopPaths.Inside(Source, "Payload/" + Root + "/extra.dll")); Write(Source, "Extra.map", "old archive"); Assert.ThrowsExactly<InvalidDataException>(() => WorkshopPaths.Verify(package, Source));
    }

    [TestMethod]
    [DataRow("1.0.23", true)][DataRow("1.0.2", true)][DataRow("1.0.23", false)]
    public void DirectVersionJumpNeedsNoIntermediateRelease(string version, bool receipted)
    {
        var old = Package(version);
        if (receipted) Install(Plan(WorkshopAction.Install));
        else foreach (var file in old.Files) Write(Game, file.Path, File.ReadAllText(WorkshopPaths.Inside(Source, "Payload/" + file.Path)));
        Package("1.0.27");
        Install(Plan(receipted ? WorkshopAction.Update : WorkshopAction.Install));
        Assert.AreEqual("1.0.27", WorkshopInstallation.ReadReceipt(Game, 123).Version);
        Assert.AreEqual("payload 1.0.27", Read(Root + "/Example.dll"));
    }

    // A file held open in the installed folder (e.g. by a scanner) defers the whole change: the
    // old version and receipt stay, the staged request waits for the next start.
    [TestMethod] public void LockedInstalledFileDefersTheChangeAndLeavesTheOldVersion()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows file sharing semantics."); return; }
        Package(); Install(Plan(WorkshopAction.Install)); Package("1.1.0");
        var paths = new InstallPaths(Game);
        WorkshopStaging.StagePlan(paths, Plan(WorkshopAction.Update), _ => { });
        bool deferred = false;
        using (new FileStream(WorkshopPaths.Inside(Game, Root + "/info.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            try { InstallSwap.Apply(paths, 123, _ => { }); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { deferred = true; }
        Assert.IsTrue(deferred);
        Assert.AreEqual("payload 1.0.0", Read(Root + "/Example.dll"));
        Assert.AreEqual("1.0.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
        Assert.IsTrue(InstallStaging.Pending(paths, 123));
        Assert.AreEqual("install", InstallSwap.Apply(paths, 123, _ => { }));
        Assert.AreEqual("1.1.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
    }
}
