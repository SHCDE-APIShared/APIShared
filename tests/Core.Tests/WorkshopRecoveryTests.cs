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
    [TestMethod] public void InspectionAndDialogRollbackAreReadOnlyUntilConfirmedHandoff()
    {
        var interrupted = WorkshopRecovery.Inspect(game);
        Assert.HasCount(1, interrupted); Assert.IsTrue(interrupted[0].CanRollback);
        CollectionAssert.AreEqual(new ulong[] { 123 }, interrupted[0].Items);
        Assert.AreEqual("failure interrupted test", interrupted[0].Failure);
        string request = WorkshopRecovery.StageRollback(game, interrupted, false);
        string[] rows = File.ReadAllLines(Path.Combine(request, "operations.tsv"));
        Assert.AreEqual(WorkshopRecovery.Protocol, rows[0]); Assert.AreEqual("stop", rows[1]);
        Assert.AreEqual("partial", File.ReadAllText(Path.Combine(game, Target)));
        Assert.AreEqual("applying", File.ReadAllLines(Path.Combine(old, "journal.log"))[0]);
        Assert.AreNotEqual(old, request);
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
