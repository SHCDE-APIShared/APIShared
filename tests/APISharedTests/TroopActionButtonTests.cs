using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class TroopActionButtonTests
    {
        private static TroopActionButtonDefinition Definition(string id, int priority = 0,
            Action<bool> context = null) => new TroopActionButtonDefinition(id,
                new Command(), "Original", _ => null, priority, context);

        [TestMethod]
        public void RegistrationStartsHiddenAndSnapshotsFreezeLogicalChanges()
        {
            int notifications = 0;
            var registry = new TroopActionButtonRegistry(() => notifications++);
            var empty = registry.Capture();
            Assert.IsTrue(registry.TryRegister("owner", Definition("action"), out var handle, out _));
            var initial = registry.Capture();
            Assert.AreEqual(0, empty.Length);
            Assert.AreEqual(1, initial.Length);
            Assert.IsFalse(initial[0].Visible);
            Assert.IsTrue(initial[0].Enabled);
            Assert.IsFalse(registry.IsExecutable(initial[0].Entry));
            Assert.AreSame(initial, registry.Capture());
            handle.SetVisible(false); handle.SetEnabled(true); handle.SetTooltip("Original");
            Assert.AreSame(initial, registry.Capture());
            Assert.AreEqual(1, notifications, "Equal setters must not wake dormant presentation.");
            handle.SetVisible(true); handle.SetEnabled(false); handle.SetTooltip("Changed"); handle.RequestButtonRefresh();
            var changed = registry.Capture();
            Assert.IsFalse(initial[0].Visible);
            Assert.IsTrue(initial[0].Enabled);
            Assert.AreEqual("Original", initial[0].Tooltip);
            Assert.AreEqual(0, initial[0].Version);
            Assert.IsTrue(changed[0].Visible);
            Assert.IsFalse(changed[0].Enabled);
            Assert.AreEqual("Changed", changed[0].Tooltip);
            Assert.AreEqual(1, changed[0].Version);
            Assert.IsFalse(registry.IsExecutable(changed[0].Entry));
            handle.SetEnabled(true);
            Assert.IsTrue(registry.IsExecutable(changed[0].Entry));
            handle.SetVisible(false);
            Assert.IsFalse(registry.IsExecutable(changed[0].Entry));
            Assert.ThrowsExactly<ArgumentException>(() => handle.SetTooltip(" "));
        }

        [TestMethod]
        public void RegistrationOrderUsesPriorityThenOrdinalOwnerAndLocalId()
        {
            var registry = new TroopActionButtonRegistry(null);
            Assert.IsTrue(registry.TryRegister("z", Definition("same"), out _, out _));
            Assert.IsTrue(registry.TryRegister("a", Definition("z"), out _, out _));
            Assert.IsTrue(registry.TryRegister("a", Definition("a"), out _, out _));
            Assert.IsTrue(registry.TryRegister("a", Definition("same", 1), out _, out _));
            Assert.IsTrue(registry.TryRegister("A", Definition("same"), out _, out _));
            Assert.IsFalse(registry.TryRegister("z", Definition("same", 5), out var duplicate, out var reason));
            Assert.IsNull(duplicate); Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
            CollectionAssert.AreEqual(new[] { "A:same", "a:a", "a:z", "z:same", "a:same" },
                registry.Capture().Select(state => state.Entry.Owner + ":" + state.Entry.Definition.ButtonId).ToArray());
            foreach (var invalid in new[] { Definition(""), Definition("negative", -1),
                new TroopActionButtonDefinition("command", null, "Tooltip", _ => null),
                new TroopActionButtonDefinition("tooltip", new Command(), " ", _ => null),
                new TroopActionButtonDefinition("factory", new Command(), "Tooltip", null), null })
                Assert.IsFalse(registry.TryRegister("owner", invalid, out _, out _));
            Assert.IsFalse(registry.TryRegister(" ", Definition("owner"), out _, out _));
        }

        [TestMethod]
        public void ReentrantConsumerMutationOnlyChangesTheNextInvocationView()
        {
            var registry = new TroopActionButtonRegistry(null);
            ITroopActionButtonRegistration later = null;
            registry.TryRegister("owner", Definition("first", context: available => {
                if (!available) return;
                later.SetVisible(false); later.SetEnabled(false); later.SetTooltip("Next");
                registry.TryRegister("owner", Definition("new"), out _, out _);
            }), out var first, out _);
            registry.TryRegister("owner", Definition("later"), out later, out _);
            first.SetVisible(true); later.SetVisible(true);
            var invocation = registry.Capture();
            invocation[0].Entry.Definition.ContextChanged(true);
            Assert.AreEqual(2, invocation.Length);
            Assert.IsTrue(invocation[1].Visible); Assert.IsTrue(invocation[1].Enabled);
            Assert.AreEqual("Original", invocation[1].Tooltip);
            var next = registry.Capture();
            Assert.AreEqual(3, next.Length);
            Assert.IsFalse(next[1].Visible); Assert.IsFalse(next[1].Enabled);
            Assert.AreEqual("Next", next[1].Tooltip);
        }

        [TestMethod]
        public void ChangedNotificationCanCaptureAndPublishAnotherLogicalState()
        {
            TroopActionButtonRegistry registry = null;
            ITroopActionButtonRegistration handle = null;
            var seen = new List<bool>();
            bool adjust = false;
            registry = new TroopActionButtonRegistry(() => {
                var snapshot = registry.Capture();
                if (snapshot.Length != 0) seen.Add(snapshot[0].Enabled);
                if (adjust) { adjust = false; handle.SetEnabled(false); }
            });
            registry.TryRegister("owner", Definition("action"), out handle, out _);
            adjust = true; handle.SetVisible(true);
            CollectionAssert.AreEqual(new[] { true, true, false }, seen);
            Assert.IsFalse(registry.Capture()[0].Enabled);
        }

        [TestMethod]
        public void CommandRechecksAuthorizationAfterConsumerCanExecute()
        {
            foreach (bool hide in new[] { false, true })
            {
                var registry = new TroopActionButtonRegistry(null);
                registry.TryRegister("owner", Definition("action"), out var handle, out _);
                handle.SetVisible(true);
                var entry = registry.Capture(out long revision)[0].Entry;
                int checks = 0, invoked = 0, completed = 0, failed = 0;
                var parameter = new object();
                var command = new Command { CanRun = value => {
                    Assert.AreSame(parameter, value);
                    if (hide) handle.SetVisible(false); else handle.SetEnabled(false);
                    return true;
                }, Run = _ => invoked++ };
                Assert.IsFalse(TroopActionCommandExecution.TryInvoke(command, parameter,
                    () => { checks++; return registry.IsExecutable(entry, revision); }, () => completed++, _ => failed++));
                Assert.AreEqual(2, checks); Assert.AreEqual(0, invoked);
                Assert.AreEqual(0, completed); Assert.AreEqual(0, failed);
            }
        }

        [TestMethod]
        public void CommandAcceptsTooltipAndUnrelatedAvailabilityUpdatesDuringCanExecute()
        {
            var registry = new TroopActionButtonRegistry(null);
            registry.TryRegister("owner", Definition("action"), out var handle, out _);
            registry.TryRegister("owner", Definition("other"), out var other, out _);
            handle.SetVisible(true); other.SetVisible(true);
            var rendered = registry.Capture(out long revision);
            var entry = rendered[0].Entry;
            int invoked = 0, requests = 0;
            var command = new Command { CanRun = _ => {
                requests++;
                handle.SetTooltip("Dynamic tooltip " + requests);
                other.SetEnabled(requests % 2 == 0);
                return true;
            }, Run = _ => invoked++ };
            for (int click = 0; click < 3; click++)
                Assert.IsTrue(TroopActionCommandExecution.TryInvoke(command, null,
                    () => registry.IsExecutable(entry, revision), null, error => Assert.Fail(error.ToString())));
            Assert.AreEqual(3, invoked, "Cosmetic changes must not veto an otherwise valid click.");
            Assert.AreNotSame(rendered, registry.Capture(), "The regression must actually replace the general snapshot.");
        }

        [TestMethod]
        [DataRow("hide")]
        [DataRow("hide-other")]
        [DataRow("register")]
        [DataRow("refresh")]
        [DataRow("refresh-other")]
        public void CommandRejectsPageMembershipAndFactoryChangesDuringCanExecute(string mutation)
        {
            var registry = new TroopActionButtonRegistry(null);
            registry.TryRegister("owner", Definition("action"), out var handle, out _);
            registry.TryRegister("owner", Definition("other"), out var other, out _);
            handle.SetVisible(true); other.SetVisible(true);
            var entry = registry.Capture(out long revision)[0].Entry;
            int invoked = 0;
            var command = new Command { CanRun = parameter => {
                switch (mutation)
                {
                    case "hide": handle.SetVisible(false); break;
                    case "hide-other": other.SetVisible(false); break;
                    case "register": registry.TryRegister("owner", Definition("new"), out _, out _); break;
                    case "refresh": handle.RequestButtonRefresh(); break;
                    case "refresh-other": other.RequestButtonRefresh(); break;
                }
                return true;
            }, Run = _ => invoked++ };
            Assert.IsFalse(TroopActionCommandExecution.TryInvoke(command, null,
                () => registry.IsExecutable(entry, revision), null, error => Assert.Fail(error.ToString())));
            Assert.AreEqual(0, invoked, "Potentially changed page membership must reject a stale click.");
        }

        [TestMethod]
        public void CommandRejectsInitialAndCanExecuteVetoAndCompletesOnlySuccessfulInvocation()
        {
            int canRun = 0, invoked = 0, completed = 0, failed = 0;
            var parameter = new object();
            var command = new Command { CanRun = _ => { canRun++; return false; }, Run = _ => invoked++ };
            Assert.IsFalse(TroopActionCommandExecution.TryInvoke(command, parameter, () => false, () => completed++, _ => failed++));
            Assert.AreEqual(0, canRun);
            Assert.IsFalse(TroopActionCommandExecution.TryInvoke(command, parameter, () => true, () => completed++, _ => failed++));
            Assert.AreEqual(1, canRun); Assert.AreEqual(0, invoked); Assert.AreEqual(0, completed);
            command.CanRun = value => { Assert.AreSame(parameter, value); return true; };
            command.Run = value => { Assert.AreSame(parameter, value); invoked++; };
            Assert.IsTrue(TroopActionCommandExecution.TryInvoke(command, parameter, () => true, () => completed++, _ => failed++));
            Assert.AreEqual(1, invoked); Assert.AreEqual(1, completed); Assert.AreEqual(0, failed);
        }

        [TestMethod]
        public void CommandExceptionsAreIsolatedAndNeverAcknowledgeFailedExecution()
        {
            foreach (bool duringCanExecute in new[] { false, true })
            {
                var expected = new InvalidOperationException("consumer");
                int completed = 0, failures = 0, invoked = 0;
                var command = new Command { CanRun = _ => { if (duringCanExecute) throw expected; return true; },
                    Run = _ => { invoked++; throw expected; } };
                Assert.IsFalse(TroopActionCommandExecution.TryInvoke(command, null, () => true,
                    () => completed++, error => { Assert.AreSame(expected, error); failures++; }));
                Assert.AreEqual(duringCanExecute ? 0 : 1, invoked);
                Assert.AreEqual(0, completed); Assert.AreEqual(1, failures);
            }
        }

        [TestMethod]
        public void FailedPresentationRetriesOnlyForNewHudRegistrationOrExplicitRefresh()
        {
            var registry = new TroopActionButtonRegistry(() => { });
            Assert.IsTrue(registry.TryRegister("owner", Definition("first"), out var registration, out _));
            var gate = new TroopActionPresentationRetry();
            var panel = new object(); var root = new object();
            Assert.IsTrue(gate.CanAttempt(panel, root, registry.Capture()));
            gate.RecordFailure(panel, root, registry.Capture());
            Assert.IsFalse(gate.CanAttempt(panel, root, registry.Capture()));
            registration.SetVisible(true); registration.SetEnabled(false); registration.SetTooltip("new text");
            Assert.IsFalse(gate.CanAttempt(panel, root, registry.Capture()), "Ordinary refreshers must not retry an incompatible HUD every frame.");
            registration.RequestButtonRefresh();
            Assert.IsTrue(gate.CanAttempt(panel, root, registry.Capture()));
            gate.RecordFailure(panel, root, registry.Capture());
            Assert.IsTrue(gate.CanAttempt(new object(), root, registry.Capture()));
            Assert.IsTrue(gate.CanAttempt(panel, new object(), registry.Capture()));
            Assert.IsTrue(registry.TryRegister("owner", Definition("second"), out _, out _));
            Assert.IsTrue(gate.CanAttempt(panel, root, registry.Capture()));
            gate.RecordFailure(panel, root, registry.Capture());
            gate.Clear();
            Assert.IsTrue(gate.CanAttempt(panel, root, registry.Capture()));
        }

        private sealed class Command : ICommand
        {
            internal Func<object, bool> CanRun = _ => true;
            internal Action<object> Run = _ => { };
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => CanRun(parameter);
            public void Execute(object parameter) => Run(parameter);
        }
    }
}
