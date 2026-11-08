using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WorkshopLocalStateTests
{
    [TestMethod]
    public void OnlyIntactReceiptedPackagesCanOfferAnUpdateAtStartup()
    {
        string game = Path.Combine(Path.GetTempPath(), "WorkshopLocalState-" + Guid.NewGuid().ToString("N"));
        const string relative = "BepInEx/plugins/Example/Mods/Child/Child.dll";
        string file = Path.Combine(game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try {
            File.WriteAllText(file, "release");
            var delivery = new WorkshopFile(relative, new FileInfo(file).Length, WorkshopInventory.Hash(file));
            var item = new WorkshopCatalogItem {
                IsSubscribed = true, CanInstall = true, InstalledVersion = "1.0.32",
                Package = new WorkshopPackage { Version = "1.0.35", Files = new[] { delivery } },
                Receipt = new WorkshopReceipt { Version = "1.0.32", Files = new[] { delivery } }
            };
            string preference = Path.Combine(game, "BepInEx/plugins/Example/LobbyModSettings/preferences.json");
            Directory.CreateDirectory(Path.GetDirectoryName(preference)!);
            File.WriteAllText(preference, "local");
            Assert.AreEqual(WorkshopLocalStatus.Verified, WorkshopLocalState.Inspect(game, item).Status);
            Assert.AreEqual(WorkshopAction.Update, WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(game, item).Status, true));
            Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(game, item).Status, false));

            File.WriteAllText(file, "develop"); // Same length as release.
            Assert.AreEqual(WorkshopLocalStatus.Modified, WorkshopLocalState.Inspect(game, item).Status);
            Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(game, item).Status, true));
            Assert.AreEqual(WorkshopAction.Update, WorkshopLocalState.ManualAction(item));
            Assert.AreEqual("develop", File.ReadAllText(file));

            File.WriteAllText(file, "older package");
            Assert.AreEqual(WorkshopLocalStatus.Modified, WorkshopLocalState.Inspect(game, item).Status);
            Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(game, item).Status, true));

            File.Delete(file);
            Assert.AreEqual(WorkshopLocalStatus.Missing, WorkshopLocalState.Inspect(game, item).Status);
            Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(game, item).Status, true));
            Assert.AreEqual("local", File.ReadAllText(preference));

            item.Package.Version = item.Receipt.Version;
            Assert.AreEqual(WorkshopAction.Reinstall, WorkshopLocalState.ManualAction(item));
            item.Package.Version = "1.0.30";
            Assert.AreEqual(WorkshopAction.Downgrade, WorkshopLocalState.ManualAction(item));
            item.Receipt = null;
            Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(game, item).Status, true));
            Assert.AreEqual(WorkshopAction.Install, WorkshopLocalState.ManualAction(item));
        } finally {
            Directory.Delete(game, recursive: true);
        }
    }
}
