using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WorkshopDiagnosticsTests
{
    [TestMethod]
    public void ReportsEveryPluginAndAssetConflictAndKeepsExceptionType()
    {
        string game = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WorkshopDiagnostics"));
        string root = "BepInEx/plugins/Pack";
        var conflicts = new List<WorkshopOwnershipConflict> {
            WorkshopDiagnostics.Conflict(game, new[] { root }, "First", Path.Combine(game, "BepInEx/plugins/First/First.dll"), false),
            WorkshopDiagnostics.Conflict(game, new[] { root }, "Second", Path.Combine(game, "BepInEx/plugins/Second"), true)
        };
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopDiagnostics.RejectConflicts(conflicts));
        Assert.AreEqual("An installed plugin outside this payload already owns GUID: First", exception.Message);
        var logs = new List<(WorkshopDiagnosticLevel Level, string Text)>();
        string context = WorkshopDiagnostics.Context(42, "Pack", "1.0.0", "1.0.1", "prepare Update");
        WorkshopDiagnostics.Failure((level, text) => logs.Add((level, text)), context, exception);
        Assert.HasCount(2, logs);
        foreach (var log in logs) {
            Assert.AreEqual(WorkshopDiagnosticLevel.Warning, log.Level);
            StringAssert.Contains(log.Text, "item=42"); StringAssert.Contains(log.Text, "installed=1.0.0");
            StringAssert.Contains(log.Text, "available=1.0.1"); StringAssert.Contains(log.Text, "existingPath=");
            StringAssert.Contains(log.Text, "allowedRoots=" + Path.GetFullPath(Path.Combine(game, "BepInEx/plugins/Pack")));
        }
        // Ownership depends on location, including when both versions are equal.
        Assert.IsNotNull(WorkshopDiagnostics.Conflict(game, new[] { root }, "First", conflicts[0].Path, false));
        Assert.IsNull(WorkshopDiagnostics.Conflict(game, new[] { root }, "First", Path.Combine(game, root, "Mods/First.dll"), false));
        Assert.IsNull(WorkshopDiagnostics.Conflict(game, new[] { root }, "Pack", Path.Combine(game, root), true));
        Assert.IsNull(WorkshopDiagnostics.Conflict(game, new[] { root }, "First", Path.Combine(game, root, "Mods/First"), true));
        Assert.IsNotNull(WorkshopDiagnostics.Conflict(game, new[] { root }, "Sibling", Path.Combine(game, root + "Other", "Other.dll"), false));
    }

    [TestMethod]
    public void CatalogFailuresCannotBeReportedAsCurrentAndValidItemsStayQuiet()
    {
        var logs = new List<(WorkshopDiagnosticLevel Level, string Text)>();
        void Write(WorkshopDiagnosticLevel level, string text) => logs.Add((level, text));
        var item = new WorkshopCatalogItem { WorkshopId = 42, Title = "Pack", InstalledVersion = "1.0.0", IsSubscribed = true,
            DiagnosticError = new InvalidDataException("Malformed package") };
        Assert.AreEqual(WorkshopDiagnosticLevel.Error, WorkshopDiagnostics.Catalog(Write, item));
        StringAssert.Contains(logs[0].Text, "item=42"); StringAssert.Contains(logs[0].Text, "Malformed package");
        Assert.AreEqual("failed", WorkshopDiagnostics.NoChangesResult(false, 1));
        item.DiagnosticError = null; item.Status = "Download or installation information unavailable";
        Assert.AreEqual(WorkshopDiagnosticLevel.Warning, WorkshopDiagnostics.Catalog(Write, item));
        StringAssert.Contains(logs[1].Text, item.Status);
        Assert.AreEqual("blocked", WorkshopDiagnostics.NoChangesResult(true, 0));
        item.Package = new WorkshopPackage { Version = "1.0.0" }; item.CanInstall = true;
        Assert.IsNull(WorkshopDiagnostics.Catalog(Write, item));
        Assert.HasCount(2, logs);
        Assert.AreEqual("current", WorkshopDiagnostics.NoChangesResult(false, 0));
        var receiptError = new WorkshopCatalogItem { Title = "Installation state", DiagnosticError = new IOException("Unreadable receipt") };
        Assert.AreEqual(WorkshopDiagnosticLevel.Error, WorkshopDiagnostics.Catalog(Write, receiptError));
        StringAssert.Contains(logs[2].Text, "Unreadable receipt");
    }

    [TestMethod]
    public void DistinguishesPolicyBlocksFromTechnicalFailuresAndOutcomes()
    {
        var logs = new List<(WorkshopDiagnosticLevel Level, string Text)>();
        void Write(WorkshopDiagnosticLevel level, string text) => logs.Add((level, text));
        WorkshopDiagnostics.Failure(Write, "step=dependencies", WorkshopDiagnostics.Blocked("Missing dependency: Other"));
        WorkshopDiagnostics.Failure(Write, "step=catalog; item=42", new InvalidDataException("Invalid manifest"));
        Assert.AreEqual(WorkshopDiagnosticLevel.Warning, logs[0].Level);
        Assert.AreEqual(WorkshopDiagnosticLevel.Error, logs[1].Level);
        StringAssert.Contains(logs[1].Text, "InvalidDataException");
        WorkshopDiagnostics.Outcome(Write, "current", false, 0);
        WorkshopDiagnostics.Outcome(Write, "blocked", true, 0);
        WorkshopDiagnostics.Outcome(Write, "blocked", false, 1);
        WorkshopDiagnostics.Outcome(Write, "declined", false, 0);
        WorkshopDiagnostics.Outcome(Write, "awaiting restart", false, 0);
        Assert.AreEqual(WorkshopDiagnosticLevel.Info, logs[2].Level);
        Assert.AreEqual(WorkshopDiagnosticLevel.Warning, logs[3].Level);
        Assert.AreEqual(WorkshopDiagnosticLevel.Error, logs[4].Level);
        Assert.AreEqual(WorkshopDiagnosticLevel.Info, logs[5].Level);
        Assert.AreEqual(WorkshopDiagnosticLevel.Info, logs[6].Level);
        StringAssert.Contains(logs[4].Text, "technicalErrors=1");
    }
}
