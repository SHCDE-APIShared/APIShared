using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;

// Process supervision for startup maintenance: the state gate is released only
// after the child has really exited.
[TestClass]
public sealed class WorkshopMaintenanceRunnerTests
{
    private static ProcessStartInfo Sleep(int seconds) => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command Start-Sleep -Seconds " + seconds) { UseShellExecute = false, CreateNoWindow = true }
        : new ProcessStartInfo("sleep", seconds.ToString()) { UseShellExecute = false };
    private static ProcessStartInfo Exit(int code) => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", "/c echo summary& exit " + code) { UseShellExecute = false, CreateNoWindow = true }
        : new ProcessStartInfo("sh", "-c \"echo summary; exit " + code + "\"") { UseShellExecute = false };
    private static bool Exited(int id) { try { using var p = Process.GetProcessById(id); return p.HasExited; } catch (ArgumentException) { return true; } }

    [TestMethod] public async Task NothingToDoStartsNoProcess()
    {
        using var gate = new SemaphoreSlim(1, 1);
        bool started = false;
        var result = await WorkshopMaintenanceRunner.RunAsync(gate, () => false, () => { started = true; return Exit(0); }, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        Assert.AreEqual(WorkshopMaintenanceExit.Skipped, result.Exit); Assert.IsFalse(started); Assert.AreEqual(1, gate.CurrentCount);
    }

    [TestMethod] public async Task CompletedPassReportsOutputAndReleasesGate()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var result = await WorkshopMaintenanceRunner.RunAsync(gate, () => true, () => Exit(3), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));
        Assert.AreEqual(WorkshopMaintenanceExit.Completed, result.Exit); Assert.AreEqual(3, result.ExitCode); Assert.AreEqual("summary", result.Output);
        Assert.AreEqual(1, gate.CurrentCount);
    }

    [TestMethod] public async Task TimedOutPassIsKilledBeforeTheGateOpens()
    {
        using var gate = new SemaphoreSlim(1, 1);
        int id = 0;
        var result = await WorkshopMaintenanceRunner.RunAsync(gate, () => true, () => Sleep(30), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10),
            p => { id = p.Id; p.Kill(); });
        Assert.AreEqual(WorkshopMaintenanceExit.Stopped, result.Exit);
        Assert.AreEqual(1, gate.CurrentCount);
        Assert.IsTrue(Exited(id), "The gate opened while the maintenance child was alive.");
    }

    [TestMethod] public async Task SurvivingChildKeepsTheGateUntilItExits()
    {
        using var gate = new SemaphoreSlim(1, 1);
        int id = 0;
        // The kill is ignored: the child outlives timeout and kill wait.
        var result = await WorkshopMaintenanceRunner.RunAsync(gate, () => true, () => Sleep(4), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), p => id = p.Id);
        Assert.AreEqual(WorkshopMaintenanceExit.StillRunning, result.Exit);
        Assert.IsFalse(Exited(id)); Assert.AreEqual(0, gate.CurrentCount, "The gate was released while the child was alive.");
        Assert.ThrowsExactly<InvalidOperationException>(() => {
            if (!gate.Wait(TimeSpan.FromMilliseconds(200))) throw new InvalidOperationException("staging deferred");
        });
        Assert.IsTrue(await gate.WaitAsync(TimeSpan.FromSeconds(30)), "The gate was not released after the child exited.");
        Assert.IsTrue(Exited(id), "The gate opened before the child exited.");
        gate.Release();
    }
}
