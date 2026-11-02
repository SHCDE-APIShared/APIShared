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
