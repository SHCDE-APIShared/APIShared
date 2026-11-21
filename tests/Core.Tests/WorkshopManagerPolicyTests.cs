using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

// Contract of BepInEx/config/APIShared.ModManager.json (docs/WORKSHOP.md, "External mod managers"):
// listed items are left alone, and a file that is not understood never lets APIShared act.
[TestClass]
public sealed class WorkshopManagerPolicyTests
{
    [TestMethod] public void ListedItemsAndWildcardAreManaged()
    {
        var policy = WorkshopManagerPolicy.Parse("{ \"SchemaVersion\": 1, \"Manager\": \"Example Manager\", \"Items\": { \"3788821961\": \"managed\" } }");
        Assert.IsNull(policy.Error);
        Assert.AreEqual("Example Manager", policy.Manager);
        Assert.IsTrue(policy.Manages(3788821961));
        Assert.IsFalse(policy.Manages(3818609220));
        Assert.IsTrue(WorkshopManagerPolicy.Parse("{ \"SchemaVersion\": 1, \"Items\": { \"*\": \"Managed\" } }").Manages(1));
        Assert.IsFalse(WorkshopManagerPolicy.Parse("{ \"SchemaVersion\": 1 }").Manages(1), "no items: nothing taken over");
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("[1]")]
    [DataRow("{ \"SchemaVersion\": 2, \"Items\": {} }")]
    [DataRow("{ \"Items\": {} }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Items\": [\"3788821961\"] }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Items\": { \"abc\": \"managed\" } }")]
    public void UnderstoodNothingMeansEverythingIsManaged(string text)
    {
        var policy = WorkshopManagerPolicy.Parse(text);
        Assert.IsNotNull(policy.Error);
        Assert.IsTrue(policy.Manages(1) && policy.Manages(3788821961));
    }

    [TestMethod] public void UnknownValueStillKeepsApiSharedAway()
    {
        var policy = WorkshopManagerPolicy.Parse("{ \"SchemaVersion\": 1, \"Items\": { \"5\": \"auto\" } }");
        Assert.IsNotNull(policy.Error);
        Assert.IsTrue(policy.Manages(5));
        Assert.IsFalse(policy.Manages(6));
    }

    [TestMethod] public void MissingFileManagesNothingAndManagedItemsAreNeverOffered()
    {
        string game = Path.Combine(Path.GetTempPath(), "ApiManagerPolicy-" + Guid.NewGuid().ToString("N"));
        try {
            Assert.AreSame(WorkshopManagerPolicy.None, WorkshopManagerPolicy.Read(game));
            Directory.CreateDirectory(Path.GetDirectoryName(WorkshopManagerPolicy.PathFor(game))!);
            File.WriteAllText(WorkshopManagerPolicy.PathFor(game), "{ \"SchemaVersion\": 1, \"Manager\": \"M\", \"Items\": { \"7\": \"managed\" } }");
            Assert.IsTrue(WorkshopManagerPolicy.Read(game).Manages(7));
        } finally { if (Directory.Exists(game)) Directory.Delete(game, true); }

        var subscribed = new WorkshopCatalogItem { IsSubscribed = true, CanInstall = true, ManagedBy = "M",
            Package = new WorkshopPackage { Version = "1.0.1", Files = Array.Empty<WorkshopFile>() } };
        Assert.IsNull(WorkshopLocalState.StartupAction(subscribed, WorkshopLocalStatus.Missing, true, repair: true));
        var unsubscribed = new WorkshopCatalogItem { IsSubscribed = false, InstalledVersion = "1.0.0", ManagedBy = "M" };
        Assert.IsNull(WorkshopLocalState.StartupAction(unsubscribed, WorkshopLocalStatus.Missing, true), "never removed for the manager");

        // A deliberately managed package is reported as information, not as a startup blocker.
        var levels = new List<WorkshopDiagnosticLevel>();
        WorkshopDiagnostics.ReportStartupCatalog((level, _) => levels.Add(level), new[] { subscribed }, out bool blocked, out int failures);
        Assert.IsFalse(blocked); Assert.AreEqual(0, failures);
        CollectionAssert.AreEqual(new[] { WorkshopDiagnosticLevel.Info }, levels);
    }
}
