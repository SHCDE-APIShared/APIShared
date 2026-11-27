#nullable enable
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests;

// Tests the actual game-independent scheduler: lost invalidations, timer/cancellation
// boundaries, failure isolation and allocation regressions are public service contracts.
[TestClass]
public sealed class UiWorkTests
{
    private long now;
    private UiWorkScheduler New(Action<string, Exception>? report = null) => new(() => now, 1000, report!);
    private static IUiFrameWorkRegistration Register(UiWorkScheduler scheduler, string owner, string id, Action callback, int order = 0)
    {
        Assert.IsTrue(scheduler.TryRegister(owner, id, callback, order, out var work, out var reason), reason);
        return work;
    }

    [TestMethod]
    public void UnityBoundaryRejectsWrongThreadAndFailedOrDuplicateAttachment()
    {
        var scheduler = New(); bool unity = false; int frame = 0, subscriptions = 0, reads = 0, calls = 0;
        Action render = null!;
        var pump = new UiWorkFramePump(scheduler, () => unity, () => { reads++; return frame; });
        Assert.Throws<InvalidOperationException>(() => pump.Attach(_ => subscriptions++));
        Assert.AreEqual(0, subscriptions);
        unity = true;
        Assert.Throws<InvalidOperationException>(() => pump.Attach(_ => throw new InvalidOperationException("publisher")));
        pump.Attach(callback => { subscriptions++; render = callback; });
        pump.Attach(_ => subscriptions++); Assert.AreEqual(1, subscriptions);
        var work = Register(scheduler, "a", "a", () => calls++); work.Request();
        unity = false; render(); Assert.AreEqual(0, reads); Assert.AreEqual(0, calls);
        unity = true; render(); render(); Assert.AreEqual(1, calls);
        frame++; work.Request(); render(); Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void IdentityOrderAndDormantRegistration()
    {
        var scheduler = New(); var calls = new List<string>();
        var b = Register(scheduler, "b", "same", () => calls.Add("b"));
        var a = Register(scheduler, "a", "same", () => calls.Add("a"));
        var early = Register(scheduler, "z", "early", () => calls.Add("early"), -1);
        Assert.IsFalse(scheduler.TryRegister("a", "same", () => { }, 0, out _, out _));
        Assert.IsFalse(scheduler.TryRegister("a", "", () => { }, 0, out _, out _));
        scheduler.Pump(0); Assert.AreEqual(0, calls.Count);
        b.Request(); a.Request(); early.Request(); a.Request();
        scheduler.Pump(0); CollectionAssert.AreEqual(new[] { "early", "a", "b" }, calls);
        scheduler.Pump(0); scheduler.Pump(1); Assert.AreEqual(3, calls.Count);
    }

    [TestMethod]
    public void FrozenCallbacksAndRequestsDuringCallbacksSurvive()
    {
        var scheduler = New(); int first = 0, second = 0;
        IUiFrameWorkRegistration a = null!, b = null!;
        a = Register(scheduler, "a", "a", () => {
            first++; if (first != 1) return;
            a.Request(); b.CancelPending(); b.SetContinuous(false);
            scheduler.Pump(7); // Recursive publisher cannot invoke twice in this frame.
        });
        b = Register(scheduler, "b", "b", () => second++);
        a.Request(); b.Request(); scheduler.Pump(7);
        Assert.AreEqual(1, first); Assert.AreEqual(1, second, "Already frozen work may run once after cancellation.");
        scheduler.Pump(7); scheduler.Pump(8);
        Assert.AreEqual(2, first); Assert.AreEqual(1, second);
    }

    [TestMethod]
    public void DeadlinesCoalesceCancelAndDoNotCatchUp()
    {
        var scheduler = New(); int calls = 0; var work = Register(scheduler, "a", "a", () => calls++);
        work.RequestAfter(TimeSpan.FromMilliseconds(100));
        work.RequestAfter(TimeSpan.FromMilliseconds(200));
        now = 99; scheduler.Pump(1); Assert.AreEqual(0, calls);
        now = 100; scheduler.Pump(2); Assert.AreEqual(1, calls);
        now = 10000; scheduler.Pump(3); Assert.AreEqual(1, calls);
        work.RequestAfter(TimeSpan.FromMilliseconds(100)); work.Request(); scheduler.Pump(4);
        now += 100; scheduler.Pump(5); Assert.AreEqual(2, calls, "Immediate refresh satisfies the older deferred refresh.");
        work.RequestAfter(TimeSpan.FromMilliseconds(100)); work.CancelPending(); now += 100; scheduler.Pump(6);
        Assert.AreEqual(2, calls);
        work.SetContinuous(true); work.RequestAfter(TimeSpan.FromMilliseconds(100)); scheduler.Pump(7);
        work.SetContinuous(false); now += 100; scheduler.Pump(8); Assert.AreEqual(4, calls);
    }

    [TestMethod]
    public void InvalidDelaysLeaveExistingRequestAndFrameWrapWorks()
    {
        var scheduler = New(); int calls = 0; var work = Register(scheduler, "a", "a", () => calls++);
        work.RequestAfter(TimeSpan.FromMilliseconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => work.RequestAfter(TimeSpan.FromTicks(-1)));
        now = long.MaxValue - 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => work.RequestAfter(TimeSpan.FromMilliseconds(2)));
        scheduler.Pump(int.MaxValue); Assert.AreEqual(1, calls);
        work.SetContinuous(true); scheduler.Pump(int.MinValue); scheduler.Pump(int.MinValue);
        Assert.AreEqual(2, calls);
        var hugeClock = new UiWorkScheduler(() => 0, long.MaxValue, null!);
        var huge = Register(hugeClock, "a", "a", () => { });
        Assert.Throws<ArgumentOutOfRangeException>(() => huge.RequestAfter(TimeSpan.MaxValue));
    }

    [TestMethod]
    public void ConcurrentRequestDuringCallbackIsNotLostAndFailuresAreIsolated()
    {
        var scheduler = New((_, _) => throw new Exception("logger")); int calls = 0, others = 0;
        IUiFrameWorkRegistration work = null!;
        using var entered = new ManualResetEventSlim(); using var requested = new ManualResetEventSlim();
        work = Register(scheduler, "a", "a", () => {
            calls++;
            if (calls == 1) { entered.Set(); Assert.IsTrue(requested.Wait(TimeSpan.FromSeconds(5))); }
            throw new Exception("consumer");
        });
        var other = Register(scheduler, "b", "b", () => others++);
        var producer = Task.Run(() => { Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5))); work.Request(); requested.Set(); });
        work.Request(); other.SetContinuous(true); scheduler.Pump(1); producer.GetAwaiter().GetResult();
        scheduler.Pump(2); Assert.AreEqual(2, calls); Assert.AreEqual(2, others);
    }

    [TestMethod]
    public void IdleWaitingAndSteadyContinuousFramesAllocateNothing()
    {
        var scheduler = New(); var work = Register(scheduler, "a", "a", () => { });
        for (int frame = 0; frame < 100; frame++) scheduler.Pump(frame);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 100; frame < 1100; frame++) scheduler.Pump(frame);
        Assert.AreEqual(allocated, GC.GetAllocatedBytesForCurrentThread());
        work.RequestAfter(TimeSpan.FromSeconds(1));
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 1100; frame < 2100; frame++) scheduler.Pump(frame);
        Assert.AreEqual(allocated, GC.GetAllocatedBytesForCurrentThread());
        work.CancelPending(); work.SetContinuous(true);
        for (int frame = 2100; frame < 2200; frame++) scheduler.Pump(frame);
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 2200; frame < 3200; frame++) scheduler.Pump(frame);
        Assert.AreEqual(allocated, GC.GetAllocatedBytesForCurrentThread());
    }
}
