using APIShared.Workshop.Proton;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class ProtonPathsTests
{
    private sealed class Wine : IWinePathApi
    {
        internal bool WineHost = true;
        internal bool BadRoundTrip;
        internal string KernelPath;
        internal bool NoBash;
        // Wine before 10.13 (Proton 10 and older): wine_get_unix_file_name reports
        // "<prefix>/dosdevices/<x>:/..." and strips only a Z: that maps to "/";
        // wine_get_dos_file_name picks the longest matching drive root. The default
        // mode models later Wine, which reports the server's realpath.
        internal bool LegacyDosDevices;
        internal int Calls;
        internal readonly Dictionary<string, string> Drives = new(StringComparer.OrdinalIgnoreCase) {
            [@"S:\"] = "/mnt/CaseLibrary", [@"Q:\"] = "/other", [@"Z:\"] = "/"
        };
        internal readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase) {
            @"S:\", @"S:\Games", @"S:\Games\SHCDE", @"S:\Games\SHCDE\APIShared.dll",
            @"Q:\", @"Z:\", @"Z:\mnt\CaseLibrary\Games\SHCDE\APIShared.dll"
        };
        public bool IsWine => WineHost;
        public bool TryToUnix(string path, out string host)
        {
            Calls++; host = null!;
            foreach (var drive in Drives) if (path.StartsWith(drive.Key, StringComparison.OrdinalIgnoreCase)) {
                string root = LegacyDosDevices && drive.Value != "/" ? LegacyLink(drive.Key) : drive.Value;
                host = root.TrimEnd('/') + "/" + path.Substring(drive.Key.Length).Replace('\\', '/');
                if (host.Length > 1) host = host.TrimEnd('/');
                return true;
            }
            return false;
        }
        public bool TryToWindows(string host, out string path)
        {
            if (host == "/proc/sys/kernel/ostype" && KernelPath != null) { Calls++; path = KernelPath; return true; }
            if (NoBash && (host == "/bin/bash" || host == "/usr/bin/bash")) { Calls++; path = null!; return false; }
            Calls++;
            if (LegacyDosDevices && !BadRoundTrip)
                foreach (var drive in Drives.Where(d => d.Value != "/").OrderByDescending(d => d.Value.Length))
                    if (host == drive.Value || host.StartsWith(drive.Value + "/", StringComparison.Ordinal)) {
                        path = drive.Key + host.Substring(drive.Value.Length).TrimStart('/').Replace('/', '\\');
                        return true;
                    }
            path = @"Z:\" + (BadRoundTrip ? "wrong" : host.TrimStart('/').Replace('/', '\\'));
            return true;
        }
        internal static string LegacyLink(string drive) => "/pfx/dosdevices/" + char.ToLowerInvariant(drive[0]) + ":";
        // Host realpath of Wine's drive link, as ProtonProcess.ResolveDriveLink reports it.
        internal readonly Dictionary<string, int> Resolved = new(StringComparer.Ordinal);
        internal string Resolve(string link)
        {
            Resolved[link] = Resolved.TryGetValue(link, out int count) ? count + 1 : 1;
            return Drives.Single(d => d.Value != "/" && LegacyLink(d.Key) == link).Value;
        }
    }

    [TestMethod]
    public void ActualDriveMappingsPreserveCaseAndAcceptAnAliasOnRoundTrip()
    {
        var api = new Wine();
        var paths = new ProtonPaths(@"S:\Games\SHCDE", new ProtonPathOptions(), api, api.Existing.Contains);
        Assert.AreEqual("/mnt/CaseLibrary/Games/SHCDE/APIShared.dll", paths.ToHostExisting(@"S:\Games\SHCDE\APIShared.dll"));
        Assert.AreEqual("/other", paths.ToHostExisting(@"Q:\"));
        Assert.AreEqual("/", paths.ToHostExisting(@"Z:\"));
    }

    [TestMethod]
    public void MissingTargetsUseAnExistingAncestorButNeverInventAMissingDrive()
    {
        var api = new Wine();
        var paths = new ProtonPaths(@"S:\Games\SHCDE", new ProtonPathOptions(), api, api.Existing.Contains);
        Assert.AreEqual("/mnt/CaseLibrary/Games/SHCDE/New/über lang/日本.dll", paths.ToHostTarget(@"S:\Games\SHCDE\New\über lang\日本.dll"));
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => paths.ToHostTarget(@"X:\New\APIShared.dll"));
        Assert.ThrowsExactly<FileNotFoundException>(() => paths.ToHostExisting(@"S:\Games\SHCDE\missing.dll"));
    }

    // Regression: older Wine reports mapped drives through the prefix's drive symlink, which
    // the host updaters' per-component link checks refuse. Both Wine forms must yield the
    // same canonical host path, and drive aliases must agree for containment checks.
    [TestMethod]
    public void LegacyWineDriveLinksResolveToTheSameCanonicalPathsOncePerDrive()
    {
        var api = new Wine { LegacyDosDevices = true };
        var paths = new ProtonPaths(@"S:\Games\SHCDE", new ProtonPathOptions(), api, api.Existing.Contains, api.Resolve);
        Assert.AreEqual("/mnt/CaseLibrary/Games/SHCDE/APIShared.dll", paths.ToHostExisting(@"S:\Games\SHCDE\APIShared.dll"));
        Assert.AreEqual("/mnt/CaseLibrary/Games/SHCDE/APIShared.dll", paths.ToHostExisting(@"Z:\mnt\CaseLibrary\Games\SHCDE\APIShared.dll"));
        Assert.AreEqual("/mnt/CaseLibrary/Games/SHCDE/New/über lang/日本.dll", paths.ToHostTarget(@"S:\Games\SHCDE\New\über lang\日本.dll"));
        Assert.AreEqual("/mnt/CaseLibrary", paths.ToHostExisting(@"S:\"));
        Assert.AreEqual("/other", paths.ToHostExisting(@"Q:\"));
        Assert.AreEqual("/", paths.ToHostExisting(@"Z:\"));
        Assert.AreEqual(@"S:\Games\SHCDE\APIShared.dll", paths.ToWindowsExisting("/mnt/CaseLibrary/Games/SHCDE/APIShared.dll"));
        Assert.AreEqual(1, api.Resolved[Wine.LegacyLink(@"S:\")]);
        Assert.AreEqual(1, api.Resolved[Wine.LegacyLink(@"Q:\")]);
    }

    [TestMethod]
    public void UnresolvedOrInvalidWineDriveLinksAreRefused()
    {
        var api = new Wine { LegacyDosDevices = true };
        var unresolved = new ProtonPaths(@"S:\Games\SHCDE", new ProtonPathOptions(), api, api.Existing.Contains);
        Assert.ThrowsExactly<IOException>(() => unresolved.ToHostExisting(@"S:\Games\SHCDE\APIShared.dll"));
        Assert.AreEqual("/", unresolved.ToHostExisting(@"Z:\"), "A Z: mapped to the root needs no resolution.");
        foreach (string result in new[] { "", "relative", "/bad/../root", "/bad\nroot" }) {
            var invalid = new ProtonPaths(@"S:\Games\SHCDE", new ProtonPathOptions(), api, api.Existing.Contains, _ => result);
            Assert.ThrowsExactly<IOException>(() => invalid.ToHostExisting(@"S:\Games\SHCDE\APIShared.dll"));
        }
    }

    [TestMethod]
    public void ConfiguredRootAppliesOnlyToItsGameAndIsNotATextualPrefix()
    {
        var api = new Wine(); api.Existing.Add(@"S:\Games\SHCDE-other");
        var paths = new ProtonPaths(@"S:\Games\SHCDE", new ProtonPathOptions { HostGameDirectory = "/custom/Über Game" }, api, api.Existing.Contains);
        Assert.IsTrue(paths.HasGameOverride);
        Assert.AreEqual("/custom/Über Game", paths.ToHostExisting(@"S:\Games\SHCDE"));
        Assert.AreEqual("/custom/Über Game/New.dll", paths.ToHostTarget(@"S:\Games\SHCDE\New.dll"));
        Assert.AreEqual("/mnt/CaseLibrary/Games/SHCDE-other", paths.ToHostExisting(@"S:\Games\SHCDE-other"));
    }

    [TestMethod]
    public void FailedRoundTripCannotAuthorizeAHostPath()
    {
        var api = new Wine { BadRoundTrip = true };
        var paths = new ProtonPaths(@"S:\Games\SHCDE", new ProtonPathOptions(), api, api.Existing.Contains);
        Assert.ThrowsExactly<IOException>(() => paths.ToHostExisting(@"S:\Games\SHCDE\APIShared.dll"));
    }

    [TestMethod]
    public void ReverseTranslationRequiresTheSameExistingFile()
    {
        var api = new Wine();
        var paths = new ProtonPaths(@"S:\Games\SHCDE", new ProtonPathOptions(), api, api.Existing.Contains);
        Assert.AreEqual(@"Z:\mnt\CaseLibrary\Games\SHCDE\APIShared.dll", paths.ToWindowsExisting("/mnt/CaseLibrary/Games/SHCDE/APIShared.dll"));
        Assert.ThrowsExactly<IOException>(() => paths.ToWindowsExisting("/missing.dll"));
    }

    [TestMethod]
    public void InvalidRootsAndTraversalAreRejectedBeforeCallingWine()
    {
        foreach (string value in new[] { "", @"S:relative", @"S:\Games\..\elsewhere", "S:\\bad\nname", @"\\server\share\file" }) {
            var api = new Wine();
            Assert.ThrowsExactly<ArgumentException>(() => new ProtonPaths(value, new ProtonPathOptions(), api));
            Assert.AreEqual(0, api.Calls);
        }
        foreach (string value in new[] { "relative", "/bad/../root", "/bad/./root", "/bad\tname" })
            Assert.ThrowsExactly<ArgumentException>(() => new ProtonPaths(@"S:\Game", new ProtonPathOptions { HostGameDirectory = value }, new Wine()));
    }

    [TestMethod]
    public void LongPathsDoNotHitAnArtificialMaxPathLimit()
    {
        var api = new Wine();
        var paths = new ProtonPaths(@"S:\Games\SHCDE", new ProtonPathOptions(), api, api.Existing.Contains);
        string suffix = string.Join("\\", Enumerable.Repeat("long-name", 40));
        Assert.AreEqual("/mnt/CaseLibrary/Games/SHCDE/" + suffix.Replace('\\', '/'), paths.ToHostTarget(@"S:\Games\SHCDE\" + suffix));
    }

    [TestMethod]
    public void NativeWindowsRefusesContextBeforeAnyFilesystemOrInterpreterProbe()
    {
        var api = new Wine { WineHost = false };
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => ProtonExecutionContext.Create("invalid root", new ProtonPathOptions { WineBashPath = "invalid" }, TimeSpan.FromSeconds(1), api));
        Assert.AreEqual(0, api.Calls);
        Assert.IsFalse(ProtonProcess.IsLinuxWine(api));
        Assert.IsFalse(ProtonProcess.ConfirmLinuxWine(api, TimeSpan.Zero));
        Assert.AreEqual(0, api.Calls);
    }

    [TestMethod]
    public void CreateProcessQuotingPreservesQuotesAndTrailingBackslashes()
    {
        Assert.AreEqual("\"\"", ProtonProcess.Quote(""));
        Assert.AreEqual("\"/mnt/Über Game/file\"", ProtonProcess.Quote("/mnt/Über Game/file"));
        Assert.AreEqual("\"a\\\"b\"", ProtonProcess.Quote("a\"b"));
        Assert.AreEqual("\"C:\\Game\\\\\"", ProtonProcess.Quote(@"C:\Game\"));
        Assert.ThrowsExactly<ArgumentException>(() => ProtonProcess.Quote("bad\nargument"));
    }

    [TestMethod]
    public void LinuxDetectionRequiresTheActualHostKernelAndSafelyRefusesMissingFallback()
    {
        string kernel = Path.GetTempFileName();
        try {
            var api = new Wine { KernelPath = kernel, NoBash = true };
            File.WriteAllText(kernel, "Linux\n");
            Assert.IsTrue(ProtonProcess.IsLinuxWine(api));
            Assert.IsTrue(ProtonProcess.ConfirmLinuxWine(api, TimeSpan.FromSeconds(1)));
            File.WriteAllText(kernel, "Darwin\n");
            Assert.IsFalse(ProtonProcess.IsLinuxWine(api));
            Assert.IsFalse(ProtonProcess.ConfirmLinuxWine(api, TimeSpan.FromSeconds(1)));
            File.Delete(kernel);
            Assert.IsFalse(ProtonProcess.IsLinuxWine(api));
            Assert.IsFalse(ProtonProcess.ConfirmLinuxWine(api, TimeSpan.FromSeconds(1)));
        } finally { File.Delete(kernel); }
    }

    [TestMethod]
    public void ContextRejectsInvalidExplicitRootsBeforeNativeTranslation()
    {
        var api = new Wine();
        Assert.ThrowsExactly<ArgumentException>(() => ProtonExecutionContext.Create(@"S:\Game",
            new ProtonPathOptions { HostGameDirectory = "relative" }, TimeSpan.FromSeconds(1), api));
        Assert.AreEqual(0, api.Calls);
        Assert.ThrowsExactly<TimeoutException>(() => ProtonProcess.Remaining(System.Diagnostics.Stopwatch.StartNew(), TimeSpan.Zero));
    }

    [TestMethod]
    public void NativePlatformNeverCallsUnavailableWineTranslationExports()
    {
        if (WinePathInterop.Instance.IsWine) return; // Real Wine still requires its separate integration acceptance.
        Assert.IsFalse(WinePathInterop.Instance.TryToUnix(@"S:\Game", out _));
        Assert.IsFalse(WinePathInterop.Instance.TryToWindows("/tmp", out _));
    }

    // Under Wine a started Linux program has no process handle and no connected stdio,
    // so host commands report only through files (HostRun). These cases run that real
    // protocol with a local Bash (Git Bash on Windows, /bin/bash on Linux CI); Wine and
    // Unity Mono themselves remain a separate Proton acceptance.
    private sealed class LocalBashHost : IWinePathApi
    {
        public bool IsWine => true;
        public bool TryToUnix(string path, out string host)
        {
            host = OperatingSystem.IsWindows() ? "/" + char.ToLowerInvariant(path[0]) + path.Substring(2).Replace('\\', '/') : path;
            return true;
        }
        public bool TryToWindows(string host, out string path) { path = null!; return false; }
    }

    private static string LocalBash()
    {
        string bash = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")
            : "/bin/bash";
        if (!File.Exists(bash)) Assert.Inconclusive("No local Bash for the host-run protocol: " + bash);
        return bash;
    }

    [TestMethod]
    public void HostCommandsReportOutputErrorsAndExitThroughFilesOnly()
    {
        string bash = LocalBash();
        var host = new LocalBashHost();
        var budget = TimeSpan.FromSeconds(30);
        Assert.AreEqual("out", ProtonProcess.RunHost(host, bash, new[] { "-c", "printf out" }, budget));
        var failure = Assert.ThrowsExactly<IOException>(() => ProtonProcess.RunHost(host, bash,
            new[] { "-c", "printf out; printf probe-error >&2; exit 7" }, budget));
        StringAssert.Contains(failure.Message, "exit 7");
        StringAssert.Contains(failure.Message, "probe-error");
        // Values arrive as separate arguments and are never evaluated as shell text.
        Assert.AreEqual("a \"b\";$(false) \\\n", ProtonProcess.RunHost(host, bash,
            new[] { "-c", "printf '%s\\n' \"$1\"", "probe", "a \"b\";$(false) \\" }, budget));
        Assert.ThrowsExactly<TimeoutException>(() => ProtonProcess.RunHost(host, bash, new[] { "-c", "sleep 5" }, TimeSpan.FromMilliseconds(300)));
    }

    [TestMethod]
    public void UnfinishedHostRunsKeepTheirDirectoryAndFinishedRunsRemoveIt()
    {
        string bash = LocalBash();
        string root = Path.Combine(Path.GetTempPath(), "apishared-host-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try {
            var run = HostRun.Start(new LocalBashHost(), bash, new[] { "-c", "sleep 1; exit 3" }, root);
            Assert.IsFalse(run.TryGetExit(out _), "The exit record must not exist before the program ends.");
            run.Dispose();
            Assert.IsTrue(Directory.Exists(run.RunDirectory), "A running program may still write its records.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int code;
            while (!run.TryGetExit(out code)) {
                if (watch.Elapsed > TimeSpan.FromSeconds(30)) Assert.Fail("The host run never published its exit record.");
                Thread.Sleep(20);
            }
            Assert.AreEqual(3, code);
            run.Dispose();
            Assert.IsFalse(Directory.Exists(run.RunDirectory), "A finished run removes its owned records.");
        } finally {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
