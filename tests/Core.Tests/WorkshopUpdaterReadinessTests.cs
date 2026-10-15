using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

[TestClass]
public sealed class WorkshopUpdaterReadinessTests
{
    private static readonly string Hash = new string('a', 64);

    [TestMethod]
    public void OpenUpdaterWriterDoesNotBreakTheHandoffAndPartialHashIsNotReady()
    {
        string directory = NewDirectory(), path = Path.Combine(directory, "ready.flag");
        try {
            using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) {
                byte[] bytes = Encoding.UTF8.GetBytes(Hash);
                writer.Write(bytes, 0, 16); writer.Flush();
                Assert.IsFalse(WorkshopUpdaterReadiness.TryConfirm(path, Hash, out var partialFailure));
                Assert.IsNull(partialFailure);
                writer.Write(bytes, 16, bytes.Length - 16); writer.Flush();
                Assert.IsTrue(WorkshopUpdaterReadiness.TryConfirm(path, Hash, out var completeFailure));
                Assert.IsNull(completeFailure);
            }
        } finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void ExclusiveLockIsUnconfirmedAndTheSameFileBecomesReadyAfterRelease()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows sharing semantics are required."); return; }
        string directory = NewDirectory(), path = Path.Combine(directory, "ready.flag");
        try {
            File.WriteAllText(path, Hash);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                Assert.IsFalse(WorkshopUpdaterReadiness.TryConfirm(path, Hash, out var failure));
                Assert.IsNotNull(failure);
            }
            Assert.IsTrue(WorkshopUpdaterReadiness.TryConfirm(path, Hash, out var releasedFailure));
            Assert.IsNull(releasedFailure);
        } finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void MissingEmptyWrongAndTruncatedReadinessNeverConfirm()
    {
        string directory = NewDirectory(), path = Path.Combine(directory, "ready.flag");
        try {
            Assert.IsFalse(WorkshopUpdaterReadiness.TryConfirm(path, Hash, out _));
            foreach (string text in new[] { "", Hash.Substring(0, 63), new string('b', 64), Hash + "\n" }) {
                File.WriteAllText(path, text);
                Assert.IsFalse(WorkshopUpdaterReadiness.TryConfirm(path, Hash, out _), text);
            }
            File.WriteAllText(path, Hash);
            Assert.IsTrue(WorkshopUpdaterReadiness.TryConfirm(path, Hash, out _));
        } finally { Directory.Delete(directory, true); }
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "APIShared-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
