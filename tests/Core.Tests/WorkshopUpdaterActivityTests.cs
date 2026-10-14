using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WorkshopUpdaterActivityTests
{
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static string Record(string mode = "install", string phase = "paused", string start = "42") =>
        WorkshopUpdaterActivity.Header + "\twindows\t123\t" + start + "\t" + mode + "\t" + phase + "\t" + Id + "\t" + new string('b', 64);

    [TestMethod] public void OnlyVerifiedLivePayloadWorkCanAuthorizeClosingAStart()
    {
        var active = WorkshopUpdaterActivity.Parse(Record(), "windows", (pid, start) => pid == 123 && start == "42");
        Assert.IsTrue(active.Active); Assert.IsTrue(active.Busy); Assert.AreEqual(Id, active.Transaction);
        foreach (var record in new[] { "", "idle", "busy", Record("maintenance"), Record(phase: "completed"), Record(phase: "unknown"), Record(start: "bad"), Record() + "\textra", Record().Replace(Id, "../escape") })
            Assert.IsFalse(WorkshopUpdaterActivity.Parse(record, "windows", (_, _) => true).Active, record);
        Assert.IsFalse(WorkshopUpdaterActivity.Parse(Record(), "windows", (_, _) => false).Active);
        Assert.IsFalse(WorkshopUpdaterActivity.Parse(Record(), "linux", (_, _) => true).Active);
    }

    [TestMethod] public void LinkedInstallationStaysRejectedWithItsOriginalExceptionTypeAndBlockDiagnostic()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "APIShared-linked-status-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(fixture, "actual"), link = Path.Combine(fixture, "linked");
        Directory.CreateDirectory(outside);
        try {
            if (OperatingSystem.IsWindows()) {
                // A junction exercises the same reparse-point rejection without
                // requiring Windows developer mode or symlink privileges.
                var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                    "New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' -ErrorAction Stop | Out-Null" }) start.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(start)!;
                Assert.IsTrue(process.WaitForExit(5000), "Junction fixture creation timed out.");
                Assert.AreEqual(0, process.ExitCode, "Junction fixture could not be created.");
            } else Directory.CreateSymbolicLink(link, outside);
            var error = Assert.ThrowsExactly<InvalidDataException>(() => WorkshopPaths.CheckLinks(link));
            Assert.AreEqual(WorkshopDiagnosticLevel.Warning, WorkshopDiagnostics.Level(error));
            StringAssert.Contains(error.Message, "canonical installation path");
        } finally {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(fixture, true);
        }
    }

    [TestMethod] public void UnlockedStaleStatusAndMissingStateNeverAuthorizeQuit()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows writer sharing is tested on Windows; Proton uses the host query.");
        string game = Path.Combine(Path.GetTempPath(), "APIShared-status-" + Guid.NewGuid().ToString("N"));
        try {
            Assert.IsFalse(WorkshopUpdaterActivity.ReadWindows(game).Busy);
            string state = Path.Combine(game, "_APIShared/Workshop"); Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state, "apply.lock"), Record());
            Assert.IsFalse(WorkshopUpdaterActivity.ReadWindows(game).Busy);
            using (File.Open(Path.Combine(state, "apply.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {
                var result = WorkshopUpdaterActivity.ReadWindows(game);
                Assert.IsTrue(result.Busy); Assert.IsFalse(result.Active);
            }
        } finally { if (Directory.Exists(game)) Directory.Delete(game, true); }
    }

    [TestMethod] public void LiveWindowsOwnerRequiresReadyPinsAndAnUnfinishedJournal()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Requires actual Windows PowerShell and Windows file sharing.");
        string game = Path.Combine(Path.GetTempPath(), "APIShared-live-status-" + Guid.NewGuid().ToString("N"));
        string state = Path.Combine(game, "_APIShared/Workshop"), directory = Path.Combine(state, "Transactions", Id);
        Directory.CreateDirectory(directory);
        string operations = Path.Combine(directory, "operations.tsv"); File.WriteAllText(operations, "synthetic operation pin");
        string hash = WorkshopInventory.Hash(operations);
        File.WriteAllText(Path.Combine(directory, "ready.flag"), hash);
        File.WriteAllText(Path.Combine(directory, "journal.log"), "applying\r\n");
        string helper = Path.Combine(game, "owner.ps1"), release = Path.Combine(game, "exit.flag"), ready = Path.Combine(game, "owner-ready.flag");
        File.WriteAllText(helper, @"param($State,$Transaction,$Hash,$Ready,$Release)
$ErrorActionPreference='Stop'
$self=[Diagnostics.Process]::GetCurrentProcess()
try{$start=$self.StartTime.ToUniversalTime().Ticks.ToString()}finally{$self.Dispose()}
$stream=[IO.File]::Open((Join-Path $State 'apply.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
try{
 $record=@('APISHARED-WORKSHOP-ACTIVE-1','windows',$PID.ToString(),$start,'install','paused',$Transaction,$Hash) -join [char]9
 $bytes=[Text.Encoding]::UTF8.GetBytes($record);$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)
 [IO.File]::WriteAllText($Ready,'ready')
 $watch=[Diagnostics.Stopwatch]::StartNew()
 while(-not [IO.File]::Exists($Release)){if($watch.ElapsedMilliseconds -gt 10000){throw 'Owner fixture timed out'};Start-Sleep -Milliseconds 20}
}finally{$stream.Dispose()}");
        var startInfo = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper, state, Id, hash, ready, release }) startInfo.ArgumentList.Add(argument);
        using var owner = System.Diagnostics.Process.Start(startInfo)!;
        try {
            Assert.IsTrue(SpinWait.SpinUntil(() => File.Exists(ready) || owner.HasExited, 5000));
            Assert.IsFalse(owner.HasExited);
            var result = WorkshopUpdaterActivity.ReadWindows(game);
            Assert.IsTrue(result.Active); Assert.AreEqual(Id, result.Transaction);
            File.WriteAllText(Path.Combine(directory, "journal.log"), "applying\r\ncommitted\r\n");
            Assert.IsFalse(WorkshopUpdaterActivity.ReadWindows(game).Active);
            File.WriteAllText(Path.Combine(directory, "journal.log"), "applying\r\n");
            File.WriteAllText(Path.Combine(directory, "ready.flag"), new string('c', 64));
            Assert.IsFalse(WorkshopUpdaterActivity.ReadWindows(game).Active);
        } finally {
            File.WriteAllText(release, "exit");
            if (!owner.WaitForExit(5000)) { owner.Kill(); owner.WaitForExit(); }
            Directory.Delete(game, true);
        }
    }
}
