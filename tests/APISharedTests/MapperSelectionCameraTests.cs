using System;
using System.Collections.Generic;
using System.Threading;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class MapperSelectionCameraTests
    {
        private sealed class Fixture
        {
            internal readonly object Gate = new object();
            internal long Session = 11, World = 21;
            internal int Demand, Calls, Errors;
            internal bool Main = true;
            internal readonly MapperCameraBroker Broker;
            internal Fixture()
            {
                Broker = new MapperCameraBroker(Gate, () => Main, () => Session, () => World,
                    () => Demand, value => Demand = value, (entry, error) => Errors++);
            }
            internal int Original(int mapper) { Calls++; return mapper; }
            internal void Register(string owner, string id, Action<MapperSelectionCameraPreEventArgs> pre,
                Action<MapperSelectionCameraPostEventArgs> post, int order = 0)
            { Assert.IsTrue(Broker.TryRegister(owner, id, pre, post, out var reason, order), reason); }
        }

        [TestMethod]
        public void OrderedPrivateStatesAreFrozenAndPostRunsAfterScopedDemandCleanup()
        {
            var f = new Fixture(); var events = new List<string>();
            MapperSelectionCameraPreEventArgs retained = null;
            f.Register("z", "later", e => { events.Add("pre-z"); e.State = "z"; }, e => {
                events.Add("post-z"); Assert.AreEqual("z", e.State); Assert.AreEqual(0, f.Demand);
                Assert.IsTrue(e.SuppressionActive); Assert.IsNull(e.Exception); Assert.AreEqual(81, e.Result);
            });
            f.Register("a", "first", e => {
                events.Add("pre-a"); retained = e; Assert.AreEqual(11L, e.SessionId); Assert.AreEqual(81, e.Mapper);
                e.State = "a"; e.SuppressCamera = true;
            }, e => { events.Add("post-a"); Assert.AreEqual("a", e.State); Assert.AreEqual(0, f.Demand); });
            int result = f.Broker.Invoke(81, mapper => {
                Assert.IsTrue(Monitor.IsEntered(f.Gate)); Assert.AreEqual(1, f.Demand);
                Assert.ThrowsExactly<InvalidOperationException>(() => retained.SuppressCamera = false);
                Assert.ThrowsExactly<InvalidOperationException>(() => retained.State = null);
                events.Add("original"); return f.Original(mapper);
            });
            Assert.AreEqual(81, result); Assert.AreEqual(1, f.Calls);
            CollectionAssert.AreEqual(new[] { "pre-a", "pre-z", "original", "post-z", "post-a" }, events);
            Assert.IsFalse(f.Broker.TryRegister("a", "first", _ => { }, null, out _));
        }

        [TestMethod]
        public void ThrowingPreKeepsCleanupStateButCannotArmSuppressionOrDefeatOtherObservers()
        {
            var f = new Fixture(); int posts = 0;
            f.Register("bad", "pre", e => { e.State = "cleanup"; e.SuppressCamera = true; throw new Exception("pre"); }, e => {
                Assert.AreEqual("cleanup", e.State); Assert.IsFalse(e.SuppressionActive); posts++;
            });
            f.Register("good", "post", null, e => { posts++; throw new Exception("post"); });
            for (int i = 0; i < 2; i++) f.Broker.Invoke(80, mapper => { Assert.AreEqual(0, f.Demand); return f.Original(mapper); });
            Assert.AreEqual(2, f.Calls); Assert.AreEqual(4, posts); Assert.AreEqual(2, f.Errors, "One report per failing registration.");
        }

        [TestMethod]
        public void OriginalExceptionIsObservedAndRethrownOnceAfterDemandAndAllPostsAreRestored()
        {
            var f = new Fixture(); int posts = 0; var failure = new InvalidOperationException("original");
            f.Register("owner", "id", e => e.SuppressCamera = true, e => {
                posts++; Assert.AreSame(failure, e.Exception); Assert.AreEqual(-1, e.Result);
                Assert.AreEqual(0, f.Demand); Assert.IsTrue(e.SuppressionActive);
            });
            Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => f.Broker.Invoke(80, mapper => {
                f.Calls++; Assert.AreEqual(1, f.Demand); throw failure;
            })));
            Assert.AreEqual(1, f.Calls); Assert.AreEqual(1, posts); Assert.AreEqual(0, f.Demand);
        }

        [TestMethod]
        public void NestedOriginalScopesRestoreTheirParentButCallbackCommandsNeverInheritSuppression()
        {
            var f = new Fixture(); int pre = 0, post = 0;
            f.Register("owner", "scope", e => {
                pre++; e.SuppressCamera = e.Mapper == 80;
                Assert.AreEqual(81, f.Broker.Invoke(81, mapper => { Assert.AreEqual(0, f.Demand); return mapper; }));
            }, e => {
                post++; int expected = e.Mapper == 81 ? 1 : 0;
                Assert.AreEqual(expected, f.Demand);
                f.Broker.Invoke(82, mapper => { Assert.AreEqual(0, f.Demand); return mapper; });
                Assert.AreEqual(expected, f.Demand);
            });
            f.Broker.Invoke(80, outer => {
                Assert.AreEqual(1, f.Demand);
                f.Broker.Invoke(81, inner => { Assert.AreEqual(0, f.Demand); return inner; });
                Assert.AreEqual(1, f.Demand); return outer;
            });
            Assert.AreEqual(2, pre); Assert.AreEqual(2, post); Assert.AreEqual(0, f.Demand);
        }

        [TestMethod]
        public void WorldAndSessionReplacementPreventArmingAndCannotRestoreStaleParentDemand()
        {
            var f = new Fixture();
            f.Register("owner", "world", e => {
                e.SuppressCamera = true; if (e.Mapper == 80) f.World++; if (e.Mapper == 83) f.Session++;
            }, e => { Assert.AreEqual(0, f.Demand); });
            f.Broker.Invoke(80, mapper => { Assert.AreEqual(0, f.Demand); return mapper; });
            f.Broker.Invoke(81, mapper => {
                Assert.AreEqual(1, f.Demand); f.World++; f.Broker.Reset();
                Assert.AreEqual(0, f.Demand); return mapper;
            });
            Assert.AreEqual(0, f.Demand);
            f.Broker.Invoke(83, mapper => { Assert.AreEqual(0, f.Demand); return mapper; });
            f.Main = false;
            f.Broker.Invoke(82, mapper => { Assert.AreEqual(0, f.Demand); return mapper; });
        }

        [TestMethod]
        public void ResetDuringPreInvalidatesItsDecisionEvenWhenSessionAndWorldAreUnchanged()
        {
            var f = new Fixture();
            f.Register("owner", "pre-reset", e => { e.SuppressCamera = true; f.Broker.Reset(); },
                e => Assert.IsFalse(e.SuppressionActive));
            f.Broker.Invoke(80, mapper => { Assert.AreEqual(0, f.Demand); return f.Original(mapper); });
            Assert.AreEqual(1, f.Calls); Assert.AreEqual(0, f.Demand);
        }

        [TestMethod]
        public void ResetOrSessionReplacementInsideNestedOriginalCannotRearmTheParent()
        {
            foreach (bool reset in new[] { false, true })
            {
                var f = new Fixture();
                f.Register("owner", "reset", e => e.SuppressCamera = true, null);
                f.Broker.Invoke(80, outer => {
                    Assert.AreEqual(1, f.Demand);
                    f.Broker.Invoke(81, inner => {
                        if (reset) f.Broker.Reset(); else f.Session++;
                        return inner;
                    });
                    Assert.AreEqual(0, f.Demand, "A stale parent scope must not survive reset/session replacement.");
                    return outer;
                });
                Assert.AreEqual(0, f.Demand);
            }
        }

        [TestMethod]
        public void UnregisteredDefaultPathHasNoManagedAllocationsAfterWarmup()
        {
            var f = new Fixture(); Func<int, int> original = f.Original;
            for (int i = 0; i < 100; i++) f.Broker.Invoke(80, original);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) f.Broker.Invoke(80, original);
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.AreEqual(1100, f.Calls); Assert.AreEqual(0, f.Demand);
        }
    }
}
