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
