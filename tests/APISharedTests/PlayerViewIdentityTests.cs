using System;
using APIShared.PlayerControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Process-wide player-view identity: original-human save identity, lease ownership and world retirement.
    [TestClass]
    public sealed class PlayerViewIdentityTests
    {
        [TestMethod]
        public void ProtectedSaveUsesTheOriginalHumanIdentityAndRestoresViewsIncludingNestedAndFailedWriters()
        {
            var owner = new object();
            bool inEngine = true;
            long epoch = 1;
            int nativeView = 1, writes = 0;
            var guard = new PlayerViewIdentity(() => inEngine, expected => epoch == expected,
                () => nativeView, view => { writes++; nativeView = view; });
            Assert.IsTrue(guard.TryBind(owner, 1, 1, out PlayerViewIdentity.Lease token, out _));
            Assert.AreEqual(1, guard.ProtectedHumanId);
            nativeView = 3; // The separate view-lease adapter performs this transition.
            Assert.IsFalse(guard.TryRelease(owner, token, out _));
            Assert.IsFalse(guard.TryBind(owner, 1, 1, out _, out _));
            int writers = 0;
            Assert.IsTrue(guard.TryRunWriter(17, value => {
                writers++; Assert.AreEqual(1, nativeView);
                Assert.AreEqual(1, guard.ProtectedHumanId);
                Assert.IsTrue(guard.TryRunWriter(value, nested => {
                    writers++; Assert.AreEqual(1, nativeView); return nested + 1;
                }, out int nestedResult, out _));
                Assert.AreEqual(18, nestedResult); Assert.AreEqual(1, nativeView);
                Assert.IsFalse(guard.TryBind(owner, 1, 1, out _, out _));
                return 0xFEDCBA9876543210UL;
            }, out ulong result, out _));
            Assert.AreEqual(0xFEDCBA9876543210UL, result);
            Assert.AreEqual(2, writers); Assert.AreEqual(3, nativeView);
            Assert.ThrowsExactly<InvalidOperationException>(() => guard.TryRunWriter<int, int>(0, value => {
                Assert.AreEqual(1, nativeView);
                throw new InvalidOperationException("writer failure");
            }, out _, out _));
            Assert.AreEqual(3, nativeView);
            inEngine = false;
            int priorWrites = writes;
            Assert.IsFalse(guard.TryRunWriter(0, value => { writers++; return value; }, out _, out _));
            Assert.AreEqual(priorWrites, writes); Assert.AreEqual(2, writers);
            inEngine = true; nativeView = 1;
            Assert.IsTrue(guard.TryRelease(owner, token, out _)); Assert.AreEqual(0, guard.ProtectedHumanId);

            // A normal, unleased save must not query an optional failed adapter.
            var dormant = new PlayerViewIdentity(() => throw new InvalidOperationException("unused monitor"),
                expected => throw new InvalidOperationException("unused mode"),
                () => throw new InvalidOperationException("unused native reader"),
                value => throw new InvalidOperationException("unused native writer"));
            Assert.IsTrue(dormant.TryRunWriter(9, value => value + 1, out int unchanged, out _));
            Assert.AreEqual(10, unchanged);
            Func<int, int> originalWriter = value => value + 1;
            for (int i = 0; i < 32; i++) dormant.TryRunWriter(i, originalWriter, out _, out _);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) dormant.TryRunWriter(i, originalWriter, out unchanged, out _);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated, "Dormant saves allocate or touch an optional provider.");
            Assert.AreEqual(10000, unchanged);
        }

        [TestMethod]
        public void SaveIdentityDoesNotReviveReleasedOrReplacedWorldsAndRetainsProtectionAfterRestoreFailure()
        {
            var owner = new object();
            long epoch = 1;
            int nativeView = 1;
            bool failForeignRestore = false;
            var guard = new PlayerViewIdentity(() => true, expected => expected == epoch,
                () => nativeView, view => {
                    if (view == 3 && failForeignRestore) throw new InvalidOperationException("restore unavailable");
                    nativeView = view;
                });
            Assert.IsTrue(guard.TryBind(owner, 1, 1, out PlayerViewIdentity.Lease token, out _)); nativeView = 3;
            Assert.IsTrue(guard.TryRunWriter(0, value => {
                Assert.AreEqual(1, nativeView);
                Assert.IsTrue(guard.TryRelease(owner, token, out _));
                Assert.IsFalse(guard.TryBind(owner, 1, 1, out _, out _));
                return value;
            }, out _, out _));
            Assert.AreEqual(1, nativeView); Assert.AreEqual(0, guard.ProtectedHumanId);

            Assert.IsTrue(guard.TryBind(owner, 1, 1, out token, out _)); nativeView = 3;
            Assert.IsTrue(guard.TryRunWriter(0, value => {
                epoch = 2; nativeView = 6; // Native load/mission replacement in an append callback.
                Assert.IsTrue(guard.RetireEndedWorld(1));
                return value;
            }, out _, out _));
            Assert.AreEqual(6, nativeView); Assert.AreEqual(0, guard.ProtectedHumanId);

            nativeView = 1;
            Assert.IsTrue(guard.TryBind(owner, 2, 1, out token, out _)); nativeView = 3;
            failForeignRestore = true;
            Assert.IsFalse(guard.TryRunWriter(7, value => value + 1, out int result, out string failure));
            Assert.AreEqual(8, result); Assert.IsFalse(string.IsNullOrWhiteSpace(failure));
            Assert.AreEqual(1, nativeView); Assert.AreEqual(1, guard.ProtectedHumanId);
            Assert.IsNotNull(guard.Failure);
            Assert.IsTrue(guard.TryRelease(owner, token, out _));
            Assert.IsNull(guard.Failure); Assert.AreEqual(0, guard.ProtectedHumanId);
        }

        [TestMethod]
        public void SaveIdentityRechecksWorldAcrossNativeAdaptersAndSerializesLeaseTransitions()
        {
            var owner = new object();
            // The adapter can cross managed observers. A load during a failing
            // setter must not roll an old view back into the replacement world.
            foreach (bool throwAfterReplacement in new[] { false, true })
            {
                long world = 1;
                int view = 1, writes = 0, writers = 0;
                bool replaceOnWrite = false;
                PlayerViewIdentity guard = null;
                guard = new PlayerViewIdentity(() => true, epoch => epoch == world, () => view, next => {
                    writes++;
                    if (replaceOnWrite)
                    {
                        world = 2; view = 6;
                        Assert.IsTrue(guard.RetireEndedWorld(1));
                        Assert.IsFalse(guard.TryBind(owner, 2, 6, out _, out _), "A save transition is still in progress.");
                        if (throwAfterReplacement) throw new InvalidOperationException("load during setter");
                    }
                    else view = next;
                });
                Assert.IsTrue(guard.TryBind(owner, 1, 1, out _, out _)); view = 3;
                replaceOnWrite = true;
                Assert.IsFalse(guard.TryRunWriter(0, value => { writers++; return value; }, out _, out _));
                Assert.AreEqual(0, writers); Assert.AreEqual(1, writes);
                Assert.AreEqual(6, view); Assert.AreEqual(0, guard.ProtectedHumanId);
                replaceOnWrite = false;
                Assert.IsTrue(guard.TryBind(owner, 2, 6, out _, out _), "The failed save must release its transition scope.");
            }

            long epochNow = 1;
            int currentView = 1, readWrites = 0;
            bool reenter = true, replaceOnRead = false;
            PlayerViewIdentity readerGuard = null;
            readerGuard = new PlayerViewIdentity(() => true, epoch => epoch == epochNow, () => {
                if (reenter)
                {
                    reenter = false;
                    Assert.IsFalse(readerGuard.TryBind(owner, 1, 1, out _, out _), "A recursive bind cannot replace its caller.");
                }
                int result = currentView;
                if (replaceOnRead) { epochNow = 2; currentView = 6; }
                return result;
            }, next => { readWrites++; currentView = next; });
            Assert.IsTrue(readerGuard.TryBind(owner, 1, 1, out PlayerViewIdentity.Lease token, out _));
            replaceOnRead = true;
            Assert.IsFalse(readerGuard.TryRelease(owner, token, out _), "A stale read cannot release an identity in a new world.");
            Assert.IsTrue(readerGuard.RetireEndedWorld(1));
            Assert.AreEqual(0, readWrites); Assert.AreEqual(6, currentView);
            epochNow = 1; currentView = 1;
            Assert.IsFalse(readerGuard.TryBind(owner, 1, 1, out _, out _), "A stale read cannot bind a replaced world.");
            Assert.AreEqual(0, readerGuard.ProtectedHumanId);
        }
    }
}
