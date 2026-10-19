using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;

namespace APIShared.Core.Tests;

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
    private WorkshopPackage Package(string version = "1.0.0", params string[] extras)
    {
        Write(Source, "Payload/" + Root + "/info.json", "{\"GUID\":\"ExampleGuid\",\"Version\":\"" + version + "\",\"Manifest\":1}");
        Write(Source, "Payload/" + Root + "/Example.dll", "payload " + version);
        foreach (string extra in extras) Write(Source, "Payload/" + Root + "/" + extra, "old " + extra);
        var files = WorkshopPaths.Enumerate(Path.Combine(Source, "Payload")).Select(path => new WorkshopFile(path.Substring(Path.Combine(Source, "Payload").Length + 1).Replace('\\', '/'), new FileInfo(path).Length, WorkshopInventory.Hash(path))).ToArray();
        Write(Source, WorkshopPackage.ManifestFileName, WorkshopPackageCodec.Write(new { SchemaVersion = 1, PackageId = "ExamplePackage", Version = version, MinimumAPISharedVersion = "0.7.1", Roots = new[] { Root }, Files = files }));
        var package = WorkshopPackageCodec.Read(File.ReadAllText(Path.Combine(Source, WorkshopPackage.ManifestFileName))); WorkshopPaths.Verify(package, Source); return package;
    }
    private void InstallReceipt(WorkshopPackage package, ulong id = 123)
    {
        foreach (var file in package.Files) Write(Game, file.Path, File.ReadAllText(WorkshopPaths.Inside(Source, "Payload/" + file.Path)));
        Write(Game, "_APIShared/Workshop/Receipts/" + id + ".json", WorkshopPackageCodec.Write(new WorkshopReceipt { WorkshopId = id.ToString(), PackageId = package.PackageId, Version = package.Version,
            Files = package.Files, ModGuids = package.ModGuids, ManifestHash = WorkshopInventory.Hash(Path.Combine(Source, WorkshopPackage.ManifestFileName)), TransactionId = Guid.NewGuid().ToString("N") }));
    }
    [TestMethod] public void ReceiptControlsUpdatesDespiteChangedOrMissingDll()
    {
        var package = Package(); InstallReceipt(package); Write(Game, Root + "/Example.dll", "local changes");
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Update));
        File.Delete(WorkshopPaths.Inside(Game, Root + "/Example.dll"));
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Update));
        var plan = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Reinstall);
        Assert.AreEqual(2, plan.Files.Count); Assert.IsFalse(File.Exists(WorkshopPaths.Inside(Game, Root + "/Example.dll")));
        Assert.AreEqual("1.0.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
    }
    [TestMethod] public void UpdatePreservesUnknownSettingsAndModifiedObsoleteFiles()
    {
        var old = Package("1.0.0", "obsolete.dll", "modified.dll"); InstallReceipt(old);
        Write(Game, Root + "/modified.dll", "user changed"); Write(Game, Root + "/unknown.txt", "unknown"); Write(Game, Root + "/config/settings.json", "settings");
        File.Delete(WorkshopPaths.Inside(Source, "Payload/" + Root + "/obsolete.dll")); File.Delete(WorkshopPaths.Inside(Source, "Payload/" + Root + "/modified.dll")); Package("1.1.0");
        var plan = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Update);
        CollectionAssert.AreEqual(new[] { Root + "/obsolete.dll" }, plan.RemovedFiles.ToArray()); Assert.IsTrue(plan.Diagnostics.Any(x => x.Contains("modified.dll")));
        string stage = WorkshopTransaction.Stage(plan); string operations = File.ReadAllText(Path.Combine(stage, "operations.tsv"));
        Assert.IsTrue(operations.Contains("delete\t" + Root + "/obsolete.dll")); Assert.IsFalse(operations.Contains("unknown.txt")); Assert.IsFalse(operations.Contains("config"));
        Assert.IsTrue(operations.TrimEnd().Split('\n').Last().StartsWith("receipt-write\t")); Assert.AreEqual("1.0.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
    }
    [TestMethod] public void ChangedTargetOrPayloadInvalidatesPlan()
    {
        Package(); var plan = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Install);
        Write(Game, Root + "/Example.dll", "appeared later"); Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopTransaction.Stage(plan));
        File.Delete(WorkshopPaths.Inside(Game, Root + "/Example.dll")); Write(Source, "Payload/" + Root + "/Example.dll", "different");
        Assert.ThrowsExactly<InvalidDataException>(() => WorkshopTransaction.Stage(plan));
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
    [TestMethod] public void BatchStagesAllPayloadBeforeReceiptsAndKeepsDependencyEndState()
    {
        Package();
        var first = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Install);
        string secondSource = Source + "-second";
        Directory.Move(Source, secondSource);
        foreach (string path in Directory.EnumerateFiles(secondSource, "*", SearchOption.AllDirectories)) {
            string relative = path.Substring(secondSource.Length + 1).Replace('\\', '/');
            Write(Source, relative.Replace("Example", "Second"), File.ReadAllText(path).Replace("Example", "Second"));
        }
        Write(Source, WorkshopPackage.ManifestFileName, WorkshopPackageBuilder.Manifest(Source, new WorkshopPackageDraft("SecondPackage", "1.0.0", "0.7.1")));
        Directory.Move(Source, Source + "-saved"); Directory.Move(secondSource, Source);
        // The first plan retains its source; move the second snapshot to its
        // own source before planning it again.
        var second = WorkshopInstallation.Prepare("owner", Game, 456, Source + "-saved", WorkshopAction.Install);
        string stage = WorkshopTransaction.StageBatch(new[] { first, second }, Guid.NewGuid().ToString("N"));
        string[] rows = File.ReadAllLines(Path.Combine(stage, "operations.tsv"));
        Assert.AreEqual(WorkshopTransaction.BatchProtocol, rows[0]);
        Assert.IsTrue(rows[^1].StartsWith("receipt-write\t")); Assert.IsTrue(rows[^2].StartsWith("receipt-write\t"));
        Assert.IsTrue(rows.Skip(1).Take(rows.Length - 3).All(r => r.StartsWith("write\t")));
        if (OperatingSystem.IsWindows()) {
            // Simulate a crash after all payload writes and the first receipt.
            // Missing staged bytes must not prevent restoring the whole batch.
            var journal = new List<string> { "applying" };
            foreach (string row in rows.Skip(1).Take(rows.Length - 2)) {
                string[] cells = row.Split('\t');
                string target = WorkshopPaths.Inside(Game, cells[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(stage, "New", cells[2]), target);
                journal.Add("begin\t" + cells[2]); journal.Add("done\t" + cells[2]);
            }
            File.WriteAllLines(Path.Combine(stage, "journal.log"), journal);
            Directory.Delete(Path.Combine(stage, "New"), true);
            Assert.AreEqual(2, RunUpdater(stage, recover: true));
            Assert.IsFalse(File.Exists(WorkshopInstallation.ReceiptPath(Game, 123)));
            Assert.IsFalse(File.Exists(WorkshopInstallation.ReceiptPath(Game, 456)));
            Assert.IsFalse(File.Exists(WorkshopPaths.Inside(Game, Root + "/Example.dll")));
            stage = WorkshopTransaction.StageBatch(new[] { first, second }, Guid.NewGuid().ToString("N"));
            Assert.AreEqual(0, RunUpdater(stage));
            Assert.AreEqual("1.0.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
            Assert.AreEqual("1.0.0", WorkshopInstallation.ReadReceipt(Game, 456).Version);
            Assert.IsNotNull(WorkshopInstallation.ReadReceipt(Game, 456).Dependencies);
        }
        var state = WorkshopBatch.EndState(new[] { first, second }, new Dictionary<string, Version>());
        Assert.AreEqual(new Version(1, 0, 0, 0), state["SecondGuid"]);
        var dependent = WorkshopPackageCodec.Read(File.ReadAllText(Path.Combine(Source, WorkshopPackage.ManifestFileName)));
        dependent.Dependencies = new[] { new WorkshopDependency("SecondGuid", "1.0.0") };
        WorkshopBatch.Dependencies(dependent, state);
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopBatch.Dependencies(dependent, new Dictionary<string, Version>()));
    }
    [TestMethod] public void RemovalAndOwnershipUseReceiptsWithoutDeletingUnknownFiles()
    {
        var package = Package(); InstallReceipt(package); Write(Game, Root + "/Example.dll", "changed"); Write(Game, Root + "/unknown.txt", "unknown");
        var plan = WorkshopInstallation.Prepare("owner", Game, 123, null!, WorkshopAction.Remove);
        CollectionAssert.AreEqual(new[] { Root + "/info.json" }, plan.RemovedFiles.ToArray());
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
    [TestMethod] public void CorruptOwnReceiptAllowsExplicitInstallWithBackupAndNoUnknownDeletion()
    {
        Package(); Write(Game, Root + "/unknown.dll", "keep"); Write(Game, "_APIShared/Workshop/Receipts/123.json", "broken JSON");
        Assert.ThrowsExactly<InvalidDataException>(() => WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Update));
        var plan = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Install);
        Assert.AreEqual(0, plan.RemovedFiles.Count); Assert.IsTrue(plan.Diagnostics.Any(d => d.Contains("Invalid receipt")));
        Assert.IsTrue(File.Exists(Path.Combine(WorkshopTransaction.Stage(plan), "operations.tsv")));
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
    [TestMethod] public void InterruptedWindowsTransactionRestoresFilesAndReceipt()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows updater fixture requires Windows."); return; }
        var package = Package(); InstallReceipt(package); Package("1.1.0");
        var plan = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Update); string stage = WorkshopTransaction.Stage(plan);
        // Simulate interruption after replacing the first file, before receipt publication.
        string target = WorkshopPaths.Inside(Game, plan.Files[0].Path); string backup = Path.Combine(stage, "Backup/0"); Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(target, backup); File.Copy(Path.Combine(stage, "New/0"), target, true);
        File.WriteAllText(Path.Combine(stage, "journal.log"), "applying\r\nbegin\t0\r\n");
        // Recovery needs the original backups, even if the download/staging was lost.
        File.Delete(Path.Combine(stage, "New/0"));
        Assert.AreEqual(2, RunUpdater(stage, true)); Assert.AreEqual("payload 1.0.0", File.ReadAllText(WorkshopPaths.Inside(Game, Root + "/Example.dll")));
        Assert.AreEqual("1.0.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
    }
    [TestMethod] public void ExtraPayloadAndUndeclaredMapAreRejected()
    {
        var package = Package(); Write(Source, "Payload/" + Root + "/extra.dll", "extra"); Assert.ThrowsExactly<InvalidDataException>(() => WorkshopPaths.Verify(package, Source));
        File.Delete(WorkshopPaths.Inside(Source, "Payload/" + Root + "/extra.dll")); Write(Source, "Extra.map", "old archive"); Assert.ThrowsExactly<InvalidDataException>(() => WorkshopPaths.Verify(package, Source));
    }
    [TestMethod] public void RemovalNeverApprovesChangesAfterPlanning()
    {
        var package = Package("1.0.0", "old.dll"); InstallReceipt(package);
        var plan = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Remove);
        Write(Game, Root + "/old.dll", "changed after confirmation");
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopTransaction.Stage(plan));
        Assert.AreEqual("changed after confirmation", File.ReadAllText(WorkshopPaths.Inside(Game, Root + "/old.dll")));
    }
    [TestMethod] public void WindowsUpdaterHonorsExternalManagerEnvironment()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows only."); return; }
        Package(); var plan = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Install);
        string stage = WorkshopTransaction.Stage(plan);
        RunUpdater(stage, expectedCode: 1, externalManager: true);
        Assert.IsFalse(File.Exists(WorkshopPaths.Inside(Game, Root + "/Example.dll")));
        Assert.IsFalse(File.Exists(WorkshopInstallation.ReceiptPath(Game, 123)));
    }
    private int RunUpdater(string transaction, bool recover = false, int expectedCode = 0, bool externalManager = false)
    {
        string script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fixtures/workshop-updater.ps1"));
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if (externalManager) start.Environment["SCDEModManagerLaunchId"] = "fixture";
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-GameRoot", Game, "-Transaction", transaction, "-OperationsHash", WorkshopInventory.Hash(Path.Combine(transaction, "operations.tsv")) }) start.ArgumentList.Add(arg);
        if (recover) start.ArgumentList.Add("-Recover");
        if (File.Exists(Path.Combine(transaction, "supersedes.tsv"))) {
            start.ArgumentList.Add("-SupersedesHash"); start.ArgumentList.Add(WorkshopInventory.Hash(Path.Combine(transaction, "supersedes.tsv")));
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        Assert.IsTrue(process.WaitForExit(30000), "Updater fixture did not finish.");
        Assert.AreEqual(recover ? 2 : expectedCode, process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()); return process.ExitCode;
    }
    [TestMethod] public void WindowsUpdaterCommitsAndKeepsBackup()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows updater fixture requires Windows."); return; }
        var package = Package(); string first = WorkshopTransaction.Stage(WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Install));
        Assert.AreEqual(0, RunUpdater(first)); Assert.AreEqual(package.Version, WorkshopInstallation.ReadReceipt(Game, 123).Version);
        Assert.IsTrue(File.ReadAllText(Path.Combine(first, "journal.log")).Contains("committed"));
        Package("1.1.0"); Write(Game, Root + "/Example.dll", "local customization"); string second = WorkshopTransaction.Stage(WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Update));
        Assert.AreEqual(0, RunUpdater(second)); Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(second, "Backup")).Any(f => File.ReadAllText(f) == "local customization"));
    }
    [TestMethod]
    [DataRow("1.0.23", true)][DataRow("1.0.2", true)][DataRow("1.0.23", false)]
    public void DirectVersionJumpNeedsNoIntermediateRelease(string version, bool receipted)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows updater fixture requires Windows."); return; }
        var old = Package(version);
        if (receipted) InstallReceipt(old);
        else foreach (var file in old.Files) Write(Game, file.Path, File.ReadAllText(WorkshopPaths.Inside(Source, "Payload/" + file.Path)));
        Write(Game, Root + "/config/settings.json", "user settings"); Write(Game, Root + "/unknown.txt", "unknown");
        Package("1.0.27");
        var plan = WorkshopInstallation.Prepare("owner", Game, 123, Source, receipted ? WorkshopAction.Update : WorkshopAction.Install);
        Assert.AreEqual(0, RunUpdater(WorkshopTransaction.Stage(plan)));
        Assert.AreEqual("1.0.27", WorkshopInstallation.ReadReceipt(Game, 123).Version);
        Assert.AreEqual("payload 1.0.27", File.ReadAllText(WorkshopPaths.Inside(Game, Root + "/Example.dll")));
        Assert.AreEqual("user settings", File.ReadAllText(WorkshopPaths.Inside(Game, Root + "/config/settings.json")));
        Assert.AreEqual("unknown", File.ReadAllText(WorkshopPaths.Inside(Game, Root + "/unknown.txt")));
    }
    [TestMethod] public void ConfirmedCompleteRepairStagesAcrossUnrecoverableOldTransaction()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows updater fixture requires Windows."); return; }
        InstallReceipt(Package("1.0.23")); Package("1.0.27");
        var update = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Update);
        string interrupted = WorkshopTransaction.Stage(update);
        Write(Game, update.Files[0].Path, "interrupted partial copy");
        Write(interrupted, "journal.log", "applying\r\nbegin\t0\r\n");
        var entries = WorkshopRecovery.Inspect(Game);
        Assert.IsFalse(entries[0].CanRollback);
        var repair = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Reinstall);
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopTransaction.Stage(repair));
        string replacement = WorkshopTransaction.StageBatch(new[] { repair }, Guid.NewGuid().ToString("N"), entries);
        Assert.HasCount(1, WorkshopRecovery.Inspect(Game));
        Assert.AreEqual(0, RunUpdater(replacement));
        Assert.HasCount(0, WorkshopRecovery.Inspect(Game));
        Assert.AreEqual("1.0.27", WorkshopInstallation.ReadReceipt(Game, 123).Version);
        Assert.IsTrue(!Directory.Exists(interrupted) || File.ReadAllLines(Path.Combine(interrupted, "journal.log")).Contains("applying"));
    }
    // Regression: without a usable backup the only repair kept stale/foreign DLLs and could
    // dead-end. A clean reinstallation must always resolve the interrupted transaction.
    [TestMethod] public void CleanRepairReinstallsFromSteamAndRemovesStaleAssembliesWithBackup()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows updater fixture requires Windows."); return; }
        InstallReceipt(Package("1.0.23", "old.dll"));
        File.Delete(WorkshopPaths.Inside(Source, "Payload/" + Root + "/old.dll")); Package("1.0.27");
        Write(Game, Root + "/stray.dll", "foreign"); Write(Game, Root + "/notes.txt", "user data"); Write(Game, Root + "/config/settings.json", "settings");
        var update = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Update);
        string interrupted = WorkshopTransaction.Stage(update);
        Write(Game, update.Files[0].Path, "interrupted partial copy");
        Write(interrupted, "journal.log", "applying\r\nbegin\t0\r\n");
        var entries = WorkshopRecovery.Inspect(Game);
        Assert.IsFalse(entries[0].CanRollback); Assert.IsTrue(entries[0].Readable);
        var repair = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Reinstall);
        WorkshopRepairPlanner.Complete(Game, new[] { repair }, WorkshopRepairPlanner.Collect(Game, entries));
        CollectionAssert.AreEquivalent(new[] { Root + "/old.dll", Root + "/stray.dll" }, repair.RemovedFiles.ToArray());
        string replacement = WorkshopTransaction.StageBatch(new[] { repair }, Guid.NewGuid().ToString("N"), entries);
        Assert.AreEqual(0, RunUpdater(replacement));
        Assert.HasCount(0, WorkshopRecovery.Inspect(Game));
        Assert.AreEqual("1.0.27", WorkshopInstallation.ReadReceipt(Game, 123).Version);
        Assert.AreEqual("payload 1.0.27", File.ReadAllText(WorkshopPaths.Inside(Game, Root + "/Example.dll")));
        Assert.IsFalse(File.Exists(WorkshopPaths.Inside(Game, Root + "/stray.dll")));
        Assert.IsFalse(File.Exists(WorkshopPaths.Inside(Game, Root + "/old.dll")));
        Assert.AreEqual("user data", File.ReadAllText(WorkshopPaths.Inside(Game, Root + "/notes.txt")));
        Assert.AreEqual("settings", File.ReadAllText(WorkshopPaths.Inside(Game, Root + "/config/settings.json")));
        Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(replacement, "Backup")).Any(f => File.ReadAllText(f) == "foreign"));
    }
    [TestMethod] public void CleanRepairRemovesAnUnsubscribedPackageWithoutReceipt()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows updater fixture requires Windows."); return; }
        Package(); Write(Game, Root + "/config/settings.json", "settings");
        var install = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Install);
        string interrupted = WorkshopTransaction.Stage(install);
        Write(Game, install.Files[0].Path, "interrupted partial copy");
        Write(interrupted, "journal.log", "applying\r\nbegin\t0\r\nfailure interrupted\r\n");
        var entries = WorkshopRecovery.Inspect(Game);
        // The player unsubscribed meanwhile: there is no package to reinstall and no receipt to remove.
        var removal = WorkshopRepairPlanner.Removal("owner", Game, 123);
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopRepairPlanner.AssertCoverage(Game, entries, new[] { removal }));
        WorkshopRepairPlanner.Complete(Game, new[] { removal }, WorkshopRepairPlanner.Collect(Game, entries));
        WorkshopRepairPlanner.AssertCoverage(Game, entries, new[] { removal });
        string replacement = WorkshopTransaction.StageBatch(new[] { removal }, Guid.NewGuid().ToString("N"), entries);
        Assert.AreEqual(0, RunUpdater(replacement));
        Assert.HasCount(0, WorkshopRecovery.Inspect(Game));
        Assert.IsFalse(File.Exists(WorkshopInstallation.ReceiptPath(Game, 123)));
        Assert.IsFalse(File.Exists(WorkshopPaths.Inside(Game, install.Files[0].Path)));
        Assert.AreEqual("settings", File.ReadAllText(WorkshopPaths.Inside(Game, Root + "/config/settings.json")));
    }
    [TestMethod] public void UnreadableTransactionIsArchivedForRepairAndRestorable()
    {
        string broken = Path.Combine(WorkshopInstallation.StateRoot(Game), "Transactions", Guid.NewGuid().ToString("N"));
        Write(broken, "operations.tsv", "damaged"); Write(broken, "journal.log", "applying\r\nbegin\t0\r\n");
        var entries = WorkshopRecovery.Inspect(Game);
        Assert.HasCount(1, entries); Assert.IsFalse(entries[0].Readable); Assert.IsFalse(entries[0].CanRollback);
        var moved = WorkshopRecovery.Archive(Game, entries);
        Assert.HasCount(0, WorkshopRecovery.Inspect(Game));
        Assert.IsFalse(Directory.Exists(broken));
        Assert.AreEqual("damaged", File.ReadAllText(Path.Combine(moved[0].Value, "operations.tsv")));
        WorkshopRecovery.Restore(moved);
        Assert.HasCount(1, WorkshopRecovery.Inspect(Game));
    }
    [TestMethod] public void StagingWaitsForTheStartupMaintenanceGate()
    {
        Package();
        var plan = WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Install);
        using var entered = new System.Threading.ManualResetEventSlim();
        WorkshopTransaction.StateGate.Wait();
        Task<string>? staging = null;
        try {
            staging = Task.Run(() => { entered.Set(); return WorkshopTransaction.Stage(plan); });
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(staging.Wait(TimeSpan.FromMilliseconds(100)), "Staging must not race the maintenance child.");
        } finally { WorkshopTransaction.StateGate.Release(); }
        Assert.IsTrue(Directory.Exists(staging!.GetAwaiter().GetResult()));
    }
    [TestMethod] public void LockedFilePreventsAllWritesAndLeavesOldReceipt()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows updater fixture requires Windows."); return; }
        var package = Package(); InstallReceipt(package); Package("1.1.0");
        string stage = WorkshopTransaction.Stage(WorkshopInstallation.Prepare("owner", Game, 123, Source, WorkshopAction.Update));
        using (var held = new FileStream(WorkshopPaths.Inside(Game, Root + "/info.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual(1, RunUpdater(stage, expectedCode: 1));
        Assert.AreEqual("payload 1.0.0", File.ReadAllText(WorkshopPaths.Inside(Game, Root + "/Example.dll")));
        Assert.AreEqual("1.0.0", WorkshopInstallation.ReadReceipt(Game, 123).Version);
        Assert.IsFalse(File.ReadAllText(Path.Combine(stage, "journal.log")).Contains("applying"));
    }
}
