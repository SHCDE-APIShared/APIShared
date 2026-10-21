using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class PlayerPerspectiveChangesTests
    {
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
