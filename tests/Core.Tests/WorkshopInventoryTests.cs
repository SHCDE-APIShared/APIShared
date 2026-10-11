using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests
{
    [TestClass]
    public class WorkshopInventoryTests
    {
        [TestMethod]
        public void UpdateAndFirstMigrationPreserveMutableUnknownAndModifiedFiles()
        {
            string root = Path.Combine(Path.GetTempPath(), "ApiWorkshop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string installed = Path.Combine(root, "installed"), staged = Path.Combine(root, "staged");
                Directory.CreateDirectory(installed); Directory.CreateDirectory(staged);
                File.WriteAllText(Path.Combine(installed, "removed.dll"), "old");
                File.WriteAllText(Path.Combine(installed, "moved.xaml"), "moved");
                File.WriteAllText(Path.Combine(installed, "modified.dll"), "player modification");
                File.WriteAllText(Path.Combine(installed, "unknown.txt"), "unknown");
                File.WriteAllText(Path.Combine(installed, "keep.dll"), "old");
                string oldHash = WorkshopInventory.Hash(Path.Combine(installed, "removed.dll"));
                string movedHash = WorkshopInventory.Hash(Path.Combine(installed, "moved.xaml"));
                var old = new WorkshopInventory.Inventory();
                old.Files.Add("removed.dll", oldHash); old.Files.Add("modified.dll", oldHash);
                old.Files.Add("keep.dll", oldHash); old.Files.Add("moved.xaml", movedHash);
                var next = new WorkshopInventory.Inventory();
                next.Files.Add("keep.dll", oldHash); next.Files.Add("new/path.xaml", movedHash);
                var warnings = new List<string>();
                string[] removed = WorkshopInventory.Plan(old, next, installed, staged, warnings.Add).ToArray();
                CollectionAssert.AreEquivalent(new[] { Path.Combine(installed, "removed.dll"), Path.Combine(installed, "moved.xaml") }, removed);
                Assert.AreEqual(1, warnings.Count);
                next.Legacy.Add("removed.dll", new HashSet<string> { oldHash });
                Assert.AreEqual(1, WorkshopInventory.Plan(null, next, installed, staged, warnings.Add).Count());
                // Concurrent deployment has a new owner for this exact target.
                File.WriteAllText(Path.Combine(staged, "removed.dll"), "other package");
                Assert.AreEqual(0, WorkshopInventory.Plan(null, next, installed, staged, warnings.Add).Count());
                File.Delete(Path.Combine(staged, "removed.dll"));
                File.Delete(Path.Combine(installed, "removed.dll"));
                Assert.AreEqual(0, WorkshopInventory.Plan(null, next, installed, staged, warnings.Add).Count());
                Assert.AreEqual("player modification", File.ReadAllText(Path.Combine(installed, "modified.dll")));
                Assert.IsTrue(File.Exists(Path.Combine(installed, "unknown.txt")));
            }
            finally { Directory.Delete(root, true); }
        }

        [DataTestMethod]
        [DataRow("../escape.dll")]
        [DataRow("/absolute.dll")]
        [DataRow("folder\\escape.dll")]
        [DataRow("file.dll:stream")]
        [DataRow("folder/./file.dll")]
        [DataRow("config/settings.json")]
        [DataRow("LobbyModSettings/preset.json")]
        [DataRow("CON.txt")]
        [DataRow("asset[1].xaml")]
        [DataRow("folder /file.dll")]
        [DataRow("apishared-workshop-files.json")]
        public void RejectsUnsafeAndMutablePaths(string path) => Assert.ThrowsExactly<InvalidDataException>(() => WorkshopInventory.CheckPath(path));

        [TestMethod]
        public void RejectsIdentityVersionDuplicateAndHashErrors()
        {
            string row = "{\"Path\":\"API.dll\",\"Sha256\":\"" + new string('a', 64) + "\"}";
            string json = "{\"SchemaVersion\":1,\"Guid\":\"Test\",\"Version\":\"1.0.0\",\"Files\":[" + row + "]}";
            Assert.AreEqual(1, WorkshopInventory.Read(json, "Test", "1.0.0").Files.Count);
            Assert.ThrowsExactly<InvalidDataException>(() => WorkshopInventory.Read(json, "Other", "1.0.0"));
            Assert.ThrowsExactly<InvalidDataException>(() => WorkshopInventory.Read(json, "Test", "2.0.0"));
            Assert.ThrowsExactly<InvalidDataException>(() => WorkshopInventory.Read(json.Replace(row, row + "," + row), "Test", "1.0.0"));
            Assert.ThrowsExactly<InvalidDataException>(() => WorkshopInventory.CheckHash("123"));
            foreach (string schema in new[] { "\"1\"", "1.1", "null", "2" })
                Assert.ThrowsExactly<InvalidDataException>(() => WorkshopInventory.Read(json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":" + schema), "Test", "1.0.0"));
            foreach (string guid in new[] { "CON", "Pack.", "Pack " })
                Assert.ThrowsExactly<InvalidDataException>(() => WorkshopInventory.CheckGuid(guid));
        }

        [TestMethod]
        public void RejectsLinkedDirectoriesAndPayloadMismatch()
        {
            string root = Path.Combine(Path.GetTempPath(), "ApiWorkshopLinks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var inventory = new WorkshopInventory.Inventory(); inventory.Files.Add("absent.dll", new string('a', 64));
                Assert.ThrowsExactly<InvalidDataException>(() => WorkshopInventory.VerifyFiles(inventory, root));
                Assert.IsFalse(WorkshopInventory.IsContainedWithoutLinks(root, Path.Combine(root, "..", "escape.dll")));
                string outside = Path.Combine(root, "outside"), link = Path.Combine(root, "linked");
                Directory.CreateDirectory(outside);
                // NTFS junctions require no symlink privilege on Windows.
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + outside + "\"") { CreateNoWindow = true, UseShellExecute = false });
                process.WaitForExit(); Assert.AreEqual(0, process.ExitCode);
                try { Assert.IsFalse(WorkshopInventory.IsContainedWithoutLinks(root, Path.Combine(link, "old.dll"))); }
                finally { Directory.Delete(link); }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
