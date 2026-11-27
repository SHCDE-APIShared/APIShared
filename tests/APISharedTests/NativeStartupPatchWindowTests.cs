using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using System.Threading;

// Startup detours skip RedBird's per-patch thread suspension only inside the synchronous
// LibraryLoaded run on the thread that opened the window; every other installation keeps it.
// The test host has no Unity dispatcher, so the window here ends with APIShared's own handler.
[TestClass]
[DoNotParallelize]
public sealed class NativeStartupPatchWindowTests
{
    private static readonly NativeDetourBackend Suspending = new NativeDetourBackend(
        new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false });

    [TestInitialize]
    [TestCleanup]
    public void Reset() => NativeStartupPatchWindow.ResetForTest();

    [TestMethod]
    public void ClosedWindowKeepsEveryBackend()
    {
        Assert.IsTrue(NativeStartupPatchWindow.SuspendThreads);
        Assert.AreSame(Suspending, NativeStartupPatchWindow.Backend(Suspending));
        Assert.IsNull(NativeStartupPatchWindow.Backend(), "The transaction default must stay untouched.");
    }

    [TestMethod]
    public void OpenWindowRemovesOnlyThreadSuspension()
    {
        NativeStartupPatchWindow.OpenForStartupRun();
        Assert.IsFalse(NativeStartupPatchWindow.SuspendThreads);

        var adapted = NativeStartupPatchWindow.Backend(Suspending) as NativeDetourBackend;
        Assert.IsNotNull(adapted);
        Assert.AreEqual(Suspending.Options with { SuspendThreadsDuringPatch = false }, adapted.Options,
            "Only thread suspension may change; scheme and jump policies stay audited.");

        var defaulted = NativeStartupPatchWindow.Backend() as NativeDetourBackend;
        Assert.IsNotNull(defaulted);
        Assert.AreEqual(NativeDetourBackend.Instance.Options with { SuspendThreadsDuringPatch = false }, defaulted.Options);

        var alreadyFree = new NativeDetourBackend(Suspending.Options with { SuspendThreadsDuringPatch = false });
        Assert.AreSame(alreadyFree, NativeStartupPatchWindow.Backend(alreadyFree));
    }

    [TestMethod]
    public void OtherThreadsKeepThreadSuspension()
    {
        NativeStartupPatchWindow.OpenForStartupRun();
        bool otherOpen = true;
        IHookBackend otherBackend = null;
        var thread = new Thread(() =>
        {
            otherOpen = NativeStartupPatchWindow.IsOpen;
            otherBackend = NativeStartupPatchWindow.Backend(Suspending);
        });
        thread.Start();
        thread.Join();
        Assert.IsFalse(otherOpen);
        Assert.AreSame(Suspending, otherBackend);
        Assert.IsTrue(NativeStartupPatchWindow.IsOpen, "The opening thread lost its window.");
    }

    [TestMethod]
    public void WindowClosesWithTheHandlerAndOpensOncePerProcess()
    {
        NativeStartupPatchWindow.OpenForStartupRun();
        NativeStartupPatchWindow.EndApiSharedHandler();
        Assert.IsFalse(NativeStartupPatchWindow.IsOpen);
        Assert.AreSame(Suspending, NativeStartupPatchWindow.Backend(Suspending));

        NativeStartupPatchWindow.OpenForStartupRun();
        Assert.IsFalse(NativeStartupPatchWindow.IsOpen, "A second LibraryLoaded run must not reopen the window.");
    }
}
