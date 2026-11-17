using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WorkshopRecoveryTests
{
    private string game = null!;
    private string old = null!;
    private const string Target = "BepInEx/plugins/Example/Example.dll";
    private const string Receipt = "_APIShared/Workshop/Receipts/123.json";
    [TestInitialize] public void Init()
    {
        game = Path.Combine(Path.GetTempPath(), "APIShared-Recovery-" + Guid.NewGuid().ToString("N"));
        old = Path.Combine(game, "_APIShared/Workshop/Transactions", Guid.NewGuid().ToString("N"));
        Write(old, "Backup/0", "original"); Write(game, Target, "partial");
        Write(old, "operations.tsv", "APISHARED-WORKSHOP-1\r\nwrite\t" + Target + "\t0\t" + WorkshopInventory.Hash(Path.Combine(old, "Backup/0")) + "\t" + WorkshopInventory.Hash(Path.Combine(game, Target)) +
            "\r\nreceipt-write\t" + Receipt + "\t1\tmissing\t" + new string('a', 64) + "\r\n");
        Write(old, "journal.log", "applying\r\nbegin\t0\r\nfailure interrupted test\r\n");
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(game)) Directory.Delete(game, true); }
    private static void Write(string root, string relative, string text)
    {
        string file = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }
    [TestMethod] public void CleanRepairRemovesForeignAssembliesButKeepsSettingsDataAndOtherMods()
    {
        Write(game, Target, "current payload");
        Write(game, "BepInEx/plugins/Example/obsolete.dll", "old dll");
        Write(game, "BepInEx/plugins/Example/unknown.DLL", "unknown dll");
        Write(game, "BepInEx/plugins/Example/config/helper.dll", "user data");
        Write(game, "BepInEx/plugins/Example/presets.json", "user data");
        Write(game, "BepInEx/plugins/Other/other.dll", "other mod");
        var keep = new HashSet<string>(new[] { Target }, StringComparer.OrdinalIgnoreCase);
        CollectionAssert.AreEqual(new[] { "BepInEx/plugins/Example/obsolete.dll", "BepInEx/plugins/Example/unknown.DLL" },
            WorkshopRepairPlanner.Cleanup(game, "BepInEx/plugins/Example", keep, null, null).ToArray());
        // Interrupted targets are covered even when already absent; earlier files only when present.
        CollectionAssert.Contains(WorkshopRepairPlanner.Cleanup(game, "BepInEx/plugins/Example", keep,
            new[] { "BepInEx/plugins/Example/gone.txt" }, new[] { "BepInEx/plugins/Example/partial.txt" }).ToArray(), "BepInEx/plugins/Example/partial.txt");
        CollectionAssert.DoesNotContain(WorkshopRepairPlanner.Cleanup(game, "BepInEx/plugins/Example", keep,
            new[] { "BepInEx/plugins/Example/gone.txt" }, null).ToArray(), "BepInEx/plugins/Example/gone.txt");
        Assert.AreEqual("user data", File.ReadAllText(Path.Combine(game, "BepInEx/plugins/Example/config/helper.dll")));
    }
    [TestMethod] public void RecoveryButtonsKeepTheirExplicitActionsAndCancelIsSafe()
    {
        Assert.AreEqual(WorkshopRecovery.Choice.Repair, WorkshopRecovery.Decide(true, 6));
        Assert.AreEqual(WorkshopRecovery.Choice.Rollback, WorkshopRecovery.Decide(true, 7));
        foreach (int answer in new[] { 0, 1, 2, -1, 99 }) Assert.AreEqual(WorkshopRecovery.Choice.Later, WorkshopRecovery.Decide(true, answer));
        Assert.AreEqual(WorkshopRecovery.Choice.CompleteRepair, WorkshopRecovery.Decide(false, 6));
        Assert.AreEqual(WorkshopRecovery.Choice.Later, WorkshopRecovery.Decide(false, 7));
        // With resume available only Yes changes its meaning.
        Assert.AreEqual(WorkshopRecovery.Choice.Resume, WorkshopRecovery.Decide(true, 6, resume: true));
        Assert.AreEqual(WorkshopRecovery.Choice.Rollback, WorkshopRecovery.Decide(true, 7, resume: true));
        foreach (int answer in new[] { 0, 1, 2, -1, 99 }) Assert.AreEqual(WorkshopRecovery.Choice.Later, WorkshopRecovery.Decide(true, answer, resume: true));
        Assert.AreEqual(WorkshopRecovery.Choice.CompleteRepair, WorkshopRecovery.Decide(false, 6, resume: true));
    }
    [TestMethod] public void ResumePointAcceptsOnlySequentialProgress()
    {
        Assert.AreEqual((0, -1), WorkshopRecovery.ResumePoint(new[] { "applying" }, 2));
        Assert.AreEqual((0, 0), WorkshopRecovery.ResumePoint(new[] { "applying", "begin\t0", "failure x", "trace t; y" }, 2));
        Assert.AreEqual((1, 1), WorkshopRecovery.ResumePoint(new[] { "applying", "begin\t0", "pause p", "done\t0", "begin\t1" }, 2));
        Assert.AreEqual((2, -1), WorkshopRecovery.ResumePoint(new[] { "applying", "begin\t0", "done\t0", "begin\t1", "done\t1" }, 2));
        foreach (var journal in new[] {
            new[] { "begin\t1" }, new[] { "begin\t0", "begin\t0" }, new[] { "done\t0" }, new[] { "begin\t0", "done\t1" },
            new[] { "begin\t00" }, new[] { "begin 0" }, new[] { "begin" }, new[] { "begin\t0", "done\t0", "begin\t1", "done\t1", "begin\t2" } })
            Assert.ThrowsExactly<InvalidDataException>(() => WorkshopRecovery.ResumePoint(journal, 2), string.Join("|", journal));
    }
    [TestMethod] public void ResumeIsOfferedOnlyWithIntactStagedCopiesBackupsAndTargets()
    {
        // Row 0 completed, receipt row 1 begun with partial bytes.
        Write(old, "Backup/0", "original");
        Write(old, "New/0", "new payload"); Write(old, "New/1", "new receipt");
        string before = WorkshopInventory.Hash(Path.Combine(old, "Backup/0"));
        string after = WorkshopInventory.Hash(Path.Combine(old, "New/0")), receiptAfter = WorkshopInventory.Hash(Path.Combine(old, "New/1"));
        Write(old, "operations.tsv", WorkshopTransaction.BatchProtocol + "\r\nwrite\t" + Target + "\t0\t" + before + "\t" + after +
            "\r\nreceipt-write\t" + Receipt + "\t1\tmissing\t" + receiptAfter + "\r\n");
        Write(game, Target, "new payload"); Write(game, Receipt, "partial receipt");
        Write(old, "journal.log", "applying\r\nbegin\t0\r\ndone\t0\r\nbegin\t1\r\n");
        var entry = WorkshopRecovery.Inspect(game).Single();
        Assert.IsTrue(entry.CanRollback);
        Assert.IsNull(WorkshopRecovery.ResumeError(game, entry));
        string request = WorkshopRecovery.StageResume(game, entry);
        CollectionAssert.AreEqual(new[] { WorkshopRecovery.Protocol, WorkshopRecovery.ResumeDisposition, entry.Reference }, File.ReadAllLines(Path.Combine(request, "operations.tsv")));
        Directory.Delete(request, true);

        File.Delete(Path.Combine(old, "New/1"));
        StringAssert.Contains(WorkshopRecovery.ResumeError(game, WorkshopRecovery.Inspect(game).Single()), "Staged file missing");
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopRecovery.StageResume(game, WorkshopRecovery.Inspect(game).Single()));
        Write(old, "New/1", "new receipt");
        Write(game, Target, "changed by player");
        StringAssert.Contains(WorkshopRecovery.ResumeError(game, WorkshopRecovery.Inspect(game).Single()), "Completed target changed");
        Write(game, Target, "new payload");
        Write(old, "journal.log", "applying\r\nbegin\t0\r\ndone\t0\r\nbegin\t1\r\nbegin\t1\r\n");
        StringAssert.Contains(WorkshopRecovery.ResumeError(game, WorkshopRecovery.Inspect(game).Single()), "not sequential");
        Write(old, "journal.log", "applying\r\nbegin\t0\r\ndone\t0\r\nbegin\t1\r\n");
        Write(old, "supersedes.tsv", "x"); Write(old, "supersedes.sha256", "x");
        StringAssert.Contains(WorkshopRecovery.ResumeError(game, WorkshopRecovery.Inspect(game).Single()), "complete repair");
    }
    [TestMethod] public void StaleUpdaterRecordIsDescribedForDiagnosticsOnly()
    {
        long ticks = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc).Ticks;
        string text = string.Join("\t", WorkshopUpdaterActivity.Header, "windows", "4242", ticks.ToString(), "install", "apply", new string('b', 32), new string('c', 64));
        Assert.AreEqual("platform=windows; pid=4242; processStart=2026-11-17 12:00:00.000Z; mode=install; phase=apply; transaction=" + new string('b', 32),
            WorkshopUpdaterActivity.DescribeRecord(text));
        Assert.IsNull(WorkshopUpdaterActivity.DescribeRecord(""));
        Assert.IsNull(WorkshopUpdaterActivity.DescribeRecord("garbage"));
        string state = Path.Combine(game, "_APIShared/Workshop");
        Assert.IsNull(WorkshopUpdaterActivity.DescribeStaleWindows(game));
        Write(state, "apply.lock", text);
        StringAssert.Contains(WorkshopUpdaterActivity.DescribeStaleWindows(game), "phase=apply");
        using (File.Open(Path.Combine(state, "apply.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            Assert.IsNull(WorkshopUpdaterActivity.DescribeStaleWindows(game), "A live writer is not stale.");
        Write(state, "apply.lock", "");
        Assert.IsNull(WorkshopUpdaterActivity.DescribeStaleWindows(game));
    }
    [TestMethod] public void LargeUserCaseWithoutFailureOrReceiptHasReadableProgressAndFreshConsent()
    {
        var operations = new List<string> { WorkshopTransaction.BatchProtocol };
        var journal = new List<string> { "applying" };
        int changes = 0;
        for (int i = 0; i < 882; i++) {
            string kind = i < 874 ? "write" : i < 881 ? "delete" : "receipt-write";
            string relative = i == 881 ? Receipt : i == 778 ? "BepInEx/plugins/Example/Locales/zh-CN.txt" : "BepInEx/plugins/Example/file" + i;
            bool missing = i == 54 || i == 865 || i == 871 || i == 881;
            string original = "original " + i;
            if (!missing) Write(old, "Backup/" + i, original);
            string before = missing ? "missing" : WorkshopInventory.Hash(Path.Combine(old, "Backup/" + i));
            string current = i < 84 || i >= 779 ? "updated " + i : original;
            Write(game, relative, current);
            string after = kind == "delete" ? "missing" : WorkshopInventory.Hash(Path.Combine(game, relative));
            if (i <= 778) {
                journal.Add("begin\t" + i); journal.Add("done\t" + i);
                if (before != after) changes++;
            } else if (missing) File.Delete(Path.Combine(game, relative));
            else Write(game, relative, original);
            operations.Add(kind + "\t" + relative + "\t" + i + "\t" + before + "\t" + after);
        }
        File.WriteAllLines(Path.Combine(old, "operations.tsv"), operations);
        File.WriteAllLines(Path.Combine(old, "journal.log"), journal);
        Write(old, "cancel.flag", "stale handoff");
        var entry = WorkshopRecovery.Inspect(game).Single();
        Assert.AreEqual(84, changes); Assert.IsTrue(entry.CanRollback); Assert.IsNull(entry.Failure);
        Assert.IsTrue(entry.Progress.Reliable); Assert.AreEqual(779, entry.Progress.Completed); Assert.AreEqual(882, entry.Progress.Total);
        StringAssert.Contains(WorkshopRecovery.Describe(entry, true), "779 von 882");
        StringAssert.Contains(WorkshopRecovery.Describe(entry, true), "Unterbrochen ohne protokollierten Fehler.");
        StringAssert.Contains(WorkshopRecovery.Describe(entry, false), "779 of 882");
        StringAssert.Contains(WorkshopRecovery.Describe(entry, false), "Locales/zh-CN.txt");
        Assert.IsNull(entry.Progress.Pending); Assert.IsFalse(File.Exists(Path.Combine(game, Receipt)));
        Assert.IsFalse(Directory.Exists(Path.Combine(old, "New")));
        string request = WorkshopRecovery.StageRollback(game, new[] { entry }, false);
        Assert.AreNotEqual(old, request); Assert.IsFalse(File.Exists(Path.Combine(request, "cancel.flag")));
        CollectionAssert.AreEqual(journal.ToArray(), File.ReadAllLines(Path.Combine(old, "journal.log")));
        File.Delete(Path.Combine(old, "Backup/0"));
        entry = WorkshopRecovery.Inspect(game).Single();
        Assert.IsFalse(entry.CanRollback); Assert.IsTrue(entry.Progress.Reliable);
        CollectionAssert.AreEqual(new ulong[] { 123 }, entry.Items);
    }
    [TestMethod] public void ProgressCoversNoWritesPartialWriteAndPendingReceipt()
    {
        var rows = new[] { new[] { "write", Target }, new[] { "receipt-write", Receipt } };
        var progress = WorkshopRecovery.Progress(new[] { "applying" }, rows);
        Assert.IsTrue(progress.Reliable); Assert.AreEqual(0, progress.Completed); Assert.IsNull(progress.LastCompleted);
        progress = WorkshopRecovery.Progress(new[] { "applying", "begin\t0" }, rows);
        Assert.IsTrue(progress.Reliable); Assert.AreEqual(Target, progress.Pending);
        StringAssert.Contains(progress.Describe(true), "Begonnen, ohne protokollierten Abschluss: " + Target);
        StringAssert.Contains(progress.Describe(false), "Started without a recorded completion: " + Target);
        progress = WorkshopRecovery.Progress(new[] { "applying", "begin\t0", "done\t0" }, rows);
        Assert.IsTrue(progress.Reliable); Assert.AreEqual(1, progress.Completed); Assert.AreEqual(Target, progress.LastCompleted);
        Assert.IsNull(progress.Pending);
        progress = WorkshopRecovery.Progress(new[] { "applying", "begin\t0", "done\t0", "begin\t1" }, rows);
        Assert.AreEqual(Receipt, progress.Pending); Assert.AreEqual(1, progress.Completed);
        foreach (string invalid in new[] { "begin\t-1", "begin\t00", "begin\t2", "begin\tno", "begin", "begin 0", "done\t0", "begin\t1" })
            Assert.IsFalse(WorkshopRecovery.Progress(new[] { "applying", invalid }, rows).Reliable, invalid);
    }
    [TestMethod] public void UnreliableProgressDoesNotChangeRollbackEligibilityOrHideErrors()
    {
        foreach (string suffix in new[] { "done\t0\ndone\t0", "begin\t0", "done\t1", "done\t-1", "done\t00", "done\tno", "done", "done 0", "done\t999", "done\t0\textra" }) {
            Write(old, "journal.log", "applying\nbegin\t0\n" + suffix + "\nfailure original diagnostic\n");
            var entry = WorkshopRecovery.Inspect(game).Single();
            Assert.IsTrue(entry.CanRollback, suffix); Assert.IsFalse(entry.Progress.Reliable, suffix);
            StringAssert.Contains(WorkshopRecovery.Describe(entry, true), "Fortschritt nicht zuverlässig bestimmbar.");
            StringAssert.Contains(WorkshopRecovery.Describe(entry, false), "Progress cannot be determined reliably.");
            StringAssert.Contains(WorkshopRecovery.Describe(entry, false), "failure original diagnostic");
        }
    }
    [TestMethod] public void InspectionAndDialogRollbackAreReadOnlyUntilConfirmedHandoff()
    {
        var interrupted = WorkshopRecovery.Inspect(game);
        Assert.HasCount(1, interrupted); Assert.IsTrue(interrupted[0].CanRollback);
        CollectionAssert.AreEqual(new ulong[] { 123 }, interrupted[0].Items);
        Assert.AreEqual("failure interrupted test", interrupted[0].Failure);
        string request = WorkshopRecovery.StageRollback(game, interrupted, false);
        Assert.AreEqual(Path.Combine(game, "_APIShared", "T"), Path.GetDirectoryName(request));
        string[] rows = File.ReadAllLines(Path.Combine(request, "operations.tsv"));
        Assert.AreEqual(WorkshopRecovery.Protocol, rows[0]); Assert.AreEqual("stop", rows[1]);
        Assert.AreEqual("partial", File.ReadAllText(Path.Combine(game, Target)));
        Assert.AreEqual("applying", File.ReadAllLines(Path.Combine(old, "journal.log"))[0]);
        Assert.AreNotEqual(old, request);
    }
    [TestMethod] public void DuplicateTransactionIdentityBlocksRecoveryWithoutTouchingEitherCopy()
    {
        string duplicate = Path.Combine(game, "_APIShared", "T", Path.GetFileName(old));
        Directory.CreateDirectory(duplicate);
        string original = File.ReadAllText(Path.Combine(old, "journal.log"));
        var error = Assert.ThrowsExactly<InvalidDataException>(() => WorkshopRecovery.Inspect(game));
        Assert.AreEqual(WorkshopDiagnosticLevel.Warning, WorkshopDiagnostics.Level(error));
        Assert.AreEqual(original, File.ReadAllText(Path.Combine(old, "journal.log")));
        Assert.IsTrue(Directory.Exists(duplicate));
    }
    [TestMethod] public void MissingBackupDisablesRollbackButKeepsRepairIdentity()
    {
        File.Delete(Path.Combine(old, "Backup/0"));
        var entries = WorkshopRecovery.Inspect(game);
        Assert.IsFalse(entries[0].CanRollback);
        CollectionAssert.AreEqual(new ulong[] { 123 }, entries[0].Items);
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopRecovery.StageRollback(game, entries, true));
    }
    [TestMethod] public void InterruptedRollbackCanContinueWithoutAlreadyRestoredBackup()
    {
        Write(game, Target, "original"); File.Delete(Path.Combine(old, "Backup/0"));
        Assert.IsTrue(WorkshopRecovery.Inspect(game)[0].CanRollback);
    }
    [TestMethod] public void ChangedJournalRequiresFreshConsent()
    {
        var entries = WorkshopRecovery.Inspect(game);
        File.AppendAllText(Path.Combine(old, "journal.log"), "failure changed\r\n");
        Assert.ThrowsExactly<IOException>(() => WorkshopRecovery.StageRollback(game, entries, true));
    }
    [TestMethod] public void OverlappingTransactionsDisableRollback()
    {
        string other = Path.Combine(Path.GetDirectoryName(old)!, Guid.NewGuid().ToString("N"));
        Write(other, "operations.tsv", File.ReadAllText(Path.Combine(old, "operations.tsv")));
        Write(other, "journal.log", "applying\r\n");
        Assert.IsTrue(WorkshopRecovery.Inspect(game).All(x => !x.CanRollback));
    }
    [TestMethod] public void InvalidJournalOrdinalCannotSelectAnArbitraryBackup()
    {
        File.AppendAllText(Path.Combine(old, "journal.log"), "begin\t../../outside\r\n");
        Assert.IsFalse(WorkshopRecovery.Inspect(game)[0].CanRollback);
    }
    [TestMethod] public void CommittedReplacementAloneResolvesTheOldTransaction()
    {
        var entries = WorkshopRecovery.Inspect(game);
        string replacement = Path.Combine(Path.GetDirectoryName(old)!, Guid.NewGuid().ToString("N"));
        Write(replacement, "supersedes.tsv", entries[0].Reference + "\r\n");
        Write(replacement, "supersedes.sha256", WorkshopInventory.Hash(Path.Combine(replacement, "supersedes.tsv")));
        Write(replacement, "journal.log", "ready\r\n");
        Assert.HasCount(1, WorkshopRecovery.Inspect(game));
        Write(replacement, "journal.log", "committed\r\n");
        Assert.HasCount(0, WorkshopRecovery.Inspect(game));
        // Original journal and backups are retained, not silently marked restored.
        Assert.IsFalse(File.ReadAllLines(Path.Combine(old, "journal.log")).Contains("restored"));
    }
    [TestMethod] public void FinishedRollbackDoesNotPermanentlySuppressFutureUpdates()
    {
        File.AppendAllText(Path.Combine(old, "journal.log"), "restored\r\n");
        Assert.HasCount(0, WorkshopRecovery.Inspect(game));
        Assert.IsFalse(File.Exists(Path.Combine(game, Receipt)));
        Assert.IsFalse(Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories).Any(p => p.EndsWith("skip-update", StringComparison.Ordinal)));
    }
}
