using System;
using System.Collections.Generic;
using System.IO;
using APIShared.Economy;
using APIShared.Goods;
using APIShared.Savegames;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Actual state/codec, save writer, appended archive reader and atomic file replacement.
    // No installed native writer/game session is launched by these bounded file fixtures.
    [TestClass]
    public class NativeStorageCargoSaveTests
    {
        private static NativeStorageState State(out CustomGoodId good)
        {
            var state = new NativeStorageState(); good = new CustomGoodId("producer.example", "fish");
            Assert.IsTrue(state.TryRegister(good, StoragePool.Granary, out string reason), reason);
            return state;
        }
        private static NativeStorageState.CargoLot Create(NativeStorageState state, CustomGoodId good)
        {
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareCreateCargo(good, 1, 1, 8, out var prepared, out var lot, out string reason), reason);
            using (prepared) prepared.Publish();
            return lot;
        }
        private static VersionedSaveWriter Capture(NativeStorageProductionSave frozen)
        {
            Assert.IsTrue(VersionedSaveKnownEntries.TryCreate(new[] { "ledger", "stores" }, out var known, out string reason), reason);
            var writer = new VersionedSaveWriter(known, true, false, "synthetic", 2, 1);
            frozen.Write(writer); writer.Close(); return writer;
        }
        private static byte[] Record(VersionedSaveWriter writer) =>
            VersionedSaveRecordCodec.Encode(writer.RecordVersion, writer.MinimumReaderVersion, writer.Written);
        private static WorkerProductionSaveIoState CompleteIo()
        {
            var io = new WorkerProductionSaveIoState();
            io.ObserveWrite(7, 4, 4); io.ObserveWrite(7, 100, 100); io.ObserveClose(7, 0); return io;
        }
        private static void WriteArchive(string path, byte[] record, bool includeStorage = true)
        {
            using (var file = File.Create(path))
            {
                file.Write(new byte[1024], 0, 1024);
                using (var zip = new ZipOutputStream(file) { IsStreamOwner = false })
                {
                    zip.PutNextEntry(new ZipEntry(includeStorage ? "_SE_ModData_" + NativeStorageProductionSave.RecordIdentifier + ".msgpack" : "unrelated.bin"));
                    byte[] bytes = record ?? Array.Empty<byte>();
                    zip.Write(bytes, 0, bytes.Length); zip.CloseEntry(); zip.Finish();
                }
            }
        }
        private sealed class Files : IDisposable
        {
            private readonly string folder = Path.Combine(Path.GetTempPath(), "api-cargo-save-" + Guid.NewGuid().ToString("N"));
            internal string Destination => Path.Combine(folder, "destination.sav");
            internal string Temporary => Path.Combine(folder, "checkpoint.tmp");
            internal Files() { Directory.CreateDirectory(folder); File.WriteAllBytes(Destination, new byte[] { 9, 8, 7, 6 }); }
            public void Dispose()
            {
                if (File.Exists(Temporary)) File.Delete(Temporary);
                if (File.Exists(Destination)) File.Delete(Destination);
                Directory.Delete(folder);
            }
        }

        [TestMethod]
        public void UnusedCargoRetainsTheExactSchemaOneEntriesAndEnvelope()
        {
            NativeStorageState state = State(out _); state.Capture(out byte[] goods, out byte[] stores);
            var frozen = new NativeStorageProductionSave(state, new object());
            VersionedSaveWriter writer = Capture(frozen);
            Assert.AreEqual(1, writer.RecordVersion); Assert.AreEqual(1, writer.MinimumReaderVersion);
            var legacy = new[] { new KeyValuePair<string, byte[]>("ledger", goods), new KeyValuePair<string, byte[]>("stores", stores) };
            CollectionAssert.AreEqual(VersionedSaveRecordCodec.Encode(1, 1, legacy), Record(writer));
            Assert.IsTrue(frozen.MatchesRecord(Record(writer)));
        }

        [TestMethod]
        public void MetadataOnlyCargoChangesInvalidateFrozenIdentityAndKeepReaderTwoAfterDiscard()
        {
            NativeStorageState state = State(out var good); object world = new object();
            object physical = state.PublicationIdentity;
            var empty = new NativeStorageProductionSave(state, world);
            var lot = Create(state, good);
            Assert.AreSame(physical, state.PublicationIdentity); Assert.IsFalse(empty.Matches(state, world));
            var active = new NativeStorageProductionSave(state, world);
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareDiscardCargo(lot, out var discard, out _, out string reason), reason);
            using (discard) discard.Publish();
            Assert.AreSame(physical, state.PublicationIdentity); Assert.IsFalse(active.Matches(state, world));
            Assert.IsTrue(state.HasCargoHistory);
            var terminal = new NativeStorageProductionSave(state, world);
            VersionedSaveWriter writer = Capture(terminal);
            Assert.AreEqual(2, writer.RecordVersion); Assert.AreEqual(2, writer.MinimumReaderVersion);
            Assert.AreEqual(VersionedSaveRecordCodec.DecodeStatus.TooNew, VersionedSaveRecordCodec.Decode(Record(writer), 1).Status);
            Assert.IsTrue(terminal.MatchesRecord(Record(writer)));
        }

        [TestMethod]
        public void FrozenCargoVersionCannotBeRelabeledAsAnOlderReaderOrAnotherWorld()
        {
            NativeStorageState state = State(out var good); Create(state, good); object world = new object();
            var frozen = new NativeStorageProductionSave(state, world); VersionedSaveWriter writer = Capture(frozen);
            Assert.IsFalse(frozen.Matches(state, new object()));
            Assert.IsFalse(frozen.MatchesRecord(VersionedSaveRecordCodec.Encode(1, 1, writer.Written)));
            Assert.IsFalse(frozen.MatchesRecord(VersionedSaveRecordCodec.Encode(2, 1, writer.Written)));
            Assert.IsTrue(frozen.MatchesRecord(Record(writer)));
        }

        [TestMethod]
        public void CargoOnlyRequirementsProtectStorageWithoutInventingWorkerOrWorkshopRecords()
        {
            var cargo = new WorkerProductionSaveRequirements(false, false, false, true);
            Assert.IsTrue(cargo.Transaction); Assert.IsTrue(cargo.Storage);
            Assert.IsFalse(cargo.Production); Assert.IsFalse(cargo.Workshops);
            Assert.IsFalse(new WorkerProductionSaveRequirements(false, false, false, false).Transaction);
            var production = new WorkerProductionSaveRequirements(true, false, false, false);
            Assert.IsTrue(production.Production); Assert.IsTrue(production.Workshops); Assert.IsFalse(production.Storage);
            Assert.IsTrue(new WorkerProductionSaveRequirements(false, true, false, false).Transaction);
        }

        [TestMethod]
        public void CargoOnlyCheckpointCanCommitWithoutAnExtendedProductionRecord()
        {
            NativeStorageState state = State(out var good); Create(state, good);
            var frozen = new NativeStorageProductionSave(state, new object()); byte[] record = Record(Capture(frozen));
            using (var files = new Files())
            {
                WriteArchive(files.Temporary, record); frozen.VerifyArchive(files.Temporary);
                var io = CompleteIo(); var requirements = new WorkerProductionSaveRequirements(false, false, false, true);
                WorkerProductionCriticalSaveRuntime.CommitSavedFile(files.Temporary, files.Destination, null, in io, requirements);
                Assert.IsFalse(File.Exists(files.Temporary)); frozen.VerifyArchive(files.Destination);
                Assert.IsTrue(frozen.IsWritten(VersionedSaveWriteStatus.Written, false));
                Assert.IsFalse(frozen.IsWritten(VersionedSaveWriteStatus.KeptLoadedRecord, true));
            }
        }

        [TestMethod]
        public void CargoOnlyIncompleteNativeWritesOrCloseKeepThePreviousDestination()
        {
            NativeStorageState state = State(out var good); Create(state, good);
            var frozen = new NativeStorageProductionSave(state, new object()); byte[] record = Record(Capture(frozen));
            var requirements = new WorkerProductionSaveRequirements(false, false, false, true);
            using (var files = new Files())
            {
                byte[] previous = File.ReadAllBytes(files.Destination); WriteArchive(files.Temporary, record);
                frozen.VerifyArchive(files.Temporary);
                foreach (int result in new[] { -1, 0, 3 })
                {
                    var io = new WorkerProductionSaveIoState();
                    io.ObserveWrite(7, 4, result); io.ObserveWrite(7, 100, 100); io.ObserveClose(7, 0);
                    Assert.ThrowsExactly<IOException>(() => WorkerProductionCriticalSaveRuntime.CommitSavedFile(files.Temporary, files.Destination, null, in io, requirements));
                    CollectionAssert.AreEqual(previous, File.ReadAllBytes(files.Destination)); Assert.IsTrue(File.Exists(files.Temporary));
                }
                var failedClose = new WorkerProductionSaveIoState(); failedClose.ObserveWrite(7, 4, 4); failedClose.ObserveClose(7, -1);
                Assert.ThrowsExactly<IOException>(() => WorkerProductionCriticalSaveRuntime.CommitSavedFile(files.Temporary, files.Destination, null, in failedClose, requirements));
                CollectionAssert.AreEqual(previous, File.ReadAllBytes(files.Destination));
            }
        }

        [TestMethod]
        public void CargoOnlyMissingOrStaleStorageBytesCannotQualifyAndLegacyCommitStillRequiresItsPayload()
        {
            NativeStorageState state = State(out var good); object world = new object();
            var old = new NativeStorageProductionSave(state, world); byte[] stale = Record(Capture(old));
            Create(state, good);
            var current = new NativeStorageProductionSave(state, world); Capture(current);
            using (var files = new Files())
            {
                byte[] previous = File.ReadAllBytes(files.Destination);
                foreach (byte[] invalid in new[] { stale, Array.Empty<byte>(), new byte[] { 1, 2, 3 } })
                {
                    WriteArchive(files.Temporary, invalid);
                    Assert.ThrowsExactly<InvalidDataException>(() => current.VerifyArchive(files.Temporary));
                    CollectionAssert.AreEqual(previous, File.ReadAllBytes(files.Destination));
                }
                WriteArchive(files.Temporary, new byte[] { 42 }, false);
                Assert.ThrowsExactly<InvalidDataException>(() => current.VerifyArchive(files.Temporary));
                CollectionAssert.AreEqual(previous, File.ReadAllBytes(files.Destination));
                WriteArchive(files.Temporary, Record(Capture(new NativeStorageProductionSave(state, world))));
                var io = CompleteIo();
                Assert.ThrowsExactly<InvalidDataException>(() => WorkerProductionCriticalSaveRuntime.CommitSavedFile(files.Temporary, files.Destination, null, in io));
                CollectionAssert.AreEqual(previous, File.ReadAllBytes(files.Destination));
            }
        }
    }
}
