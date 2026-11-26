using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WorkshopPathLinkTests
{
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
}
