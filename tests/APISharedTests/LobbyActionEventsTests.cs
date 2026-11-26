using System;
using System.Collections.Generic;
using APIShared.Lobby;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class LobbyActionEventsTests
    {
        [TestMethod]
        public void RegistrationFailureDoesNotPublishOrRetainAnObserver()
        {
            var registry = new LobbyActionObserverRegistry(() => throw new MissingMethodException("changed game"), _ => { });
            Assert.IsFalse(registry.TryRegister("mod", "id", _ => Assert.Fail(), out string reason));
            StringAssert.Contains(reason, "changed game");
            Assert.IsFalse(registry.HasObservers);
        }

        [TestMethod]
        public void ObservationIsOrderedIsolatedAndRetainsTheActualToggle()
        {
            var calls = new List<string>(); int errors = 0;
            var registry = new LobbyActionObserverRegistry(() => { }, _ => errors++);
            Assert.IsTrue(registry.TryRegister("z", "last", value => {
                calls.Add("z"); Assert.AreEqual(77UL, value.LobbyId);
                Assert.IsTrue(value.ReadyBefore); Assert.IsFalse(value.ReadyAfter);
                Assert.IsTrue(value.OriginalInvoked); Assert.AreEqual("Ready", value.Command);
            }, out _));
            Assert.IsTrue(registry.TryRegister("a", "bad", _ => { calls.Add("a"); throw new Exception(); }, out _));
            Assert.IsFalse(registry.TryRegister("z", "last", _ => { }, out _));
            registry.Publish(new LobbyActionNotification(77, "Ready", true, false, true));
            CollectionAssert.AreEqual(new[] { "a", "z" }, calls);
            Assert.AreEqual(1, errors);
        }

        [TestMethod]
        public void RegistrationsDuringObservationApplyOnlyToTheNextCommandAndRecursionIsBounded()
        {
            var calls = new List<string>(); bool added = false;
            var registry = new LobbyActionObserverRegistry(() => { }, _ => Assert.Fail());
            var notification = new LobbyActionNotification(1, "Ready", false, false, false);
            registry.TryRegister("a", "first", value => {
                calls.Add("a");
                registry.Publish(notification);
                if (!added) { added = true; Assert.IsTrue(registry.TryRegister("b", "later", __ => calls.Add("b"), out _)); }
            }, out _);
            registry.Publish(notification);
            CollectionAssert.AreEqual(new[] { "a" }, calls);
            calls.Clear(); registry.Publish(notification);
            CollectionAssert.AreEqual(new[] { "a", "b" }, calls);
            Assert.IsFalse(notification.OriginalInvoked);
        }

        [TestMethod]
        public void PublicReadinessRejectsInvalidStatesWithoutChangingLegacyGuardSemantics()
        {
            Assert.AreEqual(LobbyLaunchGuardState.Ready, default(LobbyLaunchGuardResult).State);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LobbyLaunchGuardResult((LobbyLaunchGuardState)9, "bad"));
            Assert.ThrowsExactly<ArgumentException>(() => LobbyLaunchGuardResult.Waiting(" "));
            Assert.ThrowsExactly<ArgumentException>(() => LobbyLaunchGuardResult.Rejected(null));
            var registry = new LobbyLaunchGuardRegistry(() => { }, () => "error");
            registry.TryRegisterReadiness("a", "pending", _ => LobbyLaunchGuardResult.Waiting("ack pending"), out _);
            registry.TryRegister("b", "legacy", _ => "invalid file", out _);
            var decision = registry.Evaluate(LobbyLaunchGuardRegistry.PreparedMultiplayerLoadContext(2));
            Assert.AreEqual(LobbyLaunchGuardState.Rejected, decision.State);
            Assert.AreEqual("invalid file", decision.Reason);
        }
    }
}
