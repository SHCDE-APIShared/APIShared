using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public sealed class WorkshopStartupTests
{
    [TestMethod]
    public void AssetPresenceDoesNotInventAVersionOrOverwriteLoadedPlugins()
    {
        var state = WorkshopInstalledVersions.Collect(
            new[] { new KeyValuePair<string, string>("Plugin", "1.2.3") },
            new[] { new KeyValuePair<string, string>("PLUGIN", "9.9.9"), new KeyValuePair<string, string>("Lord", "1.0.1-test") });
        Assert.AreEqual(new Version(1, 2, 3, 0), state["plugin"]);
        Assert.IsTrue(state.ContainsKey("Lord")); Assert.IsNull(state["Lord"]);
        var package = new WorkshopPackage { Dependencies = new[] { new WorkshopDependency("Lord", "", "") } };
        WorkshopBatch.Dependencies(package, state);
        package.Dependencies = new[] { new WorkshopDependency("Plugin", "1.0.0", "2.0.0") };
        WorkshopBatch.Dependencies(package, state);
        package.Dependencies = new[] { new WorkshopDependency("Lord", "1.0.0", "") };
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopBatch.Dependencies(package, state));
        StringAssert.Contains(error.Message, "Lord"); StringAssert.Contains(error.Message, "comparable");
        package.Dependencies = new[] { new WorkshopDependency("Lord", "", "2.0.0") };
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopBatch.Dependencies(package, state));
        package.Dependencies = new[] { new WorkshopDependency("Missing", "", "") };
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopBatch.Dependencies(package, state));
        Assert.ThrowsExactly<InvalidDataException>(() => WorkshopPackageCodec.Version("1.0.1-test"));
    }

    [TestMethod]
    public void BatchCanReplaceAnUnknownVersionWithoutBlockingUnrelatedPackages()
    {
        var installed = WorkshopInstalledVersions.Collect(Array.Empty<KeyValuePair<string, string>>(), new[] { new KeyValuePair<string, string>("Lord", "test") });
        var package = new WorkshopPackage { PackageId = "A", Roots = new[] { "BepInEx/plugins/A" }, ModVersions = new Dictionary<string, Version> { ["A"] = new Version(1, 0, 0) }, Dependencies = new[] { new WorkshopDependency("Lord", "", "") } };
        var plan = new WorkshopInstallPlan { PackageId = "A", Package = package };
        var state = WorkshopBatch.EndState(new[] { plan }, installed);
        WorkshopBatch.Dependencies(package, state);
        Assert.IsNull(state["Lord"]); Assert.AreEqual(new Version(1, 0, 0), state["A"]);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PromptDistinguishesBootstrapFromPendingPayload(bool german)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "WorkshopSummary-" + Guid.NewGuid().ToString("N"));
        try {
            string game = Path.Combine(fixture, "game"), source = Path.Combine(fixture, "source"), root = "BepInEx/plugins/Host";
            Directory.CreateDirectory(Path.Combine(source, "Payload", root)); Directory.CreateDirectory(Path.Combine(game, root));
            string target = Path.Combine(game, root, "info.json");
            File.WriteAllText(Path.Combine(source, "Payload", root, "info.json"), "{\"GUID\":\"Host\",\"Name\":\"Serps Mods\",\"Version\":\"1.0.26\"}");
            File.WriteAllText(target, "{\"GUID\":\"Host\",\"Version\":\"1.0.23\"}");
            Directory.CreateDirectory(Path.Combine(game, "_SE"));
            File.WriteAllText(Path.Combine(game, "_SE/Host.json"), "{\"GUID\":\"Bootstrap\",\"Version\":\"1.0.26\"}");
            var plan = new WorkshopInstallPlan { GameRoot = game, Source = source, Action = WorkshopAction.Install, PackageId = "Host", Version = "1.0.26",
                Package = new WorkshopPackage { Roots = new[] { root }, ModVersions = new Dictionary<string, Version> { ["Host"] = new Version(1, 0, 26) }, Bootstrap = new WorkshopBootstrap("Host.map", "Bootstrap", "1.0.26", "Bootstrap") } };
            string summary = WorkshopStartupSummary.Describe(plan, german);
            StringAssert.Contains(summary, "Serps Mods"); StringAssert.Contains(summary, "1.0.23"); StringAssert.Contains(summary, "1.0.26");
            StringAssert.Contains(summary, german ? "noch übernommen" : "still need to be applied");
            Assert.IsNull(WorkshopInstallation.ReadReceipt(game, 123));
            File.WriteAllText(target, "{\"GUID\":\"Other\",\"Version\":\"9.9.9\"}");
            summary = WorkshopStartupSummary.Describe(plan, german);
            Assert.IsFalse(summary.Contains("9.9.9")); StringAssert.Contains(summary, german ? "nicht eindeutig" : "could not be verified");
            File.Delete(target);
            StringAssert.Contains(WorkshopStartupSummary.Describe(plan, german), german ? "nicht eindeutig" : "could not be verified");
            Directory.Delete(Path.Combine(game, root), true);
            StringAssert.Contains(WorkshopStartupSummary.Describe(plan, german), german ? "erstmals" : "first installation");
            plan.Action = WorkshopAction.Update; plan.Previous = new WorkshopReceipt { Version = "1.0.25" };
            StringAssert.Contains(WorkshopStartupSummary.Describe(plan, german), "1.0.25");
        } finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
    }

    [TestMethod]
    public void StartupWaitsForDecisionAndStagingWithoutLosingTheMainThread()
    {
        var previous = SynchronizationContext.Current;
        int mainThread = Environment.CurrentManagedThreadId;
        bool completed = false;
        WorkshopStartupGate.Run(async () => {
            await Task.Run(() => Thread.Sleep(20));
            Assert.AreEqual(mainThread, Environment.CurrentManagedThreadId);
            // Represents confirmation followed by background staging and a main-thread handoff.
            await Task.Run(() => Thread.Sleep(20));
            Assert.AreEqual(mainThread, Environment.CurrentManagedThreadId);
            completed = true;
        });
        Assert.IsTrue(completed); Assert.AreSame(previous, SynchronizationContext.Current);
    }

    [TestMethod]
    public void StartupRestoresContextAfterDeclineOrFailure()
    {
        var previous = SynchronizationContext.Current;
        WorkshopStartupGate.Run(() => Task.CompletedTask);
        Assert.AreSame(previous, SynchronizationContext.Current);
        Assert.ThrowsExactly<IOException>(() => WorkshopStartupGate.Run(async () => { await Task.Delay(10); throw new IOException("staging failed"); }));
        Assert.AreSame(previous, SynchronizationContext.Current);
    }

    [TestMethod]
    public void StartupConsentRequiresAnOpenScopeAndConfirmedHandoffAndIsConsumedOnce()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopStartupGate.AuthorizeConfirmedUpdate());
        var order = new List<string>();
        WorkshopStartupGate.Run(async () => {
            var consent = WorkshopStartupGate.AuthorizeConfirmedUpdate();
            Assert.ThrowsExactly<InvalidOperationException>(() => consent.Quit(order.Add, () => order.Add("quit"), "update"));
            await Task.Delay(10);
            consent.Validate(); consent.ConfirmHandoff("confirmed-transaction");
            Assert.ThrowsExactly<InvalidOperationException>(() => consent.ConfirmHandoff("other-transaction"));
            consent.Quit(order.Add, () => order.Add("quit"), "update");
            Assert.ThrowsExactly<InvalidOperationException>(() => consent.Quit(order.Add, () => order.Add("duplicate quit"), "update"));
        });
        Assert.HasCount(2, order);
        StringAssert.Contains(order[0], "transaction=confirmed-transaction; handoff=confirmed");
        Assert.AreEqual("quit", order[1]);
    }

    [TestMethod]
    public void StartupConsentCannotEscapeToWorkersOrOutliveThePublisher()
    {
        WorkshopStartupAuthorization? escaped = null;
        int quits = 0;
        WorkshopStartupGate.Run(async () => {
            escaped = WorkshopStartupGate.AuthorizeConfirmedUpdate();
            escaped.ConfirmHandoff("confirmed-transaction");
            await Task.Run(() => Assert.ThrowsExactly<InvalidOperationException>(() => escaped.Validate()));
            escaped.Validate();
        });
        Assert.ThrowsExactly<InvalidOperationException>(() => escaped!.Quit(_ => { }, () => quits++, "late callback"));
        WorkshopStartupGate.Run(() => {
            Assert.ThrowsExactly<InvalidOperationException>(() => escaped!.Validate());
            return Task.CompletedTask;
        });
        Assert.AreEqual(0, quits);
    }

    [TestMethod]
    public void StartupFailureRevokesConsentAndCannotQuitAfterReturn()
    {
        WorkshopStartupAuthorization? escaped = null;
        Assert.ThrowsExactly<IOException>(() => WorkshopStartupGate.Run(async () => {
            escaped = WorkshopStartupGate.AuthorizeConfirmedUpdate();
            await Task.Delay(10);
            throw new IOException("handoff failed");
        }));
        Assert.ThrowsExactly<InvalidOperationException>(() => escaped!.Validate());
    }
}
