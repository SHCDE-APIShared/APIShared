using APIShared.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public class InterceptionEventTests
{
    private sealed class Input : InterceptionPreEventArgs
    {
        private int value;
        public int Value { get => value; set { CheckMutable(); this.value = value; } }
        internal override object Capture() => value;
        internal override void Restore(object snapshot) => value = (int)snapshot;
    }
    private record Output(int Value, bool Skipped, object State);
    private static Output Post(Input a, object state) => new(a.Value, a.SkipOriginalFunction, state);

    [TestMethod]
    public void StableOrderOwnerLocalIdsAndStickyVeto()
    {
        var events = new InterceptionEvent<Input, Output>();
        var calls = new List<string>();
        events.TryRegister("z", "same", a => { calls.Add("z"); a.SkipOriginalFunction = false; a.Value++; }, a => calls.Add("post-z"), out _);
        events.TryRegister("a", "same", a => { calls.Add("a"); a.SkipOriginalFunction = true; a.Value = 5; }, a => { Assert.IsTrue(a.Skipped); calls.Add("post-a"); }, out _);
        Assert.IsFalse(events.TryRegister("a", "same", _ => { }, null, out _));
        var invocation = events.Begin(new Input());
        Assert.AreEqual(6, invocation.Pre.Value);
        Assert.IsTrue(invocation.Pre.SkipOriginalFunction);
        invocation.Complete(s => Post(invocation.Pre, s));
        invocation.Complete(s => Post(invocation.Pre, s));
        CollectionAssert.AreEqual(new[] { "a", "z", "post-a", "post-z" }, calls);
    }

    [TestMethod]
    public void FailedPreRollsBackInputsVetoAndPrivateStateAndContinues()
    {
        var events = new InterceptionEvent<Input, Output>();
        object ownState = new object();
        events.TryRegister("a", "valid", a => { a.Value = 2; a.State = ownState; }, a => Assert.AreSame(ownState, a.State), out _);
        events.TryRegister("b", "throws", a => { Assert.IsNull(a.State); a.Value = 100; a.SkipOriginalFunction = true; a.State = new object(); throw new Exception(); },
            a => { Assert.IsNull(a.State); throw new Exception(); }, out _);
        bool finalPost = false;
        events.TryRegister("c", "last", a => { Assert.AreEqual(2, a.Value); Assert.IsFalse(a.SkipOriginalFunction); }, a => finalPost = true, out _);
        InterceptionDiagnostics.Error = (_, _) => throw new Exception("broken logger");
        try {
            var invocation = events.Begin(new Input());
            invocation.Complete(s => Post(invocation.Pre, s));
            Assert.IsTrue(finalPost);
            Assert.AreEqual(2L, events.CallbackFailures);
        } finally { InterceptionDiagnostics.Error = null; }
    }

    [TestMethod]
    public void RegistrationDuringPreJoinsNextInvocationAndNestedNotificationsAreSuppressed()
    {
        var events = new InterceptionEvent<Input, Output>();
        int pre = 0, post = 0, added = 0;
        events.TryRegister("a", "first", a => {
            pre++;
            var nested = events.Begin(new Input());
            nested.Complete(s => Post(nested.Pre, s));
            events.TryRegister("b", "added", _ => added++, _ => added++, out _);
        }, _ => post++, out _);
        var first = events.Begin(new Input());
        first.Complete(s => Post(first.Pre, s));
        Assert.AreEqual(1, pre); Assert.AreEqual(1, post); Assert.AreEqual(0, added);
        var second = events.Begin(new Input());
        second.Complete(s => Post(second.Pre, s));
        Assert.AreEqual(2, pre); Assert.AreEqual(2, post); Assert.AreEqual(2, added);
    }

    [TestMethod]
    public void FrozenAndCrossThreadSettersAreRejected()
    {
        var events = new InterceptionEvent<Input, Output>();
        events.TryRegister("a", "thread", a => {
            var exception = Task.Run(() => { try { a.Value = 1; return null; } catch (Exception e) { return e; } }).Result;
            Assert.IsInstanceOfType<InvalidOperationException>(exception);
        }, null, out _);
        var invocation = events.Begin(new Input());
        Assert.Throws<InvalidOperationException>(() => invocation.Pre.Value = 8);
        Assert.Throws<InvalidOperationException>(() => invocation.Pre.State = new object());
        Assert.Throws<InvalidOperationException>(() => invocation.Pre.SkipOriginalFunction = true);
    }

    [TestMethod]
    public void AcceptedRunsOnceAfterEveryPreWithFrozenFinalInputsAndDoesNotRunOnVeto()
    {
        var events = new InterceptionEvent<Input, Output>();
        int accepted = 0;
        events.TryRegister("a", "prepare", a => { a.Value = 2; a.State = 7; }, null, out _, accepted: (a, state) => {
            accepted++; Assert.AreEqual(5, a.Value); Assert.AreEqual(7, state);
            Assert.Throws<InvalidOperationException>(() => a.Value = 10);
        });
        events.TryRegister("b", "change", a => a.Value = 5, null, out _);
        var invocation = events.Begin(new Input());
        invocation.NotifyAccepted(); invocation.NotifyAccepted();
        Assert.AreEqual(1, accepted);
        events.TryRegister("c", "veto", a => a.SkipOriginalFunction = true, null, out _);
        invocation = events.Begin(new Input()); invocation.NotifyAccepted();
        Assert.AreEqual(1, accepted);
    }

    [TestMethod]
    public void InstallationFailureAllowsRetryAndSuccessfulInstallOccursOnce()
    {
        int installs = 0;
        var events = new InterceptionEvent<Input, Output>(() => { if (++installs == 1) throw new Exception("unavailable"); });
        Assert.IsFalse(events.TryRegister("a", "one", _ => { }, null, out string reason));
        StringAssert.Contains(reason, "unavailable");
        Assert.IsTrue(events.TryRegister("a", "one", _ => { }, null, out _));
        Assert.IsTrue(events.TryRegister("b", "two", _ => { }, null, out _));
        Assert.AreEqual(2, installs);
    }
}
