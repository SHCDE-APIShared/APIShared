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
                host = drive.Value.TrimEnd('/') + "/" + path.Substring(drive.Key.Length).Replace('\\', '/');
                if (host.Length > 1) host = host.TrimEnd('/');
                return true;
            }
            return false;
        }
        public bool TryToWindows(string host, out string path)
        {
            if (host == "/proc/sys/kernel/ostype" && KernelPath != null) { Calls++; path = KernelPath; return true; }
            if (NoBash && (host == "/bin/bash" || host == "/usr/bin/bash")) { Calls++; path = null!; return false; }
            Calls++; path = @"Z:\" + (BadRoundTrip ? "wrong" : host.TrimStart('/').Replace('/', '\\'));
            return true;
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

    [TestMethod]
    public void FailedOwnedProbeReportsItsExitAndCapturesBothPipes()
    {
        var start = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/d /c \"echo out & echo probe-error 1>&2 & exit /b 7\"")
            : new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c \"printf out; printf probe-error >&2; exit 7\"");
        var failure = Assert.ThrowsExactly<IOException>(() => ProtonProcess.Capture(start, TimeSpan.FromSeconds(5)));
        StringAssert.Contains(failure.Message, "exit 7");
        StringAssert.Contains(failure.Message, "probe-error");
    }
}
