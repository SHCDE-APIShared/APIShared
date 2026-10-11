using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests
{
    [TestClass]
    public class WorkshopMigrationTests
    {
        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void BothInstallationOrdersConsentAndInterruptedDeploymentRemainSafe(bool apiFirst)
        {
            string game = Path.Combine(Path.GetTempPath(), "ApiWorkshopMigration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(game);
            try
            {
                string pack = Path.Combine(game, "BepInEx", "plugins", "Pack"), api = Path.Combine(game, "BepInEx", "plugins", "APIShared_Serp");
                string oldDirectory = Path.Combine(pack, "Infrastructure", "APIShared_Serp");
                Directory.CreateDirectory(oldDirectory); Directory.CreateDirectory(api);
                string obsolete = Path.Combine(oldDirectory, "APIShared.dll"), current = Path.Combine(api, "APIShared.dll");
                File.WriteAllText(obsolete, "verified old delivery");
                string oldHash = WorkshopInventory.Hash(obsolete);
                string settings = Path.Combine(pack, "LobbyModSettings", "preset.json");
                Directory.CreateDirectory(Path.GetDirectoryName(settings)); File.WriteAllText(settings, "player settings");
                var next = new WorkshopInventory.Inventory();
                next.Files.Add("Host.dll", new string('a', 64));
                next.Legacy.Add("Infrastructure/APIShared_Serp/APIShared.dll", new HashSet<string> { oldHash });
                if (apiFirst) File.WriteAllText(current, "separate API");
                File.WriteAllText(Path.Combine(pack, "Host.dll"), "new host");
                if (!apiFirst) File.WriteAllText(current, "separate API");
                string se = Path.Combine(game, "_SE"), staging = Path.Combine(se, ".staging"), stagedPack = Path.Combine(staging, "BepInEx", "plugins", "Pack");
                string[] victims = WorkshopInventory.Plan(null, next, pack, stagedPack, _ => { }).ToArray();
                Assert.AreEqual(1, victims.Length);
                WorkshopDeletionManifest.Append(se, victims);
                Assert.IsTrue(File.Exists(obsolete), "Staging must not delete live files.");
                // SE confirmation declined: it discards staging and leaves installed files.
                Directory.Delete(staging, true);
                Assert.IsTrue(File.Exists(obsolete));
                WorkshopDeletionManifest.Append(se, WorkshopInventory.Plan(null, next, pack, stagedPack, _ => { }));
                // A locked manifest must not truncate pending changes.
                string manifest = Path.Combine(staging, "delete_list.txt"), original = File.ReadAllText(manifest);
                using (var locked = File.Open(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.ThrowsExactly<IOException>(() => WorkshopDeletionManifest.Append(se, new[] { "other pending removal" }));
                Assert.AreEqual(original, File.ReadAllText(manifest));
                Assert.AreEqual(0, Directory.GetFiles(se, "*.tmp").Length);
                WorkshopDeletionManifest.Append(se, victims); // interrupted deployment: reevaluation is idempotent
                Assert.AreEqual(1, File.ReadAllLines(manifest).Length);
                // Apply the updater's confirmed delete step inside this isolated fixture only.
                foreach (string target in File.ReadAllLines(manifest))
                {
                    Assert.IsTrue(Path.GetFullPath(target).StartsWith(Path.GetFullPath(pack) + Path.DirectorySeparatorChar));
                    File.Delete(target);
                }
                Assert.AreEqual(0, WorkshopInventory.Plan(null, next, pack, stagedPack, _ => { }).Count());
                Assert.AreEqual(1, Directory.GetFiles(Path.Combine(game, "BepInEx", "plugins"), "APIShared.dll", SearchOption.AllDirectories).Length);
                Assert.AreEqual("separate API", File.ReadAllText(current));
                Assert.AreEqual("player settings", File.ReadAllText(settings));
            }
            finally { Directory.Delete(game, true); }
        }
    }
}
