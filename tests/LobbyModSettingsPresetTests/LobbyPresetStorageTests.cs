using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;

namespace LobbyModSettingsPresetTests
{
    [TestClass]
    [DoNotParallelize]
    public class LobbyPresetStorageTests
    {
        private string directory;
        private string filePath;
        private LobbyPresetStorage storage;

        [TestInitialize]
        public void Initialize()
        {
            directory = Path.Combine(Path.GetTempPath(), "APIShared-PresetStorage-" + Guid.NewGuid().ToString("N"));
            filePath = Path.Combine(directory, "settings.msgpack");
            storage = new LobbyPresetStorage(filePath, null, "Community mod");
        }

        [TestCleanup]
        public void Cleanup()
        {
            string prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "APIShared-PresetStorage-");
            Assert.IsTrue(Path.GetFullPath(directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        [TestMethod]
        public void RoundTripUsesTheExistingMessagePackDictionaryFormat()
        {
            Assert.IsFalse(storage.Exists);
            Assert.IsFalse(storage.TryRead(out _));
            var payload = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["__SerpPresetSchemaVersion"] = MessagePackSerializer.Serialize(3),
                ["setting"] = MessagePackSerializer.Serialize(123)
            };
            storage.Write(payload);
            Assert.IsTrue(storage.Exists);
            Assert.IsTrue(storage.TryRead(out Dictionary<string, byte[]> read));
            CollectionAssert.AreEqual(payload["setting"], read["setting"]);
            var externalRead = MessagePackSerializer.Deserialize<Dictionary<string, byte[]>>(File.ReadAllBytes(filePath));
            Assert.AreEqual(3, MessagePackSerializer.Deserialize<int>(externalRead["__SerpPresetSchemaVersion"]));
            Assert.AreEqual(123, MessagePackSerializer.Deserialize<int>(externalRead["setting"]));
            Assert.AreEqual(1, storage.TestWriteCount);
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp-*").Length);
        }

        [TestMethod]
        public void FailedReplacementPreservesTheOldFileAndRemovesTemporaryData()
        {
            storage.Write(new Dictionary<string, byte[]> { ["value"] = new byte[] { 1 } });
            byte[] original = File.ReadAllBytes(filePath);
            using (var locked = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                storage.Write(new Dictionary<string, byte[]> { ["value"] = new byte[] { 2 } });
                CollectionAssert.AreEqual(original, File.ReadAllBytes(filePath));
                Assert.AreEqual(1, storage.TestWriteCount);
                Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp-*").Length);
            }
            storage.Write(new Dictionary<string, byte[]> { ["value"] = new byte[] { 2 } });
            Assert.IsTrue(storage.TryRead(out Dictionary<string, byte[]> updated));
            CollectionAssert.AreEqual(new byte[] { 2 }, updated["value"]);
            Assert.AreEqual(2, storage.TestWriteCount);
        }

        [TestMethod]
        public void InvalidDataIsNotOverwrittenByReadingOrBackingItUp()
        {
            Directory.CreateDirectory(directory);
            byte[] corrupt = { 0xc1 }; // Reserved MessagePack byte, not a valid dictionary.
            File.WriteAllBytes(filePath, corrupt);
            Assert.IsFalse(storage.TryRead(out Dictionary<string, byte[]> read));
            Assert.IsNull(read);
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(filePath));
            storage.BackupCorruptFile();
            string[] backups = Directory.GetFiles(directory, "*.corrupt-*");
            Assert.AreEqual(1, backups.Length);
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(backups[0]));
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(filePath));
            Assert.AreEqual(0, storage.TestWriteCount);
        }
    }
}
