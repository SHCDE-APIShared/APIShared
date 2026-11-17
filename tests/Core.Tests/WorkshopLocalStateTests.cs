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

            // Exactly the catalog's shape for a receipt whose subscription ended: no receipt object.
            var unsubscribed = new WorkshopCatalogItem { IsSubscribed = false, InstalledVersion = "1.0.32" };
            Assert.AreEqual(WorkshopAction.Remove, WorkshopLocalState.StartupAction(unsubscribed, WorkshopLocalStatus.Missing, false));
        } finally {
            Directory.Delete(game, recursive: true);
        }
    }

    [TestMethod]
    public void RequestedInstallationIsOfferedAtStartupLikeTheExplicitChoice()
    {
        var item = new WorkshopCatalogItem {
            IsSubscribed = true, CanInstall = true,
            Package = new WorkshopPackage { Version = "1.0.35", Files = Array.Empty<WorkshopFile>() }
        };
        // Not installed: no automatic offer, but a request installs it.
        Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Missing, true));
        Assert.AreEqual(WorkshopAction.Install, WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Missing, false, requested: true));
        // Modified installation with disabled offers: the request still applies the explicit action.
        item.Receipt = new WorkshopReceipt { Version = "1.0.32", Files = Array.Empty<WorkshopFile>() };
        Assert.AreEqual(WorkshopAction.Update, WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Modified, false, requested: true));
        item.Receipt.Version = "1.0.35";
        Assert.AreEqual(WorkshopAction.Reinstall, WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Verified, false, requested: true));
        // A package that cannot be installed now keeps waiting.
        item.CanInstall = false;
        Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Verified, true, requested: true));
        item.CanInstall = true; item.Package = null;
        Assert.IsNull(WorkshopLocalState.StartupAction(item, WorkshopLocalStatus.Verified, true, requested: true));
    }

    [TestMethod]
    public void InstallationRequestsSurviveMalformedEntriesAndResolveOnlyByCommit()
    {
        const string transaction = "0123456789abcdef0123456789abcdef";
        var requests = WorkshopLocalState.ParseRequests(" 42 ,7:" + transaction + ",0,abc,-1,9:XYZ,8:" + transaction + ":x,42:" + transaction + ",");
        CollectionAssert.AreEquivalent(new ulong[] { 7, 42 }, requests.Keys.ToArray());
        Assert.AreEqual(transaction, requests[7]);
        Assert.IsNull(requests[42], "A pending entry is not replaced by a later handed-off duplicate.");
        Assert.AreEqual("7:" + transaction + ",42", WorkshopLocalState.FormatRequests(requests));
        Assert.AreEqual(0, WorkshopLocalState.ParseRequests(null).Count);
        Assert.AreEqual(WorkshopLocalState.RequestOutcome.Fulfilled, WorkshopLocalState.ResolveRequest(new[] { "applying", "begin\t0", "done\t0", "committed" }));
        foreach (var journal in new[] { null, new string[0], new[] { "applying", "failure x", "restored" }, new[] { "failure Handoff cancelled" }, new[] { "applying", "begin\t0" } })
            Assert.AreEqual(WorkshopLocalState.RequestOutcome.Offer, WorkshopLocalState.ResolveRequest(journal));
        // A committed complete repair reinstalled the item although the original never commits.
        Assert.AreEqual(WorkshopLocalState.RequestOutcome.Fulfilled, WorkshopLocalState.ResolveRequest(new[] { "applying", "begin\t0" }, superseded: true));
        CollectionAssert.AreEquivalent(new ulong[] { 3, 5 }, WorkshopLocalState.ParseIds("5, 3,x,0,5").ToArray());
        Assert.AreEqual("3,5", WorkshopLocalState.FormatIds(new ulong[] { 5, 3, 5 }));
    }
}
