using APIShared.Events;
using APIShared.NativeBrokers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

// Managed dispatch of the native entry brokers (production NativeBrokerDispatch.cs), without native code:
// the original is a managed delegate here. Native installation, the demand guard and foreign-detour
// refusal are covered on copied code in tests/APISharedTests/NativeBrokerTests.cs.
[TestClass]
[DoNotParallelize]
public class NativeBrokerDispatchTests
{
    private sealed class Fixture
    {
        internal readonly List<string> Calls = new();
        internal readonly List<bool> Demand = new();
        internal int Ready;
        internal string Refusal;
        internal int OriginalCalls;
        internal readonly PclReachabilityDispatcher Dispatcher;
        internal readonly PclReachabilityNative Original;
        internal Fixture()
        {
            Dispatcher = new PclReachabilityDispatcher(() => { Ready++; return Refusal; }, Demand.Add);
            Original = (manager, player, first, second, mode) => { OriginalCalls++; Calls.Add("original"); return player * 1000 + first * 100 + second * 10 + mode; };
            Dispatcher.Original = Original;
        }
        internal int Call() => Dispatcher.Dispatch(new IntPtr(77), 1, 2, 3, 4);
        internal NativeBrokerRegistration Add(string owner, string id, int order, PclReachabilityHandler pre, PclReachabilityHandler post)
        {
            Assert.IsTrue(Dispatcher.Registry.TryAdd(owner, id, order, pre, post, out NativeBrokerRegistration handle, out string reason), reason);
            return handle;
        }
        internal PclReachabilityHandler Log(string name) => (ref PclReachabilityCall call) => Calls.Add(name);
    }

    [TestMethod]
    public void OrderIsNumericThenOwnerThenIdIndependentOfRegistrationTime()
    {
        var fixture = new Fixture();
        fixture.Add("b.owner", "x", 0, fixture.Log("pre b0"), fixture.Log("post b0"));
        fixture.Add("a.owner", "y", 5, fixture.Log("pre a5"), fixture.Log("post a5"));
        fixture.Add("a.owner", "x", 0, fixture.Log("pre a0x"), null);
        fixture.Add("c.owner", "x", -3, null, fixture.Log("post c-3"));
        fixture.Add("a.owner", "z", 0, fixture.Log("pre a0z"), fixture.Log("post a0z"));
        Assert.AreEqual(1234, fixture.Call());
        CollectionAssert.AreEqual(new[] { "pre a0x", "pre a0z", "pre b0", "pre a5", "original", "post c-3", "post a0z", "post b0", "post a5" }, fixture.Calls);
    }

    [TestMethod]
    public void FirstPreOverrideWinsSkipsLaterPresAndOriginalButPostsRun()
    {
        var fixture = new Fixture();
        bool sawOverride = false, sawOriginal = true; int postResult = -1;
        fixture.Add("a", "observe", 0, (ref PclReachabilityCall call) =>
        {
            fixture.Calls.Add("pre a");
            Assert.IsFalse(call.IsPost);
            Assert.IsFalse(call.TrySetResult(9), "TrySetResult is Post only");
            Assert.AreEqual((77, 1, 2, 3, 4), ((int)call.Manager, call.Player, call.FirstPcl, call.SecondPcl, call.Mode));
        }, null);
        fixture.Add("b", "override", 1, (ref PclReachabilityCall call) => { fixture.Calls.Add("pre b"); Assert.IsTrue(call.TryOverride(1)); Assert.IsFalse(call.TryOverride(2)); }, null);
        fixture.Add("c", "late", 2, fixture.Log("pre c"), (ref PclReachabilityCall call) =>
        {
            sawOverride = call.IsOverridden; sawOriginal = call.OriginalCalled; postResult = call.Result;
            Assert.AreEqual(0, call.OriginalResult);
            Assert.IsFalse(call.TryOverride(5), "TryOverride is Pre only");
            Assert.IsTrue(call.TrySetResult(call.Result + 40));
        });
        Assert.AreEqual(41, fixture.Call());
        CollectionAssert.AreEqual(new[] { "pre a", "pre b" }, fixture.Calls);
        Assert.AreEqual(0, fixture.OriginalCalls);
        Assert.IsTrue(sawOverride); Assert.IsFalse(sawOriginal); Assert.AreEqual(1, postResult);
        Assert.AreEqual(0, fixture.Dispatcher.Registry.Failures, "callback assertions held");
    }

    [TestMethod]
    public void PostsSeeTheOriginalResultAndChainReplacementsInOrder()
    {
        var fixture = new Fixture();
        var seen = new List<int>();
        fixture.Add("a", "double", 0, null, (ref PclReachabilityCall call) => { seen.Add(call.Result); Assert.IsTrue(call.OriginalCalled); call.TrySetResult(call.Result * 2); });
        fixture.Add("a", "plus", 1, null, (ref PclReachabilityCall call) => { seen.Add(call.Result); seen.Add(call.OriginalResult); call.TrySetResult(call.Result + 1); });
        Assert.AreEqual(2469, fixture.Call());
        CollectionAssert.AreEqual(new[] { 1234, 2468, 1234 }, seen, "OriginalResult ignores earlier replacements");
        Assert.AreEqual(1, fixture.OriginalCalls);
    }

    [TestMethod]
    public void ThrowingCallbacksAreRolledBackCountedReportedAndIsolated()
    {
        var reports = new List<string>();
        Action<string, Exception> previous = InterceptionDiagnostics.Error;
        InterceptionDiagnostics.Error = (registration, error) => reports.Add(registration);
        try
        {
            var fixture = new Fixture();
            fixture.Add("a", "bad-pre", 0, (ref PclReachabilityCall call) => { call.TryOverride(99); throw new InvalidOperationException("pre"); }, null);
            fixture.Add("b", "pre", 1, fixture.Log("pre b"), null);
            fixture.Add("c", "bad-post", 2, null, (ref PclReachabilityCall call) => { call.TrySetResult(-5); throw new InvalidOperationException("post"); });
            fixture.Add("d", "post", 3, null, (ref PclReachabilityCall call) => fixture.Calls.Add("post d " + call.Result));
            Assert.AreEqual(1234, fixture.Call(), "the failed override and the failed replacement are rolled back");
            CollectionAssert.AreEqual(new[] { "pre b", "original", "post d 1234" }, fixture.Calls);
            Assert.AreEqual(2, fixture.Dispatcher.Registry.Failures);
            for (int index = 0; index < 3; index++) fixture.Call();
            Assert.AreEqual(8, fixture.Dispatcher.Registry.Failures);
            // Reported on the first, second and fourth failure of each registration.
            Assert.AreEqual(6, reports.Count);
            Assert.IsTrue(reports.All(report => report.StartsWith("a/bad-pre PclReachability Pre") || report.StartsWith("c/bad-post PclReachability Post")), string.Join(";", reports));
        }
        finally { InterceptionDiagnostics.Error = previous; }
    }

    [TestMethod]
    public void UnregisterDuringDispatchSkipsTheRestOfThatRegistration()
    {
        var fixture = new Fixture();
        NativeBrokerRegistration later = null, self = null;
        fixture.Add("a", "remover", 0, (ref PclReachabilityCall call) => { fixture.Calls.Add("pre a"); later.Dispose(); }, null);
        self = fixture.Add("b", "self", 1, (ref PclReachabilityCall call) => { fixture.Calls.Add("pre b"); self.Dispose(); }, fixture.Log("post b"));
        later = fixture.Add("c", "later", 2, fixture.Log("pre c"), fixture.Log("post c"));
        fixture.Add("d", "stays", 3, null, fixture.Log("post d"));
        fixture.Call();
        CollectionAssert.AreEqual(new[] { "pre a", "pre b", "original", "post d" }, fixture.Calls);
        Assert.IsFalse(later.IsActive); Assert.IsFalse(self.IsActive);
        Assert.AreEqual(2, fixture.Dispatcher.Registry.Count);
        fixture.Calls.Clear();
        fixture.Call();
        CollectionAssert.AreEqual(new[] { "pre a", "original", "post d" }, fixture.Calls);
    }

    [TestMethod]
    public void DemandFollowsActiveRegistrationsAndInstallRunsBeforeTheFirstAdd()
    {
        var fixture = new Fixture { Refusal = "ForeignOwner: occupied" };
        Assert.IsFalse(fixture.Dispatcher.Registry.TryAdd("a", "x", 0, fixture.Log("x"), null, out var refused, out string reason));
        Assert.IsNull(refused); Assert.AreEqual("ForeignOwner: occupied", reason); Assert.AreEqual(0, fixture.Demand.Count);
        fixture.Refusal = null;
        Assert.IsFalse(fixture.Dispatcher.Registry.TryAdd("a", " ", 0, fixture.Log("x"), null, out _, out _), "blank ID");
        Assert.IsFalse(fixture.Dispatcher.Registry.TryAdd("a", "none", 0, null, null, out _, out _), "no callback");
        int readyBefore = fixture.Ready;
        NativeBrokerRegistration first = fixture.Add("a", "x", 0, fixture.Log("x"), null);
        Assert.AreEqual(readyBefore + 1, fixture.Ready, "invalid registrations never reach installation");
        Assert.IsFalse(fixture.Dispatcher.Registry.TryAdd("a", "x", 5, null, fixture.Log("y"), out _, out reason));
        StringAssert.Contains(reason, "already registered");
        NativeBrokerRegistration second = fixture.Add("b", "x", 0, null, fixture.Log("y"));
        CollectionAssert.AreEqual(new[] { true, true }, fixture.Demand);
        first.Dispose(); first.Dispose();
        CollectionAssert.AreEqual(new[] { true, true }, fixture.Demand, "an active registration remains");
        second.Dispose();
        CollectionAssert.AreEqual(new[] { true, true, false }, fixture.Demand);
        NativeBrokerRegistration again = fixture.Add("a", "x", 0, fixture.Log("x"), null);
        Assert.IsTrue(again.IsActive);
        CollectionAssert.AreEqual(new[] { true, true, false, true }, fixture.Demand);
    }

    [TestMethod]
    public void ConcurrentRegistrationChurnDuringDispatchKeepsEveryCallConsistent()
    {
        var dispatcher = new PclReachabilityDispatcher(() => null, _ => { });
        dispatcher.Original = (manager, player, first, second, mode) => player + first + second + mode;
        int stop = 0; Exception failure = null;
        var churn = new Thread(() =>
        {
            try
            {
                for (int round = 0; round < 4000; round++)
                {
                    Assert.IsTrue(dispatcher.Registry.TryAdd("churn", "r" + (round % 3), round % 5,
                        (ref PclReachabilityCall call) => { }, (ref PclReachabilityCall call) => call.TrySetResult(call.Result), out var handle, out string reason), reason);
                    handle.Dispose();
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { Volatile.Write(ref stop, 1); }
        });
        dispatcher.Registry.TryAdd("steady", "post", 0, null, (ref PclReachabilityCall call) => call.TrySetResult(call.Result + 1), out _, out _);
        churn.Start();
        long calls = 0;
        while (Volatile.Read(ref stop) == 0 || calls < 1000)
        {
            Assert.AreEqual(11, dispatcher.Dispatch(IntPtr.Zero, 1, 2, 3, 4));
            calls++;
        }
        churn.Join();
        Assert.IsNull(failure, failure?.ToString());
        Assert.AreEqual(0, dispatcher.Registry.Failures);
        Assert.AreEqual(1, dispatcher.Registry.Count);
    }

    [TestMethod]
    public void TileRefreshRunsTheOriginalFirstThenIsolatedPostsInOrder()
    {
        var calls = new List<string>(); var demand = new List<bool>();
        var dispatcher = new TileRefreshDispatcher(() => null, demand.Add);
        dispatcher.Original = (manager, y, tile) => calls.Add("original " + y + "/" + tile);
        Assert.IsTrue(dispatcher.Registry.TryAdd("b", "second", 1, null, call => calls.Add("b " + call.TileY + "/" + call.TileId), out var second, out _));
        Assert.IsTrue(dispatcher.Registry.TryAdd("a", "bad", 0, null, call => throw new InvalidOperationException(), out _, out _));
        Assert.IsTrue(dispatcher.Registry.TryAdd("a", "first", 0, null, call => calls.Add("a " + (int)call.Manager), out _, out _));
        dispatcher.Dispatch(new IntPtr(9), 12, 345);
        CollectionAssert.AreEqual(new[] { "original 12/345", "a 9", "b 12/345" }, calls);
        Assert.AreEqual(1, dispatcher.Registry.Failures);
        second.Dispose();
        calls.Clear();
        dispatcher.Dispatch(new IntPtr(9), 1, 2);
        CollectionAssert.AreEqual(new[] { "original 1/2", "a 9" }, calls);
        CollectionAssert.AreEqual(new[] { true, true, true }, demand);
    }

    [TestMethod]
    public void DispatchAllocatesNothingWithOneOrEightConsumers()
    {
        foreach (int consumers in new[] { 0, 1, 8 })
        {
            var dispatcher = new PclReachabilityDispatcher(() => null, _ => { });
            dispatcher.Original = (manager, player, first, second, mode) => player + first;
            for (int index = 0; index < consumers; index++)
                dispatcher.Registry.TryAdd("owner", "r" + index, index, (ref PclReachabilityCall call) => { if (call.Player < 0) call.TryOverride(0); },
                    (ref PclReachabilityCall call) => call.TrySetResult(call.Result), out _, out _);
            var tile = new TileRefreshDispatcher(() => null, _ => { });
            tile.Original = (manager, y, id) => { };
            for (int index = 0; index < consumers; index++)
                tile.Registry.TryAdd("owner", "r" + index, index, null, call => { }, out _, out _);
            long sink = 0;
            for (int index = 0; index < 1000; index++) { sink += dispatcher.Dispatch(IntPtr.Zero, index, 1, 2, 3); tile.Dispatch(IntPtr.Zero, index, 1); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 100000; index++) { sink += dispatcher.Dispatch(IntPtr.Zero, index, 1, 2, 3); tile.Dispatch(IntPtr.Zero, index, 1); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated, consumers + " consumers allocate (sink " + sink + ")");
        }
    }
}
