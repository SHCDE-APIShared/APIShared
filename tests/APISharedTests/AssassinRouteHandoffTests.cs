using System;
using System.Collections.Generic;
using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class AssassinRouteHandoffTests
    {
        private static readonly System.Reflection.FieldInfo ObserverField = typeof(TemporaryGateRouteAcceptanceBridge)
            .GetField("observer", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        private object previousObserver;
        [TestInitialize]
        public void RegisterRequestedObservation()
        { previousObserver = ObserverField.GetValue(null); ObserverField.SetValue(null, new RequestedObserver()); }
        [TestCleanup]
        public void RestoreObservation() => ObserverField.SetValue(null, previousObserver);
        private sealed class RequestedObserver : ITemporaryGateRouteAcceptanceObserver, ITemporaryAssassinGateObserver
        {
            public object BeginRoute(int p, int t, uint tg, int u, uint ug, int type, int x, int y, int tx, int ty, string context) => null;
            public void RouteEdge(object token, int from, int to, int direction) { }
            public void EndRoute(object token, string status, int result) { }
            public void Raid(int p, int role, int t, uint tg, int b, uint bg, string stage, string result, string detail) { }
            public void ObserveAssassinDecision(object token, int p, int from, int to, int direction, bool prepared,
                AssassinTransitionKind movement, bool allowed, int gate, uint global, string evidence) { }
            public void ObserveAssassinStage(object token, int p, string stage, string result, string detail) { }
        }
        [TestMethod]
        public void NoRegisteredObserverDoesNotInvokeReporterAndKeepsPublication()
        {
            ObserverField.SetValue(null, null);
            int reports = 0, publications = 0;
            var frame = new AssassinRouteHandoff(new IntPtr(1), 1, 2, 3, 4, 1,
                () => true, (bytes, count) => { publications++; return count; }, 1, 0,
                (stage, result, detail) => reports++);
            try
            {
                Assert.IsTrue(Stage());
                Assert.AreEqual(2, frame.Complete(7));
                Assert.AreEqual(1, publications);
                Assert.AreEqual(0, reports);
            }
            finally { frame.Leave(); }
        }
        private static bool Stage() => AssassinRouteHandoff.Stage(
            new IntPtr(1), 1, 2, 3, 4, 1, new byte[] { 0x10 }, 2, () => true);

        [TestMethod]
        public void CallerReceivesOrderedStagesAndPublicationRemainsSingleUse()
        {
            var reports = new List<string>();
            int publications = 0;
            var frame = new AssassinRouteHandoff(new IntPtr(1), 1, 2, 3, 4, 1,
                () => true, (bytes, count) => { publications++; return count; }, 1, 0,
                (stage, result, detail) => reports.Add(stage + ":" + result));
            try
            {
                Assert.IsTrue(Stage());
                Assert.AreEqual(2, frame.Complete(7));
                Assert.AreEqual(7, frame.Complete(7));
                Assert.AreEqual(1, publications);
                CollectionAssert.AreEqual(new[] { "handoff:staged", "handoff:published" }, reports);
            }
            finally { frame.Leave(); }
            Assert.IsFalse(AssassinRouteHandoff.HasFrame);
        }

        [TestMethod]
        public void NestedFramesUseTheirOwnReporterAndRestoreTheParent()
        {
            var reports = new List<string>();
            var outer = new AssassinRouteHandoff(new IntPtr(1), 1, 2, 3, 4, 1,
                () => true, (bytes, count) => count, 1, 0,
                (stage, result, detail) => reports.Add("outer:" + result));
            try
            {
                var inner = new AssassinRouteHandoff(new IntPtr(1), 1, 2, 3, 4, 1,
                    () => true, (bytes, count) => count, 1, 0,
                    (stage, result, detail) => reports.Add("inner:" + result));
                try { Assert.IsTrue(Stage()); Assert.AreEqual(2, inner.Complete(7)); }
                finally { inner.Leave(); }
                Assert.IsTrue(Stage());
                Assert.AreEqual(2, outer.Complete(7));
                CollectionAssert.AreEqual(new[] { "inner:staged", "inner:published", "outer:staged", "outer:published" }, reports);
            }
            finally { outer.Leave(); }
        }

        [TestMethod]
        public void ReporterFailureCannotChangeStagingOrThePublishedResult()
        {
            long failures = TemporaryGateRouteAcceptanceBridge.Failures;
            int publications = 0;
            var frame = new AssassinRouteHandoff(new IntPtr(1), 1, 2, 3, 4, 1,
                () => true, (bytes, count) => { publications++; return 19; }, 1, 0,
                (stage, result, detail) => { throw new InvalidOperationException("observer"); });
            try
            {
                Assert.IsTrue(Stage());
                Assert.AreEqual(19, frame.Complete(7));
                Assert.AreEqual(1, publications);
                Assert.AreEqual(failures + 2, TemporaryGateRouteAcceptanceBridge.Failures);
            }
            finally { frame.Leave(); }
        }

        [TestMethod]
        public void PublicOutputFramePublishesAndShadowFrameDeclinesOuterStaging()
        {
            int publications = 0;
            AssassinRouteOutputFrame outer = AssassinPathAPI.BeginRouteOutput(new IntPtr(1), 1, 2, 3, 4, 1,
                () => true, (bytes, count) => { publications++; return count; }, 1, 0);
            try
            {
                Assert.IsTrue(AssassinPathAPI.HasSingleUnitRoutePublication);
                AssassinRouteOutputFrame shadow = AssassinPathAPI.BeginRouteOutput(new IntPtr(1), 1, 2, 3, 4, 0, null, null);
                try { Assert.IsFalse(Stage()); Assert.AreEqual(7, shadow.Complete(7)); }
                finally { shadow.Leave(); }
                Assert.ThrowsExactly<InvalidOperationException>(() => shadow.Leave());
                Assert.IsTrue(Stage());
                Assert.AreEqual(2, outer.Complete(7));
                Assert.AreEqual(1, publications);
            }
            finally { outer.Leave(); }
            Assert.IsFalse(AssassinPathAPI.HasSingleUnitRoutePublication);
        }

        [TestMethod]
        public void ReportingIsOptionalAndInvalidRoutesKeepTheOriginalResult()
        {
            int publications = 0;
            var frame = new AssassinRouteHandoff(new IntPtr(1), 1, 2, 3, 4, 1,
                () => true, (bytes, count) => { publications++; return count; });
            try
            {
                Assert.IsFalse(AssassinRouteHandoff.Stage(new IntPtr(1), 1, 2, 3, 4, 1,
                    new byte[] { 0xff }, 2, () => true));
                Assert.AreEqual(7, frame.Complete(7));
                Assert.AreEqual(0, publications);
                Assert.IsTrue(Stage());
                Assert.AreEqual(2, frame.Complete(7));
            }
            finally { frame.Leave(); }
        }
    }
}
