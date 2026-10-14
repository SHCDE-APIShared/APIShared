using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
using System.Text;

// Compares the startup pre-check with what the real maintenance scripts do. For
// every fixture: the script may change files only when the pre-check said so
// (soundness), and outside the documented hash-only over-approximations the
// pre-check is exact, including on the state each pass leaves behind.
[TestClass]
public sealed class WorkshopMaintenancePlanTests
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private string fixture = null!;
    [TestInitialize] public void Init() => fixture = Path.Combine(Path.GetTempPath(), "APIShared-MaintenancePlan-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup()
    {
        if (!Directory.Exists(fixture)) return;
        // Remove junction fixtures as links, never their targets.
        foreach (string link in Directory.EnumerateDirectories(fixture, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)).ToArray())
            Directory.Delete(link);
        Directory.Delete(fixture, true);
    }

    private sealed class Case
    {
        internal string Name = "";
        internal Action<Builder> Build = _ => { };
        // Only payload/pin hash differences and an unreadable worklist may make
        // the pre-check request a pass that changes nothing.
        internal bool OverApproximates;
    }

    private sealed class Builder
    {
        internal readonly string Game, State, Transactions;
        internal Builder(string game) { Game = game; State = Path.Combine(game, "_APIShared", "Workshop"); Transactions = Path.Combine(State, "Transactions"); }
        internal static string Hash(string path) => WorkshopInventory.Hash(path);
        internal void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, Utf8); }
        internal string Dir(int number) => Path.Combine(Transactions, number.ToString("x32"));
        // Mirrors Test-WorkshopMaintenance.ps1 New-Transaction: one payload row,
        // one receipt row per item, committed (or open) journal, UTC sidecar or
        // legacy journal time.
        internal string Transaction(int number, string[] items = null!, bool open = false, bool legacy = false, bool keepNew = true, string journal = null!)
        {
            string dir = Dir(number);
            Write(Path.Combine(dir, "New", "0"), "new " + number); Write(Path.Combine(dir, "Backup", "0"), "old " + number);
            var rows = new List<string> { "APISHARED-WORKSHOP-2", "write\tBepInEx/plugins/Example/Example.dll\t0\t" + Hash(Path.Combine(dir, "Backup", "0")) + "\t" + Hash(Path.Combine(dir, "New", "0")) };
            int ordinal = 1;
            foreach (string item in items ?? new[] { "123" }) {
                Write(Path.Combine(dir, "New", ordinal.ToString()), "receipt " + number);
                rows.Add("receipt-write\t_APIShared/Workshop/Receipts/" + item + ".json\t" + ordinal + "\tmissing\t" + Hash(Path.Combine(dir, "New", ordinal.ToString())));
                ordinal++;
            }
            Write(Path.Combine(dir, "operations.tsv"), string.Join("\r\n", rows) + "\r\n");
            Write(Path.Combine(dir, "operations.sha256"), Hash(Path.Combine(dir, "operations.tsv")));
            Write(Path.Combine(dir, "journal.log"), journal ?? (open ? "applying\r\nbegin\t0\r\n" : "applying\r\ncommitted\r\n"));
            var when = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(number);
            if (legacy) File.SetLastWriteTimeUtc(Path.Combine(dir, "journal.log"), when);
            else Write(Path.Combine(dir, "completed.utc"), when.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
            if (!keepNew) Directory.Delete(Path.Combine(dir, "New"), true);
            return dir;
        }
        internal string Reference(string original) => Path.GetFileName(original) + "\t" + Hash(Path.Combine(original, "operations.tsv")) + "\t" + Hash(Path.Combine(original, "journal.log"));
        internal void Supersedes(string replacement, string original)
        {
            Write(Path.Combine(replacement, "supersedes.tsv"), Reference(original) + "\r\n");
            Write(Path.Combine(replacement, "supersedes.sha256"), Hash(Path.Combine(replacement, "supersedes.tsv")));
        }
        internal string Request(int number, string original, string journal, bool snapshot = true)
        {
            string dir = Dir(number);
            Write(Path.Combine(dir, "operations.tsv"), "APISHARED-WORKSHOP-RECOVERY-1\r\nstop\r\n" + Reference(original) + "\r\n");
            Write(Path.Combine(dir, "journal.log"), journal);
            if (snapshot) Write(Path.Combine(dir, "RecoveryBackup", Path.GetFileName(original), "0"), "interrupted bytes");
            return dir;
        }
        internal void Pending(string payload) => Write(Path.Combine(State, "cleanup.pending.tsv"), "APISHARED-WORKSHOP-CLEANUP-1\n" + Digest(payload) + "\n" + payload);
        internal static string Digest(string text) { using var sha = System.Security.Cryptography.SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(Utf8.GetBytes(text))).ToLowerInvariant(); }
    }

    private static IEnumerable<Case> Matrix()
    {
        yield return new Case { Name = "no-transactions-directory", Build = b => Directory.CreateDirectory(b.State) };
        yield return new Case { Name = "empty-transactions-directory", Build = b => Directory.CreateDirectory(b.Transactions) };
        yield return new Case { Name = "worklist-without-transactions-directory", Build = b => b.Pending("D\t" + new string('a', 32) + "\tall\n") };
        yield return new Case { Name = "pending-worklist", Build = b => {
            string old = b.Transaction(1); b.Transaction(2); b.Transaction(3);
            string id = Path.GetFileName(old), payload = "D\t" + id + "\tall\n";
            foreach (string file in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories)) payload += "F\t" + id + "\t" + file.Substring(old.Length + 1).Replace('\\', '/') + "\t" + Builder.Hash(file) + "\n";
            b.Pending(payload);
        } };
        yield return new Case { Name = "corrupt-pending-worklist", OverApproximates = true, Build = b => { b.Transaction(1, keepNew: false); b.Write(Path.Combine(b.State, "cleanup.pending.tsv"), "APISHARED-WORKSHOP-CLEANUP-1\ninvalid\nD\tbad\tall\n"); } };
        yield return new Case { Name = "fresh-install-with-staging-copies", Build = b => b.Transaction(1) };
        yield return new Case { Name = "two-retained-without-staging", Build = b => { b.Transaction(1, keepNew: false); b.Transaction(2, keepNew: false); } };
        yield return new Case { Name = "retention-over-limit", Build = b => { b.Transaction(1, keepNew: false); b.Transaction(2, keepNew: false); b.Transaction(3, keepNew: false); } };
        yield return new Case { Name = "legacy-prune-forty", Build = b => { for (int i = 1; i <= 40; i++) b.Transaction(i, legacy: true); } };
        yield return new Case { Name = "legacy-same-second", Build = b => {
            for (int i = 1; i <= 3; i++) { string dir = b.Transaction(i, legacy: true, keepNew: false); File.SetLastWriteTimeUtc(Path.Combine(dir, "journal.log"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(100 * (4 - i))); }
        } };
        yield return new Case { Name = "mixed-batches", Build = b => { b.Transaction(1); b.Transaction(2, new[] { "456" }); b.Transaction(3, new[] { "123", "456" }); b.Transaction(4); b.Transaction(5); } };
        yield return new Case { Name = "mixed-batches-steady", Build = b => { b.Transaction(2, new[] { "456" }, keepNew: false); b.Transaction(3, new[] { "123", "456" }, keepNew: false); b.Transaction(4, keepNew: false); b.Transaction(5, keepNew: false); } };
        yield return new Case { Name = "interrupted-only", Build = b => b.Transaction(1, open: true) };
        yield return new Case { Name = "interrupted-with-history", Build = b => { b.Transaction(1, open: true); b.Transaction(2, keepNew: false); b.Transaction(3, keepNew: false); } };
        yield return new Case { Name = "cancelled-handoff", Build = b => { b.Transaction(1, journal: "failure Handoff cancelled\r\n"); b.Transaction(2, keepNew: false); b.Transaction(3, keepNew: false); } };
        yield return new Case { Name = "staged-never-started", Build = b => { string d = b.Transaction(1); File.Delete(Path.Combine(d, "journal.log")); b.Transaction(2, keepNew: false); b.Transaction(3, keepNew: false); } };
        yield return new Case { Name = "replacement-resolves-original", Build = b => { string old = b.Transaction(1, open: true); string repair = b.Transaction(2, keepNew: false); b.Supersedes(repair, old); b.Transaction(3, keepNew: false); b.Transaction(4, keepNew: false); } };
        yield return new Case { Name = "uncommitted-replacement", Build = b => { string old = b.Transaction(1, open: true); string repair = b.Transaction(2, open: true); b.Supersedes(repair, old); } };
        yield return new Case { Name = "recovered-request-retained", Build = b => {
            string old = b.Transaction(1, journal: "applying\r\nbegin\t0\r\nrestored\r\n", keepNew: false); b.Request(2, old, "recovering\r\ncommitted\r\n"); b.Transaction(3, keepNew: false);
        } };
        yield return new Case { Name = "recovered-request-beyond-retention", Build = b => {
            string old = b.Transaction(1, journal: "applying\r\nbegin\t0\r\nrestored\r\n", keepNew: false); b.Request(2, old, "recovering\r\ncommitted\r\n"); b.Transaction(3, keepNew: false); b.Transaction(4, keepNew: false);
        } };
        yield return new Case { Name = "restored-original-with-staging", Build = b => { string old = b.Transaction(1, journal: "applying\r\nbegin\t0\r\nrestored\r\n"); b.Request(2, old, "recovering\r\ncommitted\r\n"); } };
        yield return new Case { Name = "unfinished-request", Build = b => { string old = b.Transaction(1, open: true); b.Request(2, old, "recovering\r\n"); } };
        yield return new Case { Name = "request-without-journal", Build = b => { string old = b.Transaction(1, open: true); b.Request(2, old, "", snapshot: false); File.Delete(Path.Combine(b.Dir(2), "journal.log")); } };
        yield return new Case { Name = "unknown-file-beyond-retention", Build = b => { string d = b.Transaction(1, keepNew: false); b.Write(Path.Combine(d, "user-notes.txt"), "keep"); b.Transaction(2, keepNew: false); b.Transaction(3, keepNew: false); } };
        yield return new Case { Name = "invalid-timestamp-beyond-retention", Build = b => { string d = b.Transaction(1, keepNew: false); b.Write(Path.Combine(d, "completed.utc"), "invalid"); b.Transaction(2, keepNew: false); b.Transaction(3, keepNew: false); } };
        yield return new Case { Name = "broken-operations-beyond-retention", Build = b => { string d = b.Transaction(1, keepNew: false); b.Write(Path.Combine(d, "operations.tsv"), "broken"); File.Delete(Path.Combine(d, "operations.sha256")); b.Transaction(2, keepNew: false); b.Transaction(3, keepNew: false); } };
        yield return new Case { Name = "unknown-directory-name", Build = b => { b.Write(Path.Combine(b.Transactions, "notes", "readme.txt"), "keep"); b.Transaction(1, keepNew: false); } };
        yield return new Case { Name = "changed-payload-beyond-retention", OverApproximates = true, Build = b => { string d = b.Transaction(1, keepNew: false); b.Write(Path.Combine(d, "Backup", "0"), "changed"); b.Transaction(2, keepNew: false); b.Transaction(3, keepNew: false); } };
        yield return new Case { Name = "changed-operations-pin-beyond-retention", OverApproximates = true, Build = b => { string d = b.Transaction(1, keepNew: false); b.Write(Path.Combine(d, "operations.sha256"), new string('0', 64)); b.Transaction(2, keepNew: false); b.Transaction(3, keepNew: false); } };
        if (OperatingSystem.IsWindows())
            yield return new Case { Name = "junction-beyond-retention", Build = b => {
                string d = b.Transaction(1, keepNew: false); b.Transaction(2, keepNew: false); b.Transaction(3, keepNew: false);
                string outside = Path.Combine(Path.GetDirectoryName(b.Game)!, "outside"); b.Write(Path.Combine(outside, "0"), "outside");
                Junction(Path.Combine(d, "RecoveryBackup"), outside);
            } };
    }

    private static void Junction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string a in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!; p.WaitForExit();
        Assert.AreEqual(0, p.ExitCode, "Junction fixture could not be created.");
    }

    // Everything the script could touch except its own lock file.
    private static Dictionary<string, string> Tree(string game)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(game)) return result;
        var pending = new Stack<string>(); pending.Push(game);
        while (pending.Count > 0) {
            string dir = pending.Pop();
            result[dir.Substring(game.Length) + "/"] = "dir";
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint) && dir != game) continue;
            foreach (string sub in Directory.EnumerateDirectories(dir)) pending.Push(sub);
            foreach (string file in Directory.EnumerateFiles(dir)) {
                string rel = file.Substring(game.Length).Replace('\\', '/');
                if (rel.EndsWith("/apply.lock", StringComparison.Ordinal)) continue;
                result[rel] = Builder.Hash(file);
            }
        }
        return result;
    }
    private static bool Changed(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Count != after.Count || before.Any(kv => !after.TryGetValue(kv.Key, out var v) || v != kv.Value);

    private sealed class Backend
    {
        internal string Name = "", Executable = "";
        internal Func<string, string[]> Arguments = _ => new string[0];
    }
    private static IEnumerable<Backend> Backends()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fixtures"));
        if (OperatingSystem.IsWindows())
            yield return new Backend { Name = "windows", Executable = "powershell.exe", Arguments = game => new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "workshop-updater.ps1"), "-GameRoot", game, "-Maintain" } };
        // Proton backend: real host bash/flock/coreutils. On Windows set
        // APISHARED_TEST_WSL_DISTRO (for example Debian) to run it through WSL.
        string distro = Environment.GetEnvironmentVariable("APISHARED_TEST_WSL_DISTRO");
        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(distro)) {
            static string Host(string path) => "/mnt/" + char.ToLowerInvariant(path[0]) + path.Substring(2).Replace('\\', '/');
            yield return new Backend { Name = "bash(wsl)", Executable = "wsl.exe", Arguments = game => new[] { "-d", distro, "--", "bash", Host(Path.Combine(root, "workshop-updater.sh")), Host(game), "--maintain" } };
        } else if (!OperatingSystem.IsWindows() && File.Exists("/usr/bin/flock"))
            yield return new Backend { Name = "bash", Executable = "bash", Arguments = game => new[] { Path.Combine(root, "workshop-updater.sh"), game, "--maintain" } };
    }
    private static async Task<(int Code, string Output)> RunAsync(Backend backend, string game)
    {
        var start = new ProcessStartInfo(backend.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in backend.Arguments(game)) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        return (p.ExitCode, (await o + await e).Trim());
    }

    [TestMethod] public async Task PreCheckMatchesWhatTheMaintenanceScriptsDo()
    {
        var backends = Backends().ToArray();
        if (backends.Length == 0) { Assert.Inconclusive("No maintenance script backend is available."); return; }
        var cases = Matrix().ToArray();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var report = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var slots = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount));
        await Task.WhenAll(backends.SelectMany(backend => cases.Select(async c => {
            await slots.WaitAsync();
            try {
                string game = Path.Combine(fixture, backend.Name.Replace("(", "-").Replace(")", ""), c.Name, "game");
                Directory.CreateDirectory(game);
                c.Build(new Builder(game));
                // Two passes: the fixture state, then the state the first pass left.
                for (int pass = 1; pass <= 2; pass++) {
                    bool predicted = WorkshopMaintenancePlan.MayChange(game);
                    var before = Tree(game);
                    var run = await RunAsync(backend, game);
                    bool changed = Changed(before, Tree(game));
                    report.Enqueue(backend.Name + " " + c.Name + " pass " + pass + ": predicted=" + predicted + " changed=" + changed + " exit=" + run.Code);
                    if (changed && !predicted) failures.Enqueue(backend.Name + " " + c.Name + " pass " + pass + ": script changed files but the pre-check said nothing to do. " + run.Output);
                    if (predicted && !changed && !c.OverApproximates) failures.Enqueue(backend.Name + " " + c.Name + " pass " + pass + ": pre-check requested a pass that changed nothing. " + run.Output);
                }
            } finally { slots.Release(); }
        })));
        foreach (string line in report.OrderBy(x => x, StringComparer.Ordinal)) Console.WriteLine(line);
        Assert.IsEmpty(failures, string.Join("\n", failures));
    }

    [TestMethod] public void M3JournalMarkersPreserveTheMaintenanceDecision()
    {
        var b = new Builder(Path.Combine(fixture, "m3-markers", "game"));
        string dir = b.Transaction(1, journal: "applying\r\npause 2026-10-14T21:37:28Z; waiting for game exit\r\nresumed 2026-10-14T21:37:54Z\r\nbegin\t0\r\ndone\t0\r\nbegin\t1\r\ndone\t1\r\ncommitted\r\n", keepNew: false);
        Assert.IsFalse(WorkshopMaintenancePlan.MayChange(b.Game));
        b.Write(Path.Combine(dir, "New", "0"), "remaining staging");
        Assert.IsTrue(WorkshopMaintenancePlan.MayChange(b.Game));
    }

    [TestMethod] public void SteadyHistoryNeedsNoProcess()
    {
        var b = new Builder(Path.Combine(fixture, "steady", "game"));
        Assert.IsFalse(WorkshopMaintenancePlan.MayChange(b.Game), "No Workshop state at all.");
        string old = b.Transaction(1, journal: "applying\r\nbegin\t0\r\nrestored\r\n", keepNew: false); b.Request(2, old, "recovering\r\ncommitted\r\n");
        b.Transaction(3, keepNew: false); b.Transaction(4, new[] { "456" }, keepNew: false); b.Transaction(5, open: true);
        Assert.IsFalse(WorkshopMaintenancePlan.MayChange(b.Game));
        b.Write(Path.Combine(b.Dir(4), "New", "0"), "leftover");
        Assert.IsTrue(WorkshopMaintenancePlan.MayChange(b.Game), "Staging leftovers in retained history require the pass.");
    }

    // Diagnostics policy used by WorkshopStartup: only error-level failures show a dialog.
    [TestMethod] public void OnlyErrorLevelFailuresAreUserVisible()
    {
        Assert.IsFalse(WorkshopDiagnostics.UserVisible(WorkshopDiagnostics.Blocked("downloads were pending at Steam initialization; another startup is required")));
        Assert.IsFalse(WorkshopDiagnostics.UserVisible(WorkshopDiagnostics.Blocked("Script Extender staging directory exists")));
        Assert.IsTrue(WorkshopDiagnostics.UserVisible(new IOException("Updater rejected the transaction before game exit")));
    }
}
