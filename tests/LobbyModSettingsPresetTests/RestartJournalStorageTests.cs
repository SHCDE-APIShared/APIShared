using APIShared.Internal;
using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LobbyModSettingsPresetTests
{
    // Exercises the production resolver and migration, rather than the classic direct-path seam.
    [TestClass]
    [DoNotParallelize]
    public sealed class RestartJournalStorageTests
    {
        private string root;
        private Func<string> previousDocuments;

        [TestInitialize]
        public void Initialize()
        {
            root = Path.Combine(Path.GetTempPath(), "ApiJournal", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            previousDocuments = PersonalDataFile.TestDocumentsDirectory;
            PersonalDataFile.TestDocumentsDirectory = () => Path.Combine(root, "Documents");
            ModSettingsApplication.ResetForTests(null);
        }

        [TestCleanup]
        public void Cleanup()
        {
            PersonalDataIO.TestBeforeOperation = null;
            ModSettingsApplication.ResetForTests(null);
            PersonalDataFile.TestDocumentsDirectory = previousDocuments;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        private string Config(string name) => Path.Combine(root, name, "BepInEx", "config");
        private static string Legacy(string config) => Path.Combine(config, "APIShared", "RestartPreparation.json");

        private static byte[] Journal(string identity)
        {
            string payload = DependencyFreeJson.Serialize(new Dictionary<string, object>
            {
                ["schema"] = 1,
                ["context"] = identity,
                ["mods"] = new Dictionary<string, object> { ["probe"] = new Dictionary<string, object>() },
                ["personal"] = new Dictionary<string, object>(),
                ["sources"] = new Dictionary<string, object> { ["probe"] = new Dictionary<string, object>() }
            });
            string checksum;
            using (var sha = SHA256.Create())
                checksum = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).Replace("-", "");
            return Encoding.UTF8.GetBytes(DependencyFreeJson.Serialize(new Dictionary<string, object>
            { ["payload"] = payload, ["sha256"] = checksum }));
        }

        private static void Write(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
        }

        [TestMethod]
        public void EarlyVisibilityMigratesBeforeAnyEndpointRegistrationAndRetainsJournalFormat()
        {
            string config = Config("first");
            byte[] bytes = Journal("mission|first");
            Write(Legacy(config), bytes);
            string target = ModSettingsApplication.ResetJournalStorageForTests(config);

            Assert.IsTrue(ModSettingsApplication.HasRestartPreparation);
            Assert.IsFalse(File.Exists(Legacy(config)));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target));
            ModSettingsApplication.EnterContext("mission|first");
            Assert.IsNotNull(ModSettingsApplication.ResumeSnapshot("probe"));
        }

        [TestMethod]
        public void ConsumedAndDiscardedJournalsNeverReimportLaterLegacyCopies()
        {
            foreach (bool discard in new[] { false, true })
            {
                string config = Config(discard ? "discard" : "consume");
                byte[] bytes = Journal("mission|same");
                Write(Legacy(config), bytes);
                string target = ModSettingsApplication.ResetJournalStorageForTests(config);
                ModSettingsApplication.EnterContext("mission|same");
                if (discard) ModSettingsApplication.DiscardPreparation();
                else ModSettingsApplication.ConfirmStarted();
                Assert.IsFalse(File.Exists(target));

                Write(Legacy(config), bytes); // A stale old version or failed legacy cleanup must not revive it.
                ModSettingsApplication.ResetJournalStorageForTests(config);
                Assert.IsFalse(ModSettingsApplication.HasRestartPreparation);
                ModSettingsApplication.EnterContext("mission|same");
                Assert.IsNull(ModSettingsApplication.ResumeSnapshot("probe"));
            }
        }

        [TestMethod]
        public void SharedDocumentsRetainsSeparateInstallationsAndStableNormalizedIdentity()
        {
            string first = Config("first");
            string second = Config("second");
            Write(Legacy(first), Journal("mission|first"));
            Write(Legacy(second), Journal("mission|second"));
            string firstTarget = ModSettingsApplication.ResetJournalStorageForTests(first);
            ModSettingsApplication.EnterContext("mission|first");
            string secondTarget = ModSettingsApplication.ResetJournalStorageForTests(second);
            ModSettingsApplication.EnterContext("mission|second");
            Assert.AreNotEqual(firstTarget, secondTarget);
            Assert.IsTrue(File.Exists(firstTarget) && File.Exists(secondTarget));
            Assert.AreEqual(firstTarget, ModSettingsApplication.CreateJournalStorage(first.ToUpperInvariant() + Path.DirectorySeparatorChar).Path);
            ModSettingsApplication.ResetJournalStorageForTests(first);
            ModSettingsApplication.EnterContext("mission|first");
            Assert.IsNotNull(ModSettingsApplication.ResumeSnapshot("probe"));
        }

        [TestMethod]
        public void CorruptDocumentsJournalNeverFallsBackToValidLegacyPreparation()
        {
            string config = Config("corrupt");
            Write(Legacy(config), Journal("mission|old"));
            string target = ModSettingsApplication.ResetJournalStorageForTests(config);
            byte[] corrupt = Encoding.UTF8.GetBytes("{}");
            Write(target, corrupt);
            Assert.IsTrue(ModSettingsApplication.HasRestartPreparation);
            Assert.ThrowsExactly<InvalidDataException>(() => ModSettingsApplication.EnterContext("mission|old"));
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(target));
            Assert.IsNull(ModSettingsApplication.ResumeSnapshot("probe"));
        }

        [TestMethod]
        public void CorruptLegacyBytesArePreservedAtDocumentsAndRemainRejected()
        {
            string config = Config("corrupt-legacy");
            byte[] corrupt = Encoding.UTF8.GetBytes("{}");
            Write(Legacy(config), corrupt);
            string target = ModSettingsApplication.ResetJournalStorageForTests(config);
            Assert.ThrowsExactly<InvalidDataException>(() => ModSettingsApplication.EnterContext("mission|old"));
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(target));
            Assert.IsFalse(File.Exists(Legacy(config)));
            ModSettingsApplication.DiscardPreparation();
            Assert.IsFalse(ModSettingsApplication.HasRestartPreparation);
        }

        [TestMethod]
        public void OversizedLegacyJournalIsRejectedBeforeMigrationOrDeletion()
        {
            string config = Config("oversize");
            Write(Legacy(config), new byte[8 * 1024 * 1024 + 1]);
            string target = ModSettingsApplication.ResetJournalStorageForTests(config);
            Assert.ThrowsExactly<IOException>(() => ModSettingsApplication.EnterContext("mission|old"));
            Assert.IsTrue(File.Exists(Legacy(config)));
            Assert.IsFalse(File.Exists(target));
        }

        [TestMethod]
        [DataRow(true, false)]
        [DataRow(false, false)]
        [DataRow(false, true)]
        public void ExplicitDiscardPreservesOversizedOriginalAndNeverReimportsIt(bool legacy, bool existingReceipt)
        {
            string config = Config("discard-oversize");
            string target = ModSettingsApplication.ResetJournalStorageForTests(config);
            if (existingReceipt) ModSettingsApplication.CreateJournalStorage(config).WriteAllBytes(Journal("mission|old"));
            string oversized = legacy ? Legacy(config) : target;
            long size = 8 * 1024 * 1024 + 1;
            Write(oversized, new byte[(int)size]);
            Assert.IsTrue(ModSettingsApplication.HasRestartPreparation, "The discard control must remain reachable.");
            Assert.ThrowsExactly<IOException>(() => ModSettingsApplication.ConfirmStarted());
            ModSettingsApplication.DiscardPreparation();
            Assert.IsFalse(File.Exists(target));
            if (legacy) Assert.AreEqual(size, new FileInfo(oversized).Length);
            else
            {
                string[] backups = Directory.GetFiles(Path.GetDirectoryName(target), Path.GetFileName(target) + ".invalid-*.bak");
                Assert.HasCount(1, backups);
                Assert.AreEqual(size, new FileInfo(backups[0]).Length);
            }
            ModSettingsApplication.ResetJournalStorageForTests(config);
            Assert.IsFalse(ModSettingsApplication.HasRestartPreparation);
        }

        [TestMethod]
        public void ExplicitDiscardDoesNotBypassInstallationIdentityWhenTargetIsOversized()
        {
            string config = Config("discard-identity");
            string target = ModSettingsApplication.ResetJournalStorageForTests(config);
            new PersonalDataFile(target, null, identity: "another installation").WriteAllBytes(Journal("mission|other"));
            long size = 8 * 1024 * 1024 + 1;
            Write(target, new byte[(int)size]);
            Assert.IsTrue(ModSettingsApplication.HasRestartPreparation);
            Assert.IsFalse(string.IsNullOrEmpty(ModSettingsApplication.DescribeRestartPreparation()));
            Assert.ThrowsExactly<IOException>(() => ModSettingsApplication.DiscardPreparation());
            Assert.AreEqual(size, new FileInfo(target).Length);
            Assert.HasCount(0, Directory.GetFiles(Path.GetDirectoryName(target), Path.GetFileName(target) + ".invalid-*.bak"));
        }

        [TestMethod]
        public void UnreadableJournalRemainsVisibleButDoesNotPermitReadsConsumeOrDiscard()
        {
            string config = Config("access");
            byte[] bytes = Journal("mission|same");
            string target = ModSettingsApplication.ResetJournalStorageForTests(config);
            Write(target, bytes);
            PersonalDataIO.TestBeforeOperation = (operation, path) =>
            {
                if (operation == "read" && path == target) throw new UnauthorizedAccessException("Fixture denies journal access.");
            };
            Assert.IsTrue(ModSettingsApplication.HasRestartPreparation);
            StringAssert.Contains(ModSettingsApplication.DescribeRestartPreparation(), "Fixture denies journal access");
            Assert.ThrowsExactly<IOException>(() => ModSettingsApplication.EnterContext("mission|same"));
            Assert.ThrowsExactly<IOException>(() => ModSettingsApplication.ConfirmStarted());
            Assert.ThrowsExactly<IOException>(() => ModSettingsApplication.DiscardPreparation());
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target));
        }

        [TestMethod]
        public void ForeignInstallationReceiptRemainsVisibleAndBlocksAllJournalActions()
        {
            string config = Config("foreign");
            string target = ModSettingsApplication.ResetJournalStorageForTests(config);
            byte[] bytes = Journal("mission|same");
            new PersonalDataFile(target, null, identity: "another installation").WriteAllBytes(bytes);
            Assert.IsTrue(ModSettingsApplication.HasRestartPreparation);
            StringAssert.Contains(ModSettingsApplication.DescribeRestartPreparation(), "another installation");
            Assert.ThrowsExactly<IOException>(() => ModSettingsApplication.EnterContext("mission|same"));
            Assert.ThrowsExactly<IOException>(() => ModSettingsApplication.ConfirmStarted());
            Assert.ThrowsExactly<IOException>(() => ModSettingsApplication.DiscardPreparation());
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target));
        }

        [TestMethod]
        public void CompleteInstallationIdentityRejectsAnotherOwnersReceiptEvenWithReadableJournalBytes()
        {
            string config = Config("identity");
            var correct = ModSettingsApplication.CreateJournalStorage(config);
            byte[] bytes = Journal("mission|same");
            correct.WriteAllBytes(bytes);
            var other = new PersonalDataFile(correct.Path, null, identity: "another installation");
            Assert.ThrowsExactly<IOException>(() => other.ReadAllBytes());
            CollectionAssert.AreEqual(bytes, correct.ReadAllBytes());
        }

        [TestMethod]
        public void FailedMigrationReceiptBlocksConsumptionAndRetriesWithoutLosingEitherCopy()
        {
            string config = Config("receipt-failure");
            byte[] bytes = Journal("mission|same");
            Write(Legacy(config), bytes);
            string target = ModSettingsApplication.ResetJournalStorageForTests(config);
            PersonalDataIO.TestBeforeOperation = (operation, path) =>
            {
                if (operation == "publish" && path.EndsWith(".migration.json", StringComparison.Ordinal))
                    throw new IOException("Fixture blocks durable migration receipt.");
            };
            ModSettingsApplication.EnterContext("mission|same"); // Read-only recovery is available during this process.
            Assert.ThrowsExactly<IOException>(() => ModSettingsApplication.ConfirmStarted());
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Legacy(config)));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target));

            PersonalDataIO.TestBeforeOperation = null;
            ModSettingsApplication.ResetJournalStorageForTests(config);
            ModSettingsApplication.EnterContext("mission|same");
            ModSettingsApplication.ConfirmStarted();
            Assert.IsFalse(File.Exists(Legacy(config)) || File.Exists(target));
        }

        [TestMethod]
        public void FailedLegacyDeletionCannotReviveConsumedPreparationAfterProcessRestart()
        {
            string config = Config("cleanup-failure");
            Write(Legacy(config), Journal("mission|same"));
            string target = ModSettingsApplication.ResetJournalStorageForTests(config);
            PersonalDataIO.TestBeforeOperation = (operation, path) =>
            {
                if (operation == "cleanup") throw new IOException("Fixture locks the old file.");
            };
            ModSettingsApplication.EnterContext("mission|same");
            ModSettingsApplication.ConfirmStarted();
            Assert.IsTrue(File.Exists(Legacy(config)));
            Assert.IsFalse(File.Exists(target));
            ModSettingsApplication.ResetJournalStorageForTests(config);
            Assert.IsFalse(ModSettingsApplication.HasRestartPreparation);
            ModSettingsApplication.EnterContext("mission|same");
            Assert.IsNull(ModSettingsApplication.ResumeSnapshot("probe"));
        }
    }
}
