using System;
using System.Collections.Generic;
using System.IO;
using APIShared.Economy;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    public partial class NativeStorageCargoSaveTests
    {
        // The real registry freezes and serves one record; only the Extender transport
        // delegate is supplied by this fixture. No native writer or production enrollment is run.
        private sealed class FactoryArchiveFixture
        {
            internal readonly VersionedSaveDataRegistry Registry;
            internal readonly Func<VersionedSaveContext, byte[]> Save;
            internal readonly VersionedSaveCriticalSource Enrollment;
            private readonly VersionedSavePreparedOrigin origin;
            internal string FreezeFailure;

            internal FactoryArchiveFixture()
            {
                Func<VersionedSaveContext, byte[]> save = null;
                Registry = new VersionedSaveDataRegistry((identifier, capture, load) =>
                {
                    save = capture; return true;
                }, ignored => { }, ignored => { });
                VersionedSavePreparedSource prepared = null;
                VersionedSavePreparedOrigin loaded = null;
                string originFailure = null;
                var registration = new VersionedSaveDataRegistration("factory.fixture",
                    NativeStorageProductionSave.RecordIdentifier, 3, 1, new[] { "value", "cutover" }, writer =>
                    {
                        writer.Write("value", new byte[] { 1 }); return true;
                    }, ignored => { })
                {
                    Scope = VersionedSaveScope.SaveGamesAndMaps,
                    SimulationRelevant = true,
                    Prepared = receiver =>
                    {
                        if (!prepared.TryGetCurrent(receiver, out loaded, out string reason)) originFailure = reason;
                    }
                };
                Assert.IsTrue(Registry.TryRegister(registration, out var handle, out string error), error);
                Assert.IsTrue(Registry.TryTrackPreparedSource("factory.fixture", handle, out prepared, out error), error);
                Assert.IsTrue(Registry.TryEnrollCriticalSource(prepared, "cutover", 3, out var enrollment, out error), error);
                Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "loaded-source.sav");
                Registry.PrepareLoad(); Registry.EndLoad();
                Assert.IsNull(originFailure); Assert.IsNotNull(loaded); Assert.IsNotNull(save);
                Assert.IsTrue(prepared.TryValidateCompleted(loaded, out error), error);
                Save = save; Enrollment = enrollment; origin = loaded;
            }

            internal VersionedSaveCriticalCapture Freeze(VersionedSaveContext context)
            {
                Registry.TryFreezeCriticalRecords(new[] { new VersionedSaveCriticalRecord(Enrollment, origin, new byte[] { 7 }) },
                    context, 4096, out var capture, out FreezeFailure);
                return capture;
            }
        }

        [TestMethod]
        public void ActiveFactoryFrameWithoutCaptureCannotReplaceThePreviousDestination()
        {
            Assert.IsNull(VersionedSaveFactoryFrame.Current);
            int captures = 0;
            using (var files = new Files())
            {
                byte[] previous = File.ReadAllBytes(files.Destination);
                WriteArchive(files.Temporary, new byte[] { 1 });
                var io = CompleteIo(); var requirements = new WorkerProductionSaveRequirements(false, false, false, true);
                var frame = VersionedSaveFactoryFrame.Begin(context => { captures++; return null; });
                try
                {
                    var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
                        WorkerProductionCriticalSaveRuntime.CommitSavedFile(files.Temporary, files.Destination, null, in io, requirements));
                    Assert.AreSame(frame, VersionedSaveFactoryFrame.Current);
                    Assert.AreSame(failure, frame.Failure); Assert.AreEqual(0, captures);
                    CollectionAssert.AreEqual(previous, File.ReadAllBytes(files.Destination));
                    Assert.IsTrue(File.Exists(files.Temporary));
                }
                finally { frame.Dispose(); }
                Assert.IsNull(VersionedSaveFactoryFrame.Current);
            }
        }

        [TestMethod]
        public void FactoryFailureRemainsPrimaryAndCallerFinallyClosesFrameAfterEveryCleanupFails()
        {
            Assert.IsNull(VersionedSaveFactoryFrame.Current);
            var failure = new IOException("controlled-factory-capture-failure");
            var restore = new IOException("restore"); var production = new IOException("production");
            var workshop = new IOException("workshop"); var storage = new IOException("storage");
            var calls = new List<string>();
            using (var files = new Files())
            {
                byte[] previous = File.ReadAllBytes(files.Destination);
                WriteArchive(files.Temporary, new byte[] { 1 });
                var io = CompleteIo(); var requirements = new WorkerProductionSaveRequirements(false, false, false, true);
                var frame = VersionedSaveFactoryFrame.Begin(context => throw failure);
                WorkerProductionSaveCleanupReport report;
                try
                {
                    frame.Capture(new VersionedSaveContext(true, false, null));
                    Assert.AreSame(failure, frame.Failure);
                    var rejected = Assert.ThrowsExactly<IOException>(() =>
                        WorkerProductionCriticalSaveRuntime.CommitSavedFile(files.Temporary, files.Destination, null, in io, requirements));
                    Assert.AreSame(failure, rejected);
                    report = WorkerProductionCriticalSaveRuntime.CompleteSaveCleanup(1, false, rejected,
                        () => { calls.Add("restore"); throw restore; },
                        () => { calls.Add("production"); throw production; },
                        () => { calls.Add("workshops"); throw workshop; },
                        () => { calls.Add("storage"); throw storage; });
                    Assert.AreSame(frame, VersionedSaveFactoryFrame.Current);
                }
                finally { frame.Dispose(); }
                Assert.IsNull(VersionedSaveFactoryFrame.Current);
                CollectionAssert.AreEqual(new[] { "restore", "production", "workshops", "storage" }, calls);
                Assert.AreSame(failure, report.Primary); Assert.AreSame(restore, report.RestoreError);
                Assert.AreSame(production, report.ProductionError); Assert.AreSame(workshop, report.WorkshopError);
                Assert.AreSame(storage, report.StorageError); Assert.AreSame(failure, frame.Failure);
                Assert.IsTrue(report.CleanupFailed); Assert.IsFalse(report.Committed); Assert.AreEqual(0UL, report.ReturnValue);
                CollectionAssert.AreEqual(previous, File.ReadAllBytes(files.Destination));
                Assert.IsTrue(File.Exists(files.Temporary));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void WrittenFactoryCaptureRejectsOmittedOrChangedArchiveRecordsBeforeReplacement(bool omitRecord)
        {
            Assert.IsNull(VersionedSaveFactoryFrame.Current);
            var fixture = new FactoryArchiveFixture();
            var context = new VersionedSaveContext(true, false, null);
            using (var files = new Files())
            {
                byte[] previous = File.ReadAllBytes(files.Destination);
                var io = CompleteIo(); var requirements = new WorkerProductionSaveRequirements(false, false, false, true);
                var frame = VersionedSaveFactoryFrame.Begin(fixture.Freeze);
                try
                {
                    frame.Capture(context);
                    Assert.IsNull(frame.Failure, fixture.FreezeFailure);
                    byte[] written = fixture.Save(context); Assert.IsNotNull(written);
                    Assert.IsTrue(frame.TryGetWrittenCapture(out var capture, out string reason), reason);
                    Assert.IsTrue(capture.AllWritten(out reason), reason);
                    byte[] changed = (byte[])written.Clone(); changed[changed.Length - 1] ^= 1;
                    WriteArchive(files.Temporary, changed, !omitRecord);
                    var failure = Assert.ThrowsExactly<InvalidDataException>(() =>
                        WorkerProductionCriticalSaveRuntime.CommitSavedFile(files.Temporary, files.Destination, null, in io, requirements));
                    Assert.AreSame(failure, frame.Failure);
                    CollectionAssert.AreEqual(previous, File.ReadAllBytes(files.Destination));
                    Assert.IsTrue(File.Exists(files.Temporary));
                    // Replacing the bad bytes cannot erase the original verifier failure.
                    WriteArchive(files.Temporary, written);
                    var repeated = Assert.ThrowsExactly<InvalidDataException>(() =>
                        WorkerProductionCriticalSaveRuntime.CommitSavedFile(files.Temporary, files.Destination, null, in io, requirements));
                    Assert.AreSame(failure, repeated);
                    CollectionAssert.AreEqual(previous, File.ReadAllBytes(files.Destination));
                }
                finally { frame.Dispose(); }
                Assert.IsNull(VersionedSaveFactoryFrame.Current);
            }
        }

        [TestMethod]
        public void WrittenFactoryCaptureAllowsItsExactCompletedArchiveToReplaceDestination()
        {
            Assert.IsNull(VersionedSaveFactoryFrame.Current);
            var fixture = new FactoryArchiveFixture();
            var context = new VersionedSaveContext(true, false, null);
            using (var files = new Files())
            {
                var io = CompleteIo(); var requirements = new WorkerProductionSaveRequirements(false, false, false, true);
                var frame = VersionedSaveFactoryFrame.Begin(fixture.Freeze);
                try
                {
                    frame.Capture(context);
                    Assert.IsNull(frame.Failure, fixture.FreezeFailure);
                    byte[] written = fixture.Save(context); Assert.IsNotNull(written);
                    Assert.IsTrue(frame.TryGetWrittenCapture(out _, out string reason), reason);
                    WriteArchive(files.Temporary, written);
                    byte[] expected = File.ReadAllBytes(files.Temporary);
                    WorkerProductionCriticalSaveRuntime.CommitSavedFile(files.Temporary, files.Destination, null, in io, requirements);
                    Assert.IsNull(frame.Failure); Assert.IsFalse(File.Exists(files.Temporary));
                    CollectionAssert.AreEqual(expected, File.ReadAllBytes(files.Destination));
                }
                finally { frame.Dispose(); }
                Assert.IsNull(VersionedSaveFactoryFrame.Current);
            }
        }

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
        public void RetainedFactoryFrameCannotBeHiddenFromReplacementByEarlyDisposal()
        {
            Assert.IsNull(VersionedSaveFactoryFrame.Current);
            using (var files = new Files())
            {
                byte[] previous = File.ReadAllBytes(files.Destination);
                WriteArchive(files.Temporary, new byte[] { 1 });
                var io = CompleteIo(); var requirements = new WorkerProductionSaveRequirements(false, false, false, true);
                var frame = VersionedSaveFactoryFrame.Begin(context => null);
                frame.Dispose();
                Assert.IsNull(VersionedSaveFactoryFrame.Current);
                var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    WorkerProductionCriticalSaveRuntime.CommitSavedFile(files.Temporary, files.Destination, null, in io, requirements, frame));
                Assert.AreSame(failure, frame.Failure);
                CollectionAssert.AreEqual(previous, File.ReadAllBytes(files.Destination));
                Assert.IsTrue(File.Exists(files.Temporary));
            }
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
        [DataRow(false)]
        [DataRow(true)]
        public void AFrameOpenedAfterTheRetainedNullOwnerCannotUseTheDormantReplacementPath(bool earlierFactoryFailure)
        {
            Assert.IsNull(VersionedSaveFactoryFrame.Current);
            // This is controlled managed owner data, not an invocation or proof
            // of the native Original path: the owner retained null before entry.
            VersionedSaveFactoryFrame expected = VersionedSaveFactoryFrame.Current;
            using (var files = new Files())
            {
                var state = State(out var good); Create(state, good);
                var storage = new NativeStorageProductionSave(state, new object());
                byte[] record = Record(Capture(storage));
                WriteArchive(files.Temporary, record); storage.VerifyArchive(files.Temporary);
                byte[] previous = File.ReadAllBytes(files.Destination);
                var io = CompleteIo(); var requirements = new WorkerProductionSaveRequirements(false, false, false, true);
                var originalFailure = new IOException("controlled-late-frame-factory-failure");
                var late = VersionedSaveFactoryFrame.Begin(context => throw originalFailure);
                try
                {
                    if (earlierFactoryFailure) late.Capture(new VersionedSaveContext(true, false, null));
                    Exception rejected;
                    if (earlierFactoryFailure)
                    {
                        rejected = Assert.ThrowsExactly<IOException>(() =>
                            WorkerProductionCriticalSaveRuntime.CommitSavedFile(files.Temporary, files.Destination, null, in io, requirements, expected));
                        Assert.AreSame(originalFailure, rejected);
                    }
                    else
                        rejected = Assert.ThrowsExactly<InvalidOperationException>(() =>
                            WorkerProductionCriticalSaveRuntime.CommitSavedFile(files.Temporary, files.Destination, null, in io, requirements, expected));
                    Assert.AreSame(rejected, late.Failure);
                    CollectionAssert.AreEqual(previous, File.ReadAllBytes(files.Destination));
                    Assert.IsTrue(File.Exists(files.Temporary));
                    int releases = 0;
                    var cleanupFailure = new IOException("controlled-late-frame-cleanup");
                    var report = WorkerProductionCriticalSaveRuntime.CompleteSaveCleanup(1, false, rejected,
                        () => throw cleanupFailure, () => releases++, null, () => releases++);
                    Assert.AreSame(rejected, report.Primary); Assert.AreSame(cleanupFailure, report.RestoreError);
                    Assert.AreEqual(2, releases); Assert.AreEqual(0UL, report.ReturnValue);
                    Assert.AreSame(rejected, late.Failure);
                }
                finally { late.Dispose(); }
                Assert.IsNull(VersionedSaveFactoryFrame.Current);
                // A separate positive control follows the unchanged verified-I/O
                // path with expected=null/current=null; it does not retry the
                // failed transaction or erase its retained first failure.
                using (var control = new Files())
                {
                    WriteArchive(control.Temporary, record); storage.VerifyArchive(control.Temporary);
                    byte[] exact = File.ReadAllBytes(control.Temporary);
                    WorkerProductionCriticalSaveRuntime.CommitSavedFile(control.Temporary, control.Destination, null, in io, requirements, expected);
                    Assert.IsFalse(File.Exists(control.Temporary));
                    CollectionAssert.AreEqual(exact, File.ReadAllBytes(control.Destination));
                }
                CollectionAssert.AreEqual(previous, File.ReadAllBytes(files.Destination));
                Assert.IsTrue(File.Exists(files.Temporary));
            }
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
