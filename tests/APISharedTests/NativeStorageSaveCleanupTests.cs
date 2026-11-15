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
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void OrdinaryStorageCheckpointKeepsItsSchemaOneOrTwoEnvelopeAndPayload(bool hasCargo)
        {
            var state = State(out var good);
            if (hasCargo) Create(state, good);
            state.Capture(out byte[] goods, out byte[] stores);
            int schema = hasCargo ? 2 : 1;
            var original = new NativeStorageProductionSave(state, new object());
            var explicitOrdinary = new NativeStorageProductionSave(state, new object(), false);
            var originalWriter = Capture(original); var writer = Capture(explicitOrdinary);
            Assert.AreEqual(schema, original.ReaderVersion); Assert.AreEqual(schema, explicitOrdinary.ReaderVersion);
            Assert.AreEqual(schema, writer.RecordVersion); Assert.AreEqual(schema, writer.MinimumReaderVersion);
            var legacy = new[] { new KeyValuePair<string, byte[]>("ledger", goods), new KeyValuePair<string, byte[]>("stores", stores) };
            byte[] expected = VersionedSaveRecordCodec.Encode(schema, schema, legacy);
            CollectionAssert.AreEqual(expected, Record(originalWriter));
            CollectionAssert.AreEqual(expected, Record(writer));
            Assert.IsTrue(original.MatchesRecord(expected)); Assert.IsTrue(explicitOrdinary.MatchesRecord(expected));
            Assert.IsFalse(explicitOrdinary.MatchesRecord(VersionedSaveRecordCodec.Encode(3, 3, writer.Written)));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ProtectedStorageCheckpointUsesReaderThreeWithoutRelabelingStoreBytes(bool hasCargo)
        {
            var state = State(out var good);
            if (hasCargo) Create(state, good);
            state.Capture(out byte[] goods, out byte[] stores);
            int schema = hasCargo ? 2 : 1;
            var frozen = new NativeStorageProductionSave(state, new object(), true);
            Assert.IsTrue(VersionedSaveKnownEntries.TryCreate(new[] { "ledger", "stores", NativeStorageProductionSave.CriticalMarkerEntry },
                out var known, out string reason), reason);
            var writer = new VersionedSaveWriter(known, true, false, "synthetic", 3, 3, NativeStorageProductionSave.CriticalMarkerEntry);
            frozen.Write(writer);
            Assert.AreEqual(schema, frozen.ReaderVersion);
            Assert.AreEqual(3, writer.RecordVersion); Assert.AreEqual(3, writer.MinimumReaderVersion);
            Assert.AreEqual(2, writer.Count, "The checkpoint writes only its pair; the registry injects the marker later.");
            Assert.IsFalse(writer.Written.ContainsKey(NativeStorageProductionSave.CriticalMarkerEntry));
            Assert.ThrowsExactly<ArgumentException>(() => writer.Write(NativeStorageProductionSave.CriticalMarkerEntry, new byte[] { 7 }));
            writer.Close();
            CollectionAssert.AreEqual(goods, writer.Written["ledger"]); CollectionAssert.AreEqual(stores, writer.Written["stores"]);
            // Model only the registry's post-callback insertion. Exact marker provenance
            // belongs to the real registry/frame verifier, not this physical-pair check.
            var marked = new SortedDictionary<string, byte[]>(writer.Written, StringComparer.Ordinal)
            { [NativeStorageProductionSave.CriticalMarkerEntry] = new byte[] { 7 } };
            byte[] record = VersionedSaveRecordCodec.Encode(3, 3, marked);
            Assert.IsTrue(frozen.MatchesRecord(record));
            var decoded = VersionedSaveRecordCodec.Decode(record, 3);
            Assert.AreEqual(VersionedSaveRecordCodec.DecodeStatus.Valid, decoded.Status);
            Assert.IsTrue(decoded.Entries.TryGet("ledger", out byte[] actualGoods));
            Assert.IsTrue(decoded.Entries.TryGet("stores", out byte[] actualStores));
            CollectionAssert.AreEqual(goods, actualGoods); CollectionAssert.AreEqual(stores, actualStores);
            Assert.AreEqual(VersionedSaveRecordCodec.DecodeStatus.TooNew, VersionedSaveRecordCodec.Decode(record, 2).Status);
            Assert.IsFalse(frozen.MatchesRecord(VersionedSaveRecordCodec.Encode(3, 2, marked)));
            Assert.IsFalse(frozen.MatchesRecord(VersionedSaveRecordCodec.Encode(schema, schema, marked)));
            Assert.IsFalse(new NativeStorageProductionSave(state, new object()).MatchesRecord(record));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CriticalFactoryCleanupRunsAfterEveryEarlierFailureAndPreservesCommittedResult(bool committed)
        {
            Assert.IsNull(VersionedSaveFactoryFrame.Current);
            var primary = committed ? null : new IOException("controlled-capture-failure");
            var restore = new IOException("restore"); var production = new IOException("production");
            var workshop = new IOException("workshop"); var storage = new IOException("storage");
            var calls = new List<string>();
            var frame = VersionedSaveFactoryFrame.Begin(context => null);
            if (primary != null) frame.RecordFailure(primary);
            try
            {
                var report = WorkerProductionCriticalSaveRuntime.CompleteSaveCleanup(1, committed, primary,
                    () => { calls.Add("restore"); throw restore; },
                    () => { calls.Add("production"); throw production; },
                    () => { calls.Add("workshops"); throw workshop; },
                    () => { calls.Add("storage"); throw storage; },
                    () => { calls.Add("frame"); frame.Dispose(); });
                // Check before the defensive caller finally, so a skipped frame
                // release cannot pass merely because this fixture cleans its TLS.
                Assert.IsNull(VersionedSaveFactoryFrame.Current);
                CollectionAssert.AreEqual(new[] { "restore", "production", "workshops", "storage", "frame" }, calls);
                Assert.AreSame(primary ?? restore, report.Primary);
                Assert.AreSame(restore, report.RestoreError); Assert.AreSame(production, report.ProductionError);
                Assert.AreSame(workshop, report.WorkshopError); Assert.AreSame(storage, report.StorageError);
                Assert.IsNull(report.FactoryFrameError); Assert.AreSame(primary, frame.Failure);
                Assert.IsTrue(report.CleanupFailed); Assert.AreEqual(committed, report.Committed);
                Assert.AreEqual(1UL, report.ActualWriterResult); Assert.AreEqual(committed ? 1UL : 0UL, report.ReturnValue);
            }
            finally { frame.Dispose(); }
            Assert.IsNull(VersionedSaveFactoryFrame.Current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CriticalFactoryCleanupFailureIsReportedWithoutReplacingTheFirstFailure(bool committed)
        {
            Assert.IsNull(VersionedSaveFactoryFrame.Current);
            var primary = committed ? null : new IOException("controlled-capture-failure");
            var frameError = new IOException("controlled-frame-release-failure");
            var calls = new List<string>();
            var frame = VersionedSaveFactoryFrame.Begin(context => null);
            if (primary != null) frame.RecordFailure(primary);
            try
            {
                var report = WorkerProductionCriticalSaveRuntime.CompleteSaveCleanup(1, committed, primary,
                    () => calls.Add("restore"), () => calls.Add("production"),
                    () => calls.Add("workshops"), () => calls.Add("storage"),
                    () =>
                    {
                        calls.Add("frame"); frame.Dispose();
                        // A controlled release fault follows actual TLS cleanup;
                        // this is not a native writer or archive-commit invocation.
                        throw frameError;
                    });
                Assert.IsNull(VersionedSaveFactoryFrame.Current);
                CollectionAssert.AreEqual(new[] { "restore", "production", "workshops", "storage", "frame" }, calls);
                Assert.AreSame(frameError, report.FactoryFrameError); Assert.AreSame(primary ?? frameError, report.Primary);
                Assert.IsNull(report.RestoreError); Assert.IsNull(report.ProductionError);
                Assert.IsNull(report.WorkshopError); Assert.IsNull(report.StorageError);
                Assert.AreSame(primary, frame.Failure);
                Assert.IsTrue(report.CleanupFailed); Assert.AreEqual(committed, report.Committed);
                Assert.AreEqual(1UL, report.ActualWriterResult); Assert.AreEqual(committed ? 1UL : 0UL, report.ReturnValue);
            }
            finally { frame.Dispose(); }
            Assert.IsNull(VersionedSaveFactoryFrame.Current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CriticalCohortDemandDoesNotInventAStorageCheckpoint(bool criticalCohort)
        {
            var requirements = new WorkerProductionSaveRequirements(false, false, false, false, criticalCohort, false);
            Assert.AreEqual(criticalCohort, requirements.Transaction);
            Assert.IsFalse(requirements.Storage); Assert.IsFalse(requirements.Production);
            Assert.IsFalse(requirements.Workshops); Assert.IsFalse(requirements.Faulted);
        }

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
