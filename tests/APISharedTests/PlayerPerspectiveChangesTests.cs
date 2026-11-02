using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class PlayerPerspectiveChangesTests
    {
        [TestMethod]
        public void ClearEliminatedViewAlsoClearsOwnViewWithoutOptionalNativeProtection()
        {
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
            int native = 2;
            var events = new List<APIShared.PlayerPerspectiveChange>();
            using (APIShared.PlayerPerspectiveAPI.SubscribeChanges("test", "own-view-clear", events.Add))
            {
                Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TryChangeView(2, () => native, p => native = p, out _, 2));
                APIShared.PlayerPerspectiveAPI.ClearEliminatedPlayerView();
                Assert.AreEqual(APIShared.PlayerPerspectiveChangeReason.Cleared, events[events.Count - 1].Reason);
                var active = typeof(APIShared.PlayerPerspectiveAPI).GetField("identityOverrideActive",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Assert.AreEqual(0, active.GetValue(null));
                Assert.AreEqual(2, native);
            }
            Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TryChangeView(3, () => native, p => native = p, out _));
            APIShared.PlayerPerspectiveAPI.ClearEliminatedPlayerView();
            Assert.AreEqual(3, APIShared.PlayerPerspectiveAPI.GetViewedPlayerId(), "Original-spectator view is independent.");
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
        }

        [TestMethod]
        public void EliminatedTransactionsKeepPositiveIdentityAndNeverRollbackIntoAnotherWorld()
        {
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
            int native = 2;
            bool current = true;
            var identity = typeof(APIShared.PlayerPerspectiveAPI).GetField("identityOverridePlayerId",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TryChangeView(4, () => native, p => {
                Assert.AreEqual(2, identity.GetValue(null), "Identity is protected before the native write.");
                native = p;
            }, out _, 2, () => current));
            Assert.AreEqual(4, APIShared.PlayerPerspectiveAPI.GetViewedPlayerId());
            Assert.IsFalse(APIShared.PlayerPerspectiveAPI.TryChangeView(5, () => native, p => {
                native = 7; // A reentrant load created a different world.
                current = false;
                throw new InvalidOperationException("load replaced the world");
            }, out _, 2, () => current));
            Assert.AreEqual(7, native, "The old view must not be restored into the replacement world.");
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
        }

        [TestMethod]
        public void PositiveIdentityRollbackRestoresBothViewAndProtectedIdentity()
        {
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
            int native = 2;
            Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TryChangeView(4, () => native, p => native = p, out _, 2));
            Assert.IsFalse(APIShared.PlayerPerspectiveAPI.TryChangeView(5, () => native, p => {
                if (p == 5) throw new InvalidOperationException("native failure"); native = p;
            }, out string reason, 2));
            Assert.AreEqual(4, native);
            Assert.AreEqual(4, APIShared.PlayerPerspectiveAPI.GetViewedPlayerId());
            var identity = typeof(APIShared.PlayerPerspectiveAPI).GetField("identityOverridePlayerId",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.AreEqual(2, identity.GetValue(null));
            StringAssert.Contains(reason, "rollback=verified");
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
        }

        [TestMethod]
        public void CommittedTransactionsNotifyOnceAndPreserveRollback()
        {
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
            int native = 1;
            var events = new List<APIShared.PlayerPerspectiveChange>();
            using (APIShared.PlayerPerspectiveAPI.SubscribeChanges("test", "transaction", events.Add))
            {
                Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TryChangeView(2, () => native, p => native = p, out _));
                Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TryChangeView(2, () => native, p => native = p, out _));
                Assert.AreEqual(1, events.Count);
                Assert.AreEqual(1, events[0].PreviousPlayerId);
                Assert.AreEqual(2, events[0].CurrentPlayerId);
                Assert.IsFalse(APIShared.PlayerPerspectiveAPI.TryChangeView(3, () => native, p => { if (p == 3) throw new Exception("reject"); native = p; }, out _));
                Assert.AreEqual(2, native);
                Assert.AreEqual(1, events.Count);
                APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
                Assert.AreEqual(APIShared.PlayerPerspectiveChangeReason.Cleared, events[1].Reason);
                Assert.IsTrue(events[1].Sequence > events[0].Sequence);
            }
        }

        [TestMethod]
        public void FailedRollbackPublishesUncertainAndObserversCannotBlockEachOther()
        {
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
            var events = new List<APIShared.PlayerPerspectiveChange>();
            using (APIShared.PlayerPerspectiveAPI.SubscribeChanges("test", "throwing", e => throw new Exception("observer")))
            using (APIShared.PlayerPerspectiveAPI.SubscribeChanges("test", "recording", events.Add))
            {
                Assert.IsFalse(APIShared.PlayerPerspectiveAPI.TryChangeView(2, () => 1, p => throw new Exception("native"), out _));
                Assert.AreEqual(1, events.Count);
                Assert.AreEqual(APIShared.PlayerPerspectiveChangeReason.Uncertain, events[0].Reason);
                Assert.AreEqual(-1, events[0].CurrentPlayerId);
                APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
            }
        }

        private static int IdentityOverride() => (int)typeof(APIShared.PlayerPerspectiveAPI).GetField("identityOverridePlayerId",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);

        // Regression: the input bridge admits input only while actor == native view. A switch must
        // deselect the old player while its view is current, publish the new actor only after the
        // confirmed view, and the API identity must follow the controlled player.
        [TestMethod]
        public void ControlSwitchDeselectsAsTheOldViewAndPublishesTheActorAfterTheConfirmedView()
        {
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
            int native = 1;
            var actor = new List<int>();
            var deselected = new List<string>();
            var control = new APIShared.PlayerControl.LocalInputControl(actor.Add);
            Action<int> setView = p => {
                Assert.IsTrue(actor.Count == 0 || actor[actor.Count - 1] != p, "No actor before its confirmed view.");
                native = p;
            };
            Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TrySetControlCore(control, 2, 5, () => native, setView,
                p => deselected.Add(p + "@" + native), () => true, out _));
            CollectionAssert.AreEqual(new[] { 2 }, actor);
            Assert.AreEqual(0, deselected.Count, "Nothing was controlled before.");
            Assert.AreEqual(2, IdentityOverride(), "GetLocalPlayerId follows the controlled player.");
            Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TrySetControlCore(control, 3, 5, () => native, setView,
                p => deselected.Add(p + "@" + native), () => true, out _));
            CollectionAssert.AreEqual(new[] { "2@2" }, deselected, "Vanilla deselection runs as the previous view.");
            CollectionAssert.AreEqual(new[] { 2, 3 }, actor);
            Assert.AreEqual(3, native);
            Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TrySetControlCore(control, 3, 5, () => native, setView,
                p => deselected.Add(p + "@" + native), () => true, out _));
            Assert.AreEqual(2, actor.Count, "An unchanged selection writes nothing.");
            APIShared.PlayerPerspectiveAPI.EndControlCore(control, 5, () => native, p => deselected.Add(p + "@" + native));
            CollectionAssert.AreEqual(new[] { "2@2", "3@3" }, deselected);
            Assert.AreEqual(0, actor[actor.Count - 1]);
            Assert.AreEqual(-1, IdentityOverride(), "The spectator identity returns after control.");
            Assert.AreEqual(3, native, "Ending control keeps the selected view.");
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
        }

        [TestMethod]
        public void FailedControlSwitchKeepsVerifiedPreviousControlAndBlocksInputWhenUncertain()
        {
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
            int native = 1;
            var actor = new List<int>();
            var control = new APIShared.PlayerControl.LocalInputControl(actor.Add);
            Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TrySetControlCore(control, 2, 5, () => native, p => native = p, p => { }, () => true, out _));
            Assert.IsFalse(APIShared.PlayerPerspectiveAPI.TrySetControlCore(control, 4, 5, () => native,
                p => { if (p == 4) throw new InvalidOperationException("native"); native = p; }, p => { }, () => true, out string reason));
            StringAssert.Contains(reason, "rollback=verified");
            Assert.AreEqual(2, native); Assert.AreEqual(2, control.Player);
            CollectionAssert.AreEqual(new[] { 2 }, actor, "A verified rollback keeps the previous actor unchanged.");
            Assert.AreEqual(2, IdentityOverride());
            Assert.IsFalse(APIShared.PlayerPerspectiveAPI.TrySetControlCore(control, 5, 5, () => native,
                p => throw new InvalidOperationException("native"), p => { }, () => true, out _));
            Assert.IsTrue(control.IsSuspended);
            Assert.AreEqual(-1, actor[actor.Count - 1], "An unconfirmed view blocks spectator input.");
            APIShared.PlayerPerspectiveAPI.EndControlCore(control, 5, () => native, p => Assert.Fail("No controlled view to deselect."));
            Assert.AreEqual(0, actor[actor.Count - 1]);
            Assert.AreEqual(-1, IdentityOverride());
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
        }

        [TestMethod]
        public void ControlFromAnEndedWorldIsReleasedWithoutDeselectingIntoTheNewWorld()
        {
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
            int native = 1;
            var actor = new List<int>();
            int deselects = 0;
            var control = new APIShared.PlayerControl.LocalInputControl(actor.Add);
            Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TrySetControlCore(control, 2, 5, () => native, p => native = p, p => deselects++, () => true, out _));
            Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TrySetControlCore(control, 3, 6, () => native, p => native = p, p => deselects++, () => true, out _));
            Assert.AreEqual(0, deselects, "The previous world's selection is not deselected in the next world.");
            CollectionAssert.AreEqual(new[] { 2, 0, 3 }, actor);
            Assert.IsFalse(control.IsActiveIn(5)); Assert.IsTrue(control.IsActiveIn(6));
            APIShared.PlayerPerspectiveAPI.EndControlCore(control, 7, () => native, p => deselects++);
            Assert.AreEqual(0, deselects);
            Assert.AreEqual(0, actor[actor.Count - 1]);
            Assert.IsFalse(control.IsEngaged);
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
        }

        [TestMethod]
        public void FailedActorWriteFailsClosedToBlockedInput()
        {
            var actor = new List<int>();
            var control = new APIShared.PlayerControl.LocalInputControl(v => { if (v > 0) throw new InvalidOperationException("write"); actor.Add(v); });
            Assert.ThrowsExactly<InvalidOperationException>(() => control.Activate(5, 2));
            CollectionAssert.AreEqual(new[] { -1 }, actor);
            Assert.IsTrue(control.IsSuspended); Assert.AreEqual(0, control.Player);
        }

        [TestMethod]
        public void ReentrantDeliveryKeepsObserverOrderAndDisposeStopsFutureCaptures()
        {
            var hub = new APIShared.PlayerPerspectiveChangeHub();
            var order = new List<string>();
            using (hub.Subscribe("a", "first", e => {
                order.Add("a" + e.Sequence);
                if (e.Sequence == 1) { hub.Enqueue(2, 3, APIShared.PlayerPerspectiveChangeReason.Selected); hub.Drain(); }
            }))
            {
                var second = hub.Subscribe("b", "second", e => order.Add("b" + e.Sequence));
                hub.Enqueue(1, 2, APIShared.PlayerPerspectiveChangeReason.Selected);
                hub.Drain();
                CollectionAssert.AreEqual(new[] { "a1", "b1", "a2", "b2" }, order);
                second.Dispose();
                hub.Enqueue(3, 4, APIShared.PlayerPerspectiveChangeReason.Selected);
                hub.Drain();
                Assert.AreEqual("a3", order[4]);
                Assert.AreEqual(5, order.Count);
            }
        }
    }
}
