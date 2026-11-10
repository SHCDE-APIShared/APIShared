using System;
using System.Collections.Generic;
using APIShared.Events;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace APISharedTests
{
    public partial class RuntimeTests
    {
        [TestMethod]
        public void AllyPanelClickSelectsFirstClaimAndHonorsLaterVeto()
        {
            foreach (bool veto in new[] { false, true })
            {
                var events = new InterceptionEvent<AllyPanelButtonPreEventArgs, AllyPanelButtonPostEventArgs>();
                var calls = new List<string>();
                var state = new object();
                AllyPanelButtonPostEventArgs completed = null;
                events.TryRegister("0", "broken", pre =>
                {
                    pre.TryClaim(_ => calls.Add("broken claim"));
                    throw new InvalidOperationException();
                }, null, out _);
                events.TryRegister("a", "claim", pre =>
                {
                    pre.State = state;
                    pre.TryClaim(click =>
                    {
                        calls.Add("claim");
                        click.InvokeOriginal(click.Button);
                    });
                }, post => completed = post, out _);
                events.TryRegister("b", "later", pre =>
                {
                    calls.Add("later pre");
                    pre.TryClaim(_ => calls.Add("later claim"));
                    pre.SkipOriginalFunction = veto;
                }, null, out _);

                AllyPanelButtonEvents.Execute(events, null, "ConfirmGoods", (_, button) => calls.Add(button));

                CollectionAssert.AreEqual(veto ? new[] { "later pre" } : new[] { "later pre", "claim", "ConfirmGoods" }, calls);
                TestAssert.AreSame(state, completed.State);
                TestAssert.AreEqual(veto, completed.WasSkipped);
                TestAssert.AreEqual(!veto, completed.WasClaimed);
                TestAssert.AreEqual(!veto, completed.OriginalCompleted);
            }
        }

        [TestMethod]
        public void AllyPanelClaimCannotRetryOrUseTheOriginalAfterTheCallback()
        {
            var events = new InterceptionEvent<AllyPanelButtonPreEventArgs, AllyPanelButtonPostEventArgs>();
            var failure = new InvalidOperationException("original failure");
            AllyPanelButtonInvocation captured = null;
            AllyPanelButtonPostEventArgs completed = null;
            events.TryRegister("a", "claim", pre => pre.TryClaim(click =>
            {
                captured = click;
                click.InvokeOriginal(click.Button);
            }), post => completed = post, out _);
            int originals = 0;

            var thrown = TestAssert.ThrowsExactly<InvalidOperationException>(() =>
                AllyPanelButtonEvents.Execute(events, null, "ConfirmGoods", (_, _) =>
                {
                    originals++;
                    throw failure;
                }));
            TestAssert.ThrowsExactly<InvalidOperationException>(() => captured.InvokeOriginal(captured.Button));
            TestAssert.AreEqual(1, originals);
            TestAssert.AreSame(failure, thrown);
            TestAssert.AreSame(failure, completed.CompletionException);
            TestAssert.IsFalse(completed.OriginalCompleted);
        }

        [TestMethod]
        public void AllyPanelAllowsNestedVanillaClicksOutsideNotification()
        {
            var events = new InterceptionEvent<AllyPanelButtonPreEventArgs, AllyPanelButtonPostEventArgs>();
            var observed = new List<string>();
            var originals = new List<string>();
            events.TryRegister("a", "observer", pre =>
            {
                observed.Add(pre.Button);
                if (pre.Button == "ConfirmGoods")
                {
                    AllyPanelButtonEvents.Execute(events, null, "Unexpected", (_, button) => originals.Add(button));
                    pre.TryClaim(click =>
                    {
                        click.InvokeOriginal(click.Button);
                        TestAssert.ThrowsExactly<InvalidOperationException>(() => click.InvokeOriginal(click.Button));
                    });
                }
                if (pre.Button == "Exit")
                    pre.SkipOriginalFunction = true;
            }, null, out _);

            AllyPanelButtonEvents.Execute(events, null, "ConfirmGoods", (_, button) =>
            {
                originals.Add(button);
                AllyPanelButtonEvents.Execute(events, null, "Exit", (_, nested) => originals.Add(nested));
            });

            CollectionAssert.AreEqual(new[] { "ConfirmGoods", "Exit" }, observed);
            CollectionAssert.AreEqual(new[] { "ConfirmGoods" }, originals);
        }
    }
}
