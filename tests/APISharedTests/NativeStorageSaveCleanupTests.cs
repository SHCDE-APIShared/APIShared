using System;
using System.Collections.Generic;
using System.IO;
using APIShared.Economy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    public partial class NativeStorageCargoSaveTests
    {
        [TestMethod]
        public void RestorationFailureStillReleasesEveryFrozenSaveParticipant()
        {
            var calls = new List<string>();
            var restore = new IOException("controlled-restoration-failure");
            var report = WorkerProductionCriticalSaveRuntime.CompleteSaveCleanup(0, false, null,
                () => { calls.Add("restore"); throw restore; },
                () => calls.Add("production"), () => calls.Add("workshops"), () => calls.Add("storage"));
            CollectionAssert.AreEqual(new[] { "restore", "production", "workshops", "storage" }, calls);
            Assert.AreSame(restore, report.Primary); Assert.AreSame(restore, report.RestoreError);
            Assert.IsNull(report.ProductionError); Assert.IsNull(report.WorkshopError); Assert.IsNull(report.StorageError);
            Assert.IsTrue(report.CleanupFailed); Assert.IsFalse(report.Committed); Assert.AreEqual(0UL, report.ReturnValue);
        }

        [TestMethod]
        public void CaptureFailureRemainsPrimaryWhenRestorationAndAllReleasesAlsoFail()
        {
            var original = new InvalidDataException("controlled-original-capture-failure");
            var restore = new IOException("restore"); var production = new IOException("production");
            var workshop = new IOException("workshop"); var storage = new IOException("storage");
            var calls = new List<string>();
            var report = WorkerProductionCriticalSaveRuntime.CompleteSaveCleanup(1, false, original,
                () => { calls.Add("restore"); throw restore; },
                () => { calls.Add("production"); throw production; },
                () => { calls.Add("workshops"); throw workshop; },
                () => { calls.Add("storage"); throw storage; });
            CollectionAssert.AreEqual(new[] { "restore", "production", "workshops", "storage" }, calls);
            Assert.AreSame(original, report.Primary, "Cleanup failures must not replace the actual capture error.");
            Assert.AreSame(restore, report.RestoreError); Assert.AreSame(production, report.ProductionError);
            Assert.AreSame(workshop, report.WorkshopError); Assert.AreSame(storage, report.StorageError);
            Assert.IsTrue(report.CleanupFailed); Assert.AreEqual(1UL, report.ActualWriterResult);
            Assert.IsFalse(report.Committed); Assert.AreEqual(0UL, report.ReturnValue,
                "A native writer return alone cannot claim the verified archive committed.");
        }

        [TestMethod]
        public void PostCommitCleanupFailurePreservesTheCommittedWriterResult()
        {
            var failure = new IOException("controlled-post-commit-restoration-failure");
            int released = 0;
            var report = WorkerProductionCriticalSaveRuntime.CompleteSaveCleanup(1, true, null,
                () => throw failure, () => released++, () => released++, () => released++);
            Assert.AreEqual(3, released);
            Assert.AreSame(failure, report.Primary); Assert.IsTrue(report.CleanupFailed);
            Assert.IsTrue(report.Committed); Assert.AreEqual(1UL, report.ReturnValue,
                "A later protection fault cannot relabel the already committed save as unwritten or rolled back.");
        }

        [TestMethod]
        public void SuccessfulCleanupHasNoProtectionFailureAndPreservesTheActualResult()
        {
            int restored = 0, released = 0;
            var report = WorkerProductionCriticalSaveRuntime.CompleteSaveCleanup(1, true, null,
                () => restored++, null, null, () => released++);
            Assert.AreEqual(1, restored); Assert.AreEqual(1, released);
            Assert.IsFalse(report.CleanupFailed); Assert.IsNull(report.Primary);
            Assert.IsTrue(report.Committed); Assert.AreEqual(1UL, report.ActualWriterResult); Assert.AreEqual(1UL, report.ReturnValue);
        }
    }
}
