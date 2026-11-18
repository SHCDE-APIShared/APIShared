using APIShared.Workshop.Install;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using System.Text;

namespace APIShared.Core.Tests;

// Invariants of the safe installation core (src/Workshop/Install): BepInEx only ever sees a
// complete old or complete new version of every root, interrupted swaps are finished or undone
// before loading, keep lists carry exactly the listed player files. Real process kills:
// tools/Validation/Test-WorkshopInstall.ps1.
[TestClass]
public sealed class WorkshopSafeInstallTests
{
    private const ulong Id = 77;
    private static readonly string[] Targets = { "BepInEx/plugins/PkgA", "BepInEx/plugins/PkgB" };
    private string fixture = null!;
    private InstallPaths paths = null!;
    private string Game => Path.Combine(fixture, "game");

    [TestInitialize] public void Init() { fixture = Path.Combine(Path.GetTempPath(), "ApiSafeInstall-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Game); paths = new InstallPaths(Game); }
    [TestCleanup] public void Cleanup() { InstallSwap.Fault = null; InstallStaging.AfterCopy = null; if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }

    private static void Write(string root, IDictionary<string, string> files)
    {
        foreach (var file in files) {
            string path = Path.Combine(root, file.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value);
        }
    }

    // Two roots per version; root A carries the keep list (incl. a nested list in a sub mod).
    private static Dictionary<string, string> Version(int v, string keep = "settings.json\nconfig/\n") => new()
    {
        ["PkgA/a.dll"] = "a" + v, ["PkgA/same.txt"] = "same", ["PkgA/only-" + v + ".txt"] = "v" + v, ["PkgA/settings.json"] = "default-" + v,
        ["PkgA/apishared-keep.txt"] = keep, ["PkgA/mods/child/child.dll"] = "c" + v, ["PkgA/mods/child/apishared-keep.txt"] = "child.cfg\n",
        ["PkgB/b.dll"] = "b" + v,
    };

    private string Stage(int v, string keep = "settings.json\nconfig/\n")
    {
        string source = Path.Combine(fixture, "src" + v + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        var files = Version(v, keep);
        Write(source, files);
        var roots = Targets.Select(target => {
            string name = target.Split('/')[2];
            string dir = Path.Combine(source, name);
            var entries = InstallTree.Files(dir).Select(p => new InstallFile(p, new FileInfo(Path.Combine(dir, p)).Length, InstallTree.Sha256(Path.Combine(dir, p)))).ToList();
            return new InstallRoot(target, dir, entries);
        }).ToList();
        return InstallStaging.StageInstall(paths, Id, "PkgId", roots, "{\"Version\":\"" + v + "\"}", _ => { });
    }

    private void Apply() => InstallSwap.Apply(paths, Id, _ => { });
    private void Preloader() => InstallRunner.Run(Game, _ => { }, message => throw new AssertFailedException("preloader error: " + message));
    private string Target(int i) => paths.Target(Targets[i]);
    private string Read(string relative) { string p = Path.Combine(Game, "BepInEx", "plugins", relative.Replace('/', Path.DirectorySeparatorChar)); return File.Exists(p) ? File.ReadAllText(p) : "<missing>"; }

    private static string TreeId(string root)
    {
        if (!Directory.Exists(root)) return "missing";
        var text = new StringBuilder();
        foreach (string p in InstallTree.Files(root)) text.Append(p).Append('=').Append(InstallTree.Sha256(Path.Combine(root, p))).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    // Installs version 1 through the real swap and changes it like a player would.
    private void InstallVersionOneWithPlayerFiles()
    {
        Stage(1); Apply();
        Write(Path.Combine(Game, "BepInEx", "plugins"), new Dictionary<string, string> {
            ["PkgA/settings.json"] = "player settings", ["PkgA/config/user.cfg"] = "player config",
            ["PkgA/mods/child/child.cfg"] = "child player", ["PkgA/junk.log"] = "not listed",
        });
    }

    private void AssertVersionTwoWithPlayerFiles()
    {
        Assert.AreEqual("a2", Read("PkgA/a.dll")); Assert.AreEqual("b2", Read("PkgB/b.dll")); Assert.AreEqual("c2", Read("PkgA/mods/child/child.dll"));
        Assert.AreEqual("player settings", Read("PkgA/settings.json"));
        Assert.AreEqual("player config", Read("PkgA/config/user.cfg"));
        Assert.AreEqual("child player", Read("PkgA/mods/child/child.cfg"));
        Assert.AreEqual("<missing>", Read("PkgA/junk.log"), "unlisted files disappear");
        Assert.AreEqual("<missing>", Read("PkgA/only-1.txt"), "files the new version dropped disappear");
        Assert.AreEqual("{\"Version\":\"2\"}", File.ReadAllText(paths.Receipt(Id)));
        AssertClean();
    }

    private void AssertClean()
    {
        Assert.IsFalse(File.Exists(paths.Journal(Id)), "journal left");
        Assert.IsFalse(File.Exists(paths.Request(Id)), "request left");
        Assert.IsFalse(Directory.Exists(paths.Container(Id)), "container left");
        for (int i = 0; i < Targets.Length; i++) Assert.IsFalse(Directory.Exists(paths.Backup(Id, i)), "backup left");
    }

    [TestMethod] public void StagingPublishesOnlyVerifiedCopies()
    {
        Stage(1);
        Assert.IsTrue(InstallStaging.Pending(paths, Id));
        Assert.IsFalse(Directory.Exists(Target(0)), "staging never touches BepInEx/plugins");
        // A source changing while it is copied (Steam writing) is rejected; nothing is published.
        InstallStaging.Withdraw(paths, Id);
        InstallStaging.AfterCopy = path => { foreach (string file in Directory.GetFiles(fixture, "b.dll", SearchOption.AllDirectories)) File.WriteAllText(file, "changed during copy"); };
        Assert.ThrowsExactly<InvalidDataException>(() => Stage(2));
        InstallStaging.AfterCopy = null;
        Assert.IsFalse(InstallStaging.Pending(paths, Id));
        Assert.IsFalse(Directory.Exists(paths.Container(Id) + ".tmp"));
    }

    [TestMethod]
    [DataRow("../outside.cfg\n")][DataRow("C:/abs.cfg\n")][DataRow("conf*.json\n")][DataRow("a.json\na.json\n")][DataRow("a b.json\n")]
    public void MalformedKeepListsAreRejectedWhenStaging(string keep)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => Stage(1, keep));
        Assert.IsFalse(InstallStaging.Pending(paths, Id));
    }

    [TestMethod] public void KeepListOptionsAreParsed()
    {
        string root = Path.Combine(fixture, "keep");
        Write(root, new Dictionary<string, string> { ["apishared-keep.txt"] = "# comment\nsettings.json\nconfig/ persist   # trailing\npresets.json 2\nboth/ 3 persist\n", ["sub/apishared-keep.txt"] = "x.cfg\n" });
        var entries = InstallKeep.Read(root).ToDictionary(e => e.Path);
        Assert.HasCount(5, entries);
        Assert.IsTrue(entries["config"].Folder && entries["config"].Persist && entries["config"].Revision == 0);
        Assert.IsTrue(!entries["presets.json"].Folder && entries["presets.json"].Revision == 2 && !entries["presets.json"].Persist);
        Assert.IsTrue(entries["both"].Persist && entries["both"].Revision == 3);
        Assert.IsTrue(entries.ContainsKey("sub/x.cfg"));
        Assert.IsTrue(InstallKeep.Covers(entries.Values, "config/a/b.json") && !InstallKeep.Covers(entries.Values, "configx.json"));
    }

    [TestMethod] public void UpdateCarriesExactlyTheListedPlayerFiles()
    {
        InstallVersionOneWithPlayerFiles();
        Stage(2); Apply();
        AssertVersionTwoWithPlayerFiles();
    }

    [TestMethod]
    [DataRow("journal")][DataRow("carried")][DataRow("old-moved")][DataRow("new-moved")][DataRow("receipt")][DataRow("backup-deleted")]
    public void CrashAtEveryStepIsFinishedByTheNextStartBeforeLoading(string step)
    {
        InstallVersionOneWithPlayerFiles();
        string oldA = TreeId(Target(0)), oldB = TreeId(Target(1));
        Stage(2);
        InstallSwap.Fault = s => { if (s == step) throw new InstallCrashSimulation(s); };
        Assert.ThrowsExactly<InstallCrashSimulation>(Apply);
        InstallSwap.Fault = null;
        // Whatever BepInEx could see right now: each root complete old, complete new, or missing.
        foreach (var (target, old) in new[] { (Target(0), oldA), (Target(1), oldB) }) {
            string id = TreeId(target);
            Assert.IsTrue(id == "missing" || id == old || InstallTree.ReadMarker(target) != null, "partial root " + target);
        }
        Preloader();
        AssertVersionTwoWithPlayerFiles();
    }

    [TestMethod] public void CrashDuringRecoveryIsRecoveredAgain()
    {
        InstallVersionOneWithPlayerFiles();
        Stage(2);
        InstallSwap.Fault = s => { if (s == "old-moved") throw new InstallCrashSimulation(s); };
        Assert.ThrowsExactly<InstallCrashSimulation>(Apply);
        InstallSwap.Fault = s => { if (s == "recover-new-moved") throw new InstallCrashSimulation(s); };
        Assert.ThrowsExactly<InstallCrashSimulation>(() => InstallSwap.Recover(paths, paths.Journal(Id), _ => { }));
        InstallSwap.Fault = null;
        Preloader();
        AssertVersionTwoWithPlayerFiles();
    }

    // Root A is already moved aside when root B cannot be moved: the swap is undone at once, so
    // this start still loads the complete old version; the request waits for the next start.
    [TestMethod] public void SecondRootLockedUndoesTheFirstAndKeepsTheRequest()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows file sharing semantics."); return; }
        InstallVersionOneWithPlayerFiles();
        string oldA = TreeId(Target(0)), oldB = TreeId(Target(1));
        Stage(2);
        bool failed = false;
        using (new FileStream(Path.Combine(Target(1), "b.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            try { Apply(); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { failed = true; }
        Assert.IsTrue(failed);
        Assert.AreEqual(oldA, TreeId(Target(0))); Assert.AreEqual(oldB, TreeId(Target(1)));
        Assert.IsFalse(File.Exists(paths.Journal(Id)));
        Assert.IsTrue(InstallStaging.Pending(paths, Id));
        Assert.AreEqual("{\"Version\":\"1\"}", File.ReadAllText(paths.Receipt(Id)));
        Preloader();
        AssertVersionTwoWithPlayerFiles();
    }

    // Same for a removal: root A must not stay removed while root B is still loaded.
    [TestMethod] public void SecondRootLockedUndoesARemovalAndKeepsTheRequest()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows file sharing semantics."); return; }
        InstallVersionOneWithPlayerFiles();
        string oldA = TreeId(Target(0)), oldB = TreeId(Target(1));
        InstallStaging.StageRemove(paths, Id, "PkgId", Targets, _ => { });
        bool failed = false;
        using (new FileStream(Path.Combine(Target(1), "b.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            try { Apply(); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { failed = true; }
        Assert.IsTrue(failed);
        Assert.AreEqual(oldA, TreeId(Target(0))); Assert.AreEqual(oldB, TreeId(Target(1)));
        Assert.IsFalse(File.Exists(paths.Journal(Id)));
        Assert.IsTrue(InstallStaging.Pending(paths, Id));
        Preloader();
        Assert.IsFalse(Directory.Exists(Target(0))); Assert.IsFalse(Directory.Exists(Target(1)));
        AssertClean();
    }

    // A swap whose cleanup is deferred is installed: it must not show as pending in this session.
    [TestMethod] public void DeferredCleanupIsNotReportedAsPending()
    {
        Stage(1);
        InstallSwap.Fault = s => { if (s == "backup-deleted") throw new IOException("briefly locked"); };
        Apply();
        InstallSwap.Fault = null;
        Assert.AreEqual("a1", Read("PkgA/a.dll"));
        Assert.IsTrue(File.Exists(paths.Journal(Id)), "journal finishes the cleanup");
        Assert.IsFalse(InstallStaging.Pending(paths, Id));
        Preloader();
        AssertClean();
    }

    // A failed attempt already carried player files; the player then changes one and deletes
    // another before the retry: newest data wins, the deleted one falls back to the shipped file.
    [TestMethod] public void RetryUsesTheCurrentPlayerState()
    {
        InstallVersionOneWithPlayerFiles();
        Stage(2);
        InstallSwap.Fault = s => { if (s == "carried") throw new InstallCrashSimulation(s); };
        Assert.ThrowsExactly<InstallCrashSimulation>(Apply);
        InstallSwap.Fault = null;
        InstallSwap.Recover(paths, paths.Journal(Id), _ => { });
        File.WriteAllText(Path.Combine(Target(0), "config", "user.cfg"), "changed later");
        File.Delete(Path.Combine(Target(0), "settings.json"));
        Apply();
        Assert.AreEqual("changed later", Read("PkgA/config/user.cfg"));
        Assert.AreEqual("default-2", Read("PkgA/settings.json"));
        AssertClean();
    }

    [TestMethod] public void ResetRevisionReplacesThePlayerFileExactlyOnce()
    {
        InstallVersionOneWithPlayerFiles();
        Stage(2, "settings.json 1\nconfig/\n"); Apply();
        Assert.AreEqual("default-2", Read("PkgA/settings.json"));
        Assert.AreEqual("player config", Read("PkgA/config/user.cfg"));
        File.WriteAllText(Path.Combine(Target(0), "settings.json"), "player after reset");
        Stage(3, "settings.json 1\nconfig/\n"); Apply();
        Assert.AreEqual("player after reset", Read("PkgA/settings.json"));
        // A reset of a folder the package does not ship removes the player's folder once.
        Stage(4, "settings.json 1\nconfig/ 1\n"); Apply();
        Assert.IsFalse(Directory.Exists(Path.Combine(Target(0), "config")));
        // Dropping an entry from the list stops carrying it.
        Stage(5, "config/ 1\n"); Apply();
        Assert.AreEqual("default-5", Read("PkgA/settings.json"));
    }

    [TestMethod] public void RemovalParksPersistEntriesAndSurvivesACrash()
    {
        InstallVersionOneWithPlayerFiles();
        InstallStaging.StageRemove(paths, Id, "PkgId", Targets, _ => { });
        InstallSwap.Fault = s => { if (s == "old-moved") throw new InstallCrashSimulation(s); };
        Assert.ThrowsExactly<InstallCrashSimulation>(Apply);
        InstallSwap.Fault = null;
        Preloader();
        Assert.IsFalse(Directory.Exists(Target(0))); Assert.IsFalse(Directory.Exists(Target(1)));
        Assert.IsFalse(File.Exists(paths.Receipt(Id)));
        AssertClean();
        // Nothing was marked persist here: a reinstallation starts with shipped files only.
        Stage(2); Apply();
        Assert.AreEqual("default-2", Read("PkgA/settings.json"));
        Assert.AreEqual("<missing>", Read("PkgA/config/user.cfg"));
    }

    [TestMethod] public void PersistEntriesReturnAfterUninstallAndReinstall()
    {
        Stage(1, "settings.json persist\nconfig/\n"); Apply();
        File.WriteAllText(Path.Combine(Target(0), "settings.json"), "player settings");
        Write(Target(0), new Dictionary<string, string> { ["config/user.cfg"] = "player config" });
        InstallStaging.StageRemove(paths, Id, "PkgId", Targets, _ => { }); Apply();
        Assert.IsFalse(Directory.Exists(Target(0)));
        Stage(2, "settings.json persist\nconfig/\n"); Apply();
        Assert.AreEqual("player settings", Read("PkgA/settings.json"));
        Assert.AreEqual("<missing>", Read("PkgA/config/user.cfg"));
        Assert.IsFalse(Directory.Exists(paths.Park("PkgId", Targets[0])));
    }

    [TestMethod] public void IncompleteContainersAndOpenJournalsAreProtected()
    {
        Stage(1);
        File.Delete(Path.Combine(paths.Container(Id), InstallTree.CompleteName));
        Assert.ThrowsExactly<InvalidOperationException>(Apply);
        Assert.IsFalse(Directory.Exists(Target(0)));
        Stage(1);
        InstallSwap.Fault = s => { if (s == "old-moved" || s == "new-moved") throw new InstallCrashSimulation(s); };
        Assert.ThrowsExactly<InstallCrashSimulation>(Apply);
        InstallSwap.Fault = null;
        Assert.ThrowsExactly<InvalidOperationException>(() => InstallStaging.Withdraw(paths, Id), "an open journal belongs to the preloader");
        Preloader();
        Assert.AreEqual("a1", Read("PkgA/a.dll"));
    }

    [TestMethod] public void DamagedContainerIsDiscardedInsteadOfFailingAtEveryStart()
    {
        InstallVersionOneWithPlayerFiles();
        string old = TreeId(Target(1));
        Stage(2);
        Directory.Delete(Path.Combine(paths.Container(Id), "1"), true);
        Assert.ThrowsExactly<InvalidDataException>(Apply);
        Assert.AreEqual(old, TreeId(Target(1)));
        Assert.AreEqual("a1", Read("PkgA/a.dll"));
        Assert.IsFalse(InstallStaging.Pending(paths, Id));
        AssertClean();
    }

    [TestMethod] public void PreloaderRecordsItsOutcomeForThePlugin()
    {
        Assert.HasCount(0, InstallRunner.TakeResult(paths));
        InstallRunner.Run(Game, _ => { }, _ => { }); // nothing to do: silent, no result file
        Assert.IsFalse(File.Exists(Path.Combine(paths.Work, InstallRunner.ResultName)));
        Stage(1); Preloader();
        var lines = InstallRunner.TakeResult(paths);
        Assert.IsTrue(lines.Any(l => !l.Error && l.Text.StartsWith("INSTALLED " + Id)));
        Assert.HasCount(0, InstallRunner.TakeResult(paths), "the result is reported once");
    }

    // Version 2 ships only root A: root B of version 1 must leave BepInEx/plugins in the same
    // transaction (stale plugins would keep loading), its persist entries parked, at every crash point.
    [TestMethod]
    [DataRow("")][DataRow("journal")][DataRow("carried")][DataRow("old-moved")][DataRow("new-moved")][DataRow("receipt")][DataRow("backup-deleted")]
    public void UpdateRemovesARootTheNewVersionNoLongerShips(string step)
    {
        InstallVersionOneWithPlayerFiles();
        // Root B's installed keep list (as if version 1 shipped it) marks a player file persist.
        Write(Target(1), new Dictionary<string, string> { ["b.cfg"] = "player b", ["apishared-keep.txt"] = "b.cfg persist\n" });
        string source = Path.Combine(fixture, "src-dropped");
        Write(source, Version(2).Where(f => f.Key.StartsWith("PkgA/")).ToDictionary(f => f.Key, f => f.Value));
        string dir = Path.Combine(source, "PkgA");
        var rootA = new InstallRoot(Targets[0], dir, InstallTree.Files(dir).Select(p => new InstallFile(p, new FileInfo(Path.Combine(dir, p)).Length, InstallTree.Sha256(Path.Combine(dir, p)))).ToList());
        InstallStaging.StageInstall(paths, Id, "PkgId", new[] { rootA }, "{\"Version\":\"2\"}", _ => { }, new[] { Targets[1] });
        if (step.Length > 0) {
            InstallSwap.Fault = s => { if (s == step) throw new InstallCrashSimulation(s); };
            Assert.ThrowsExactly<InstallCrashSimulation>(Apply);
            InstallSwap.Fault = null;
            Preloader();
        } else Apply();
        Assert.IsFalse(Directory.Exists(Target(1)), "dropped root removed");
        Assert.AreEqual("a2", Read("PkgA/a.dll")); Assert.AreEqual("player settings", Read("PkgA/settings.json"));
        Assert.AreEqual("player b", File.ReadAllText(Path.Combine(paths.Park("PkgId", Targets[1]), "b.cfg")), "persist entry of the dropped root parked");
        Assert.AreEqual("{\"Version\":\"2\"}", File.ReadAllText(paths.Receipt(Id)));
        Assert.IsFalse(File.Exists(paths.Journal(Id))); Assert.IsFalse(Directory.Exists(paths.Container(Id)));
    }

    // Finish deletes plan.txt first; a crash while the rest of the container is deleted (any order)
    // is completed by the next start instead of failing at every start on the missing receipt copy.
    [TestMethod] public void CrashWhileDeletingTheContainerCompletesAtTheNextStart()
    {
        Stage(1);
        InstallSwap.Fault = s => { if (s == "backup-deleted") throw new InstallCrashSimulation(s); };
        Assert.ThrowsExactly<InstallCrashSimulation>(Apply);
        InstallSwap.Fault = null;
        File.Delete(Path.Combine(paths.Container(Id), InstallPlanFile.Name));
        File.Delete(Path.Combine(paths.Container(Id), InstallPlanFile.ReceiptName));
        Preloader();
        Assert.AreEqual("a1", Read("PkgA/a.dll"));
        Assert.AreEqual("{\"Version\":\"1\"}", File.ReadAllText(paths.Receipt(Id)));
        AssertClean();
    }

    // A folder recreated where a moved-aside root belongs (e.g. a manual copy) blocks the roll
    // back of that root; the backup holds the only copy of the player files and must survive the
    // following installation instead of being deleted as a leftover.
    [TestMethod] public void RollbackKeepsABackupThatCannotReturn()
    {
        InstallVersionOneWithPlayerFiles();
        Stage(2);
        InstallSwap.Fault = s => { if (s == "old-moved") throw new InstallCrashSimulation(s); };
        Assert.ThrowsExactly<InstallCrashSimulation>(Apply);
        InstallSwap.Fault = null;
        Write(Target(0), new Dictionary<string, string> { ["manual.txt"] = "manual copy" });
        Preloader();
        Assert.AreEqual("a2", Read("PkgA/a.dll"));
        var kept = Directory.GetDirectories(paths.Backups, Id + "-0-kept-*");
        Assert.HasCount(1, kept);
        Assert.AreEqual("player settings", File.ReadAllText(Path.Combine(kept[0], "settings.json")));
    }

    [TestMethod] public void LinkedInstalledRootIsNeverMoved()
    {
        InstallVersionOneWithPlayerFiles();
        string outside = Path.Combine(fixture, "outside");
        Directory.Move(Target(1), outside);
        CreateDirectoryLink(Target(1), outside);
        try {
            Stage(2);
            Assert.ThrowsExactly<IOException>(Apply);
            Assert.IsFalse(File.Exists(paths.Journal(Id)));
            Assert.IsTrue(InstallStaging.Pending(paths, Id));
            Assert.AreEqual("b1", File.ReadAllText(Path.Combine(outside, "b.dll")));
        } finally { if (Directory.Exists(Target(1))) Directory.Delete(Target(1)); }
    }

    // An installed keep list edited into an invalid state must not block the removal forever.
    [TestMethod] public void InvalidInstalledKeepListDoesNotBlockRemoval()
    {
        InstallVersionOneWithPlayerFiles();
        File.WriteAllText(Path.Combine(Target(0), "apishared-keep.txt"), "../outside\n");
        InstallStaging.StageRemove(paths, Id, "PkgId", Targets, _ => { });
        Apply();
        Assert.IsFalse(Directory.Exists(Target(0)));
        AssertClean();
    }

    // A keep entry whose kind changes (shipped file becomes a folder) keeps the shipped version.
    [TestMethod] public void KeepEntryOfChangedKindKeepsTheShippedVersion()
    {
        Stage(1, "settings.json\nmods/\n"); Apply();
        File.WriteAllText(Path.Combine(Target(0), "settings.json"), "player settings");
        string source = Path.Combine(fixture, "src-kind");
        var files = Version(2, "settings.json\n").Where(f => f.Key != "PkgA/settings.json").ToDictionary(f => f.Key, f => f.Value);
        files["PkgA/settings.json/inner.txt"] = "now a folder";
        Write(source, files);
        var roots = Targets.Select(target => {
            string dir = Path.Combine(source, target.Split('/')[2]);
            return new InstallRoot(target, dir, InstallTree.Files(dir).Select(p => new InstallFile(p, new FileInfo(Path.Combine(dir, p)).Length, InstallTree.Sha256(Path.Combine(dir, p)))).ToList());
        }).ToList();
        InstallStaging.StageInstall(paths, Id, "PkgId", roots, "{}", _ => { });
        Apply();
        Assert.AreEqual("now a folder", Read("PkgA/settings.json/inner.txt"));
        AssertClean();
    }

    // An external mod manager took the item over after a change was staged: the preloader discards
    // the change and leaves the installed version alone.
    [TestMethod] public void ManagedItemsStagedChangeIsDiscardedByThePreloader()
    {
        InstallVersionOneWithPlayerFiles();
        string oldA = TreeId(Target(0));
        Stage(2);
        var messages = new List<string>();
        InstallRunner.Run(Game, messages.Add, message => throw new AssertFailedException("preloader error: " + message), id => id == Id);
        Assert.AreEqual(oldA, TreeId(Target(0)));
        Assert.IsFalse(InstallStaging.Pending(paths, Id));
        Assert.IsTrue(messages.Any(m => m.StartsWith("WITHDRAWN " + Id)));
        AssertClean();
    }

    // A batch stages without requests and publishes them last: an unpublished container (game
    // quit while copying) is never applied.
    [TestMethod] public void UnpublishedContainerIsIgnoredUntilPublished()
    {
        string source = Path.Combine(fixture, "src-unpublished");
        Write(source, Version(1));
        var roots = Targets.Select(target => {
            string dir = Path.Combine(source, target.Split('/')[2]);
            return new InstallRoot(target, dir, InstallTree.Files(dir).Select(p => new InstallFile(p, new FileInfo(Path.Combine(dir, p)).Length, InstallTree.Sha256(Path.Combine(dir, p)))).ToList());
        }).ToList();
        InstallStaging.StageInstall(paths, Id, "PkgId", roots, "{}", _ => { }, request: false);
        Assert.IsFalse(InstallStaging.Pending(paths, Id));
        Preloader();
        Assert.IsFalse(Directory.Exists(Target(0)));
        InstallStaging.Publish(paths, Id);
        Preloader();
        Assert.AreEqual("a1", Read("PkgA/a.dll"));
    }

    // A move that fails once (e.g. a root briefly locked) and is completed by the immediate
    // recovery is an installed change, not a failure the player is told about.
    [TestMethod] public void TransientMoveFailureCompletedByRecoveryIsASuccess()
    {
        InstallVersionOneWithPlayerFiles();
        Stage(2);
        bool failed = false;
        InstallSwap.Fault = s => { if (s == "new-moved" && !failed) { failed = true; throw new IOException("briefly locked"); } };
        Assert.AreEqual("install", InstallSwap.Apply(paths, Id, _ => { }));
        InstallSwap.Fault = null;
        AssertVersionTwoWithPlayerFiles();
    }

    // Old kept folder config/x/ (player files) while the new version ships a file config/x: the
    // shipped file wins instead of the update failing at every start.
    [TestMethod] public void NestedKindClashKeepsTheShippedFile()
    {
        InstallVersionOneWithPlayerFiles();
        Write(Target(0), new Dictionary<string, string> { ["config/x/y.cfg"] = "player nested" });
        string source = Path.Combine(fixture, "src-clash");
        var files = Version(2); files["PkgA/config/x"] = "shipped file";
        Write(source, files);
        var roots = Targets.Select(target => {
            string dir = Path.Combine(source, target.Split('/')[2]);
            return new InstallRoot(target, dir, InstallTree.Files(dir).Select(p => new InstallFile(p, new FileInfo(Path.Combine(dir, p)).Length, InstallTree.Sha256(Path.Combine(dir, p)))).ToList());
        }).ToList();
        InstallStaging.StageInstall(paths, Id, "PkgId", roots, "{}", _ => { });
        Apply();
        Assert.AreEqual("shipped file", Read("PkgA/config/x"));
        Assert.AreEqual("player config", Read("PkgA/config/user.cfg"));
        AssertClean();
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        // A junction needs no developer mode or symlink privilege.
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null" }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        Assert.IsTrue(process.WaitForExit(10000), "Junction fixture creation timed out.");
        Assert.AreEqual(0, process.ExitCode, "Junction fixture could not be created.");
    }

    [TestMethod] public void PlanTargetsMustBePluginRoots()
    {
        foreach (string target in new[] { "BepInEx/plugins", "BepInEx/plugins/a/b", "BepInEx/patchers/x", "../x", "BepInEx/plugins/.." })
            Assert.IsFalse(InstallPlanFile.IsPluginRoot(target), target);
        Assert.IsTrue(InstallPlanFile.IsPluginRoot("BepInEx/plugins/Example_Serp"));
    }
}
