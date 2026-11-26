using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WorkshopLocalStateTests
{
    [TestMethod]
    public void StartupRepairsNeverReplaceANewerPackageOnDiskOrInTheReceipt()
    {
        string game = Path.Combine(Path.GetTempPath(), "WorkshopLocalVersion-" + Guid.NewGuid().ToString("N"));
        const string root = "BepInEx/plugins/SerpsMods_Serp";
        const string relative = root + "/Locales/ar.txt";
        string file = Path.Combine(game, relative);
        void WriteHost(string path, string guid, string version)
        {
            string fullPath = Path.Combine(game, path, "info.json");
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, WorkshopPackageCodec.Write(new { GUID = guid, Version = version }));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try {
            File.WriteAllText(file, "release 1.0.38");
            var previous = new WorkshopReceipt {
                Version = "1.0.38", Files = new[] { new WorkshopFile(relative, new FileInfo(file).Length, WorkshopInventory.Hash(file)) }
            };
            var package = new WorkshopPackage { PackageId = "SerpsMods_Serp", Version = "1.0.38", Roots = new[] { root } };
            var item = new WorkshopCatalogItem { IsSubscribed = true, CanInstall = true, Package = package, Receipt = previous };
            WriteHost(root, package.PackageId, "1.0.39");
            WriteHost(root + "/Mods/Example", "Example", "99.0.0");
            File.WriteAllText(file, "release 1.0.39");
            Assert.AreEqual(WorkshopLocalStatus.Modified, WorkshopLocalState.Inspect(game, item).Status);

            string diagnostic = null!;
            WorkshopAction? Offer(bool enabled = true, bool repair = false) => WorkshopLocalState.StartupAction(
                item, WorkshopLocalStatus.Modified, enabled, repair,
                WorkshopLocalState.ReadInstalledPackageVersion(game, package), message => diagnostic = message);

            Assert.IsNull(Offer());
            StringAssert.Contains(diagnostic, "local=1.0.39");
            StringAssert.Contains(diagnostic, "receipt=1.0.38");
            StringAssert.Contains(diagnostic, "available=1.0.38");
            Assert.IsNull(Offer(enabled: false, repair: true));
            item.Receipt = null;
            Assert.IsNull(Offer(), "first-install offers also protect a newer manual install");
            item.Receipt = previous;

            package.Version = "1.0.40";
            Assert.AreEqual(WorkshopAction.Update, Offer(), "a nested child with a higher version is ignored");
            package.Version = "1.0.38";
            WriteHost(root, package.PackageId, "1.0.38");
            Assert.AreEqual(WorkshopAction.Reinstall, Offer());
            Assert.AreEqual(WorkshopAction.Reinstall, Offer(enabled: false, repair: true));

            const string otherRoot = "BepInEx/plugins/OtherRoot";
            package.Roots = new[] { root, otherRoot };
            WriteHost(otherRoot, package.PackageId, "1.0.99");
            Assert.IsNull(WorkshopLocalState.ReadInstalledPackageVersion(game, package), "two matching root GUIDs are ambiguous");
            WriteHost(root, package.PackageId, "invalid");
            WriteHost(otherRoot, "OtherMod", "99.0.0");
            Assert.AreEqual(WorkshopAction.Reinstall, Offer(), "an invalid host version falls back to the receipt");
            package.Version = "1.0.37";
            Assert.IsNull(Offer(), "the receipt alone also prevents an automatic downgrade");
            Assert.AreEqual(WorkshopAction.Downgrade, WorkshopLocalState.ManualAction(item));
            item.Receipt = null;
            Assert.AreEqual(WorkshopAction.Install, Offer(), "a package without a known installed version can still be installed");
        } finally {
            Directory.Delete(game, recursive: true);
        }
    }

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
            // A damaged installation is offered as the clean installation of the current release.
            Assert.AreEqual(WorkshopAction.Update, WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(game, item).Status, true));
            Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(game, item).Status, false));
            Assert.AreEqual(WorkshopAction.Update, WorkshopLocalState.ManualAction(item));
            Assert.AreEqual("develop", File.ReadAllText(file));

            File.WriteAllText(file, "older package");
            Assert.AreEqual(WorkshopLocalStatus.Modified, WorkshopLocalState.Inspect(game, item).Status);
            Assert.AreEqual(WorkshopAction.Update, WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(game, item).Status, true));

            File.Delete(file);
            Assert.AreEqual(WorkshopLocalStatus.Missing, WorkshopLocalState.Inspect(game, item).Status);
            Assert.AreEqual(WorkshopAction.Update, WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(game, item).Status, true));
            Assert.AreEqual("local", File.ReadAllText(preference));

            item.Package.Version = item.Receipt.Version;
            Assert.AreEqual(WorkshopAction.Reinstall, WorkshopLocalState.ManualAction(item));
            item.Package.Version = "1.0.30";
            Assert.AreEqual(WorkshopAction.Downgrade, WorkshopLocalState.ManualAction(item));
            item.Receipt = null;
            // Subscribed but not installed by APIShared: first installation offered unless offers are off.
            Assert.AreEqual(WorkshopAction.Install, WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(game, item).Status, true));
            Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalState.Inspect(game, item).Status, false));
            Assert.AreEqual(WorkshopAction.Install, WorkshopLocalState.ManualAction(item));

            // Exactly the catalog's shape for a receipt whose subscription ended: no receipt object.
            var unsubscribed = new WorkshopCatalogItem { IsSubscribed = false, InstalledVersion = "1.0.32" };
            Assert.AreEqual(WorkshopAction.Remove, WorkshopLocalState.StartupAction(unsubscribed, WorkshopLocalStatus.Missing, false));
        } finally {
            Directory.Delete(game, recursive: true);
        }
    }

    [TestMethod]
    public void InstallablePackagesAreOfferedAndIdsParseStrictly()
    {
        var item = new WorkshopCatalogItem {
            IsSubscribed = true, CanInstall = true,
            Package = new WorkshopPackage { Version = "1.0.35", Files = Array.Empty<WorkshopFile>() }
        };
        // Subscribed, not installed by APIShared yet: the first installation is offered (offers on).
        Assert.AreEqual(WorkshopAction.Install, WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Missing, true));
        Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Missing, false));
        item.Receipt = new WorkshopReceipt { Version = "1.0.35", Files = Array.Empty<WorkshopFile>() };
        Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Verified, true), "current and intact");
        Assert.AreEqual(WorkshopAction.Reinstall, WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Modified, true), "damaged: clean reinstallation");
        item.CanInstall = false;
        Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Modified, true));
        item.CanInstall = true; item.Package = null;
        Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Modified, true));
        CollectionAssert.AreEquivalent(new ulong[] { 3, 5 }, WorkshopLocalState.ParseIds("5, 3,x,0,5").ToArray());
        Assert.AreEqual("3,5", WorkshopLocalState.FormatIds(new ulong[] { 5, 3, 5 }));
    }

    // The former updater interrupted while installing (here: a first installation, no receipt
    // yet): the item is offered as a clean installation until its files match the package,
    // even with offers disabled. Unreadable transactions keep their recovery evidence.
    [TestMethod]
    public void InterruptedFormerTransactionsAreOfferedAsCleanInstallation()
    {
        string game = Path.Combine(Path.GetTempPath(), "ApiRepair-" + Guid.NewGuid().ToString("N"));
        try {
            string Transaction(string name, string journal) {
                string dir = Path.Combine(game, "_APIShared", "T", name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "journal.log"), journal);
                File.WriteAllText(Path.Combine(dir, "operations.tsv"), "write\tBepInEx/plugins/Pack/a.dll\t0\tmissing\tabc\nreceipt-write\t_APIShared/Workshop/Receipts/3788821961.json\t1\tmissing\tdef\n");
                return dir;
            }
            CollectionAssert.AreEqual(new ulong[] { 3788821961 }, WorkshopLocalState.ReadFormerTransaction(Transaction("a", "prepared\napplying\nwrite 0\n")).ToArray());
            Assert.HasCount(0, WorkshopLocalState.ReadFormerTransaction(Transaction("b", "prepared\napplying\ncommitted\n")));
            Assert.HasCount(0, WorkshopLocalState.ReadFormerTransaction(Transaction("c", "prepared\n")));
            Assert.IsNull(WorkshopLocalState.ReadFormerTransaction(Path.Combine(game, "missing")));
            string incomplete = Transaction("d", "prepared\napplying\n");
            File.Delete(Path.Combine(incomplete, "operations.tsv"));
            Assert.IsNull(WorkshopLocalState.ReadFormerTransaction(incomplete));

            WorkshopLocalState.WriteRepairs(game, new HashSet<ulong> { 3788821961 });
            CollectionAssert.AreEquivalent(new ulong[] { 3788821961 }, WorkshopLocalState.ReadRepairs(game).ToArray());
            WorkshopLocalState.WriteRepairs(game, new HashSet<ulong>());
            Assert.IsFalse(File.Exists(WorkshopLocalState.RepairPath(game)));

            var item = new WorkshopCatalogItem { IsSubscribed = true, CanInstall = true, Package = new WorkshopPackage { Version = "1.0.38", Files = Array.Empty<WorkshopFile>() } };
            Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Modified, false), "without repair: receipt-less items are not offered");
            Assert.AreEqual(WorkshopAction.Install, WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Modified, false, repair: true));
            Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Verified, false, repair: true), "files match the package again");
        } finally {
            if (Directory.Exists(game)) Directory.Delete(game, recursive: true);
        }
    }
}
