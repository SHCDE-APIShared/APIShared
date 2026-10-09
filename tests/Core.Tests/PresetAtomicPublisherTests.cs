using APIShared.ModSettings;
using APIShared.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests
{
    [TestClass]
    public class PresetAtomicPublisherTests
    {
        [DataTestMethod]
        [DataRow(null, "destination")]
        [DataRow("", "destination")]
        [DataRow("temporary", null)]
        [DataRow("temporary", "")]
        public void RejectsMissingPathsBeforeFileOperations(string temporary, string destination)
        {
            var operations = new FakeAtomicFileOperations(new[] { true }, Array.Empty<Exception>());
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.ThrowsExactly<ArgumentException>(
                () => PresetAtomicFilePublisher.Publish(temporary, destination, operations));
            Assert(operations.ReplaceCalls == 0 && operations.MoveCalls == 0 && operations.Delays.Count == 0,
                "Invalid paths must not modify files or start retries.");
        }

        [TestMethod]
        public void RejectsMissingFileOperations()
        {
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.ThrowsExactly<ArgumentNullException>(
                () => PresetAtomicFilePublisher.Publish("temporary", "destination", null));
        }

        [TestMethod]
        public void PublishesRealFileAndPreservesDestinationOnFailure()
        {
            string realDirectory = Path.Combine(
                Path.GetTempPath(),
                "SerpPresetAtomicPublisher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(realDirectory);
            try
            {
                string temporaryPath = Path.Combine(realDirectory, "preset.tmp");
                string destinationPath = Path.Combine(realDirectory, "preset.json");
                File.WriteAllText(temporaryPath, "new");
                File.WriteAllText(destinationPath, "old");
                PresetAtomicPublishResult realResult =
                    PresetAtomicFilePublisher.Publish(temporaryPath, destinationPath);
                Assert(realResult.Succeeded &&
                        File.ReadAllText(destinationPath) == "new" &&
                        !File.Exists(temporaryPath) &&
                        Directory.GetFiles(realDirectory, "*.replace-backup-*").Length == 0,
                    "Atomic preset publishing did not replace a real file and consume its temporary source.");

                string missingSourcePath = Path.Combine(realDirectory, "missing.tmp");
                File.WriteAllText(destinationPath, "preserved");
                bool failedWithIoException = false;
                try
                {
                    AtomicFileReplacement.Replace(missingSourcePath, destinationPath);
                }
                catch (IOException exception)
                {
                    failedWithIoException = exception.InnerException is System.ComponentModel.Win32Exception;
                }
                Assert(failedWithIoException && File.ReadAllText(destinationPath) == "preserved",
                    "Failed atomic replacement did not preserve the destination and expose an IOException.");
            }
            finally
            {
                Directory.Delete(realDirectory, true);
            }

        }

        [TestMethod]
        public void RetriesTransientIoFailures()
        {
            var retries = new FakeAtomicFileOperations(
                new[] { true, true, true },
                new Exception[]
                {
                    new IOException("first transient failure"),
                    new IOException("second transient failure")
                });
            PresetAtomicPublishResult retryResult = PresetAtomicFilePublisher.Publish(
                "temporary",
                "destination",
                retries);
            Assert(retryResult.Succeeded && retryResult.Attempts == 3 &&
                    retries.ReplaceCalls == 3 && retries.Delays.SequenceEqual(new[] { 15, 35 }),
                "Atomic preset publishing did not retry transient replace failures as specified.");

        }

        [TestMethod]
        public void StopsAfterBoundedRetryBudget()
        {
            var exhausted = new FakeAtomicFileOperations(
                new[] { true, true, true, true },
                new Exception[]
                {
                    new IOException("failure 1"),
                    new IOException("failure 2"),
                    new IOException("failure 3"),
                    new IOException("failure 4")
                });
            PresetAtomicPublishResult exhaustedResult = PresetAtomicFilePublisher.Publish(
                "temporary",
                "destination",
                exhausted);
            Assert(!exhaustedResult.Succeeded && exhaustedResult.Attempts == 4 &&
                    exhausted.Delays.SequenceEqual(new[] { 15, 35, 75 }) &&
                    exhaustedResult.Error != null,
                "Atomic preset publishing did not stop after its bounded retry budget.");

        }

        [TestMethod]
        public void RechecksDestinationBetweenAttempts()
        {
            var destinationDisappeared = new FakeAtomicFileOperations(
                new[] { true, false },
                new Exception[] { new IOException("destination disappeared") });
            PresetAtomicPublishResult moveResult = PresetAtomicFilePublisher.Publish(
                "temporary",
                "destination",
                destinationDisappeared);
            Assert(moveResult.Succeeded && moveResult.Attempts == 2 &&
                    destinationDisappeared.ReplaceCalls == 1 &&
                    destinationDisappeared.MoveCalls == 1,
                "Atomic preset publishing did not re-evaluate destination existence between attempts.");

        }

        [TestMethod]
        public void DoesNotRetryNonIoFailures()
        {
            var nonIoFailure = new FakeAtomicFileOperations(
                new[] { true },
                new Exception[] { new UnauthorizedAccessException("permanent") });
            bool nonIoRethrown = false;
            try
            {
                PresetAtomicFilePublisher.Publish("temporary", "destination", nonIoFailure);
            }
            catch (UnauthorizedAccessException)
            {
                nonIoRethrown = true;
            }
            Assert(nonIoRethrown && nonIoFailure.ReplaceCalls == 1 && nonIoFailure.Delays.Count == 0,
                "Atomic preset publishing retried a non-IO failure.");
        }

        private sealed class FakeAtomicFileOperations : IPresetAtomicFileOperations
        {
            private readonly Queue<bool> destinationExists;
            private readonly Queue<Exception> failures;

            public FakeAtomicFileOperations(
                IEnumerable<bool> destinationExists,
                IEnumerable<Exception> failures)
            {
                this.destinationExists = new Queue<bool>(destinationExists);
                this.failures = new Queue<Exception>(failures);
            }

            public int ReplaceCalls { get; private set; }
            public int MoveCalls { get; private set; }
            public List<int> Delays { get; } = new List<int>();

            public bool Exists(string path)
            {
                return destinationExists.Count > 0 && destinationExists.Dequeue();
            }

            public void Replace(string sourcePath, string destinationPath)
            {
                ReplaceCalls++;
                ThrowNextFailure();
            }

            public void Move(string sourcePath, string destinationPath)
            {
                MoveCalls++;
                ThrowNextFailure();
            }

            public void Delay(int milliseconds)
            {
                Delays.Add(milliseconds);
            }

            private void ThrowNextFailure()
            {
                if (failures.Count > 0)
                    throw failures.Dequeue();
            }
        }

        private static void Assert(bool condition, string message) =>
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(condition, message);
    }
}
