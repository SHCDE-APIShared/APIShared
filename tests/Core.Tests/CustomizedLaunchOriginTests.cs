using APIShared.GameModes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public class CustomizedLaunchOriginTests
{
    [TestMethod]
    public void ArbitraryOwnersSupplyCurrentEvidenceWithoutReflection()
    {
        var registry = new CustomizedLaunchOriginRegistry();
        Assert.IsFalse(registry.Capture().SupportsBuiltInOrigins);
        CustomizedLaunchOrigin value = default;
        registry.Register("community.author.custom-launch", () => value);
        Assert.AreEqual(ExternalCustomizedOrigin.None, registry.Capture().Origin);
        value = new CustomizedLaunchOrigin(CustomizedLaunchOriginKind.CoopTrail, -1, 2, 4, true, true);
        ExternalCustomizedOrigin captured = registry.Capture();
        Assert.AreEqual(ExternalCustomizedOrigin.CoopTrail, captured.Origin);
        Assert.AreEqual(2, captured.TrailId);
        Assert.AreEqual(4, captured.MissionId);
        Assert.IsTrue(captured.RestoredFromSave);
        Assert.IsTrue(captured.LaunchPending);
        value = default;
        Assert.AreEqual(ExternalCustomizedOrigin.None, registry.Capture().Origin);
    }

    [TestMethod]
    public void OwnerConflictsNeverReplaceTheRegisteredProvider()
    {
        var registry = new CustomizedLaunchOriginRegistry();
        Func<CustomizedLaunchOrigin> original = () => CustomTrail();
        registry.Register("independent.mod", original);
        registry.Register("independent.mod", original);
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Register("independent.mod", () => default));
        Assert.AreEqual(ExternalCustomizedOrigin.CustomTrail, registry.Capture().Origin);
        Assert.ThrowsExactly<ArgumentException>(() => registry.Register(" ", original));
        Assert.ThrowsExactly<ArgumentNullException>(() => registry.Register("another.mod", null!));
    }

    [TestMethod]
    public void MultipleActiveProvidersConflictEvenWhenTheirEvidenceAgrees()
    {
        foreach (bool reverse in new[] { false, true })
        {
            var registry = new CustomizedLaunchOriginRegistry();
            registry.Register(reverse ? "second" : "first", () => CustomTrail());
            registry.Register(reverse ? "first" : "second", () => CustomTrail());
            Assert.IsTrue(registry.Capture().IsInvalid);
        }
    }

    [TestMethod]
    public void FailedProviderCannotEnableAnotherProvidersCustomizedOrigin()
    {
        var registry = new CustomizedLaunchOriginRegistry();
        registry.Register("working", () => CustomTrail());
        registry.Register("broken", () => throw new InvalidOperationException("Consumer callback failed"));
        Assert.IsTrue(registry.Capture().IsInvalid);
    }

    [TestMethod]
    public void InactiveProvidersAdvertiseTheirTrackingScopeWithoutInventingAnOrigin()
    {
        var registry = new CustomizedLaunchOriginRegistry();
        registry.Register("custom-only", () => new CustomizedLaunchOrigin(
            CustomizedLaunchOriginKind.None, -1, -1, -1, false, false, supportsBuiltInOrigins: false));
        Assert.IsFalse(registry.Capture().SupportsBuiltInOrigins);
        registry.Register("built-in-tracker", () => new CustomizedLaunchOrigin(
            CustomizedLaunchOriginKind.None, -1, -1, -1, false, false));
        Assert.IsTrue(registry.Capture().SupportsBuiltInOrigins);
        Assert.AreEqual(ExternalCustomizedOrigin.None, registry.Capture().Origin);
    }

    [TestMethod]
    public void ActiveProviderKeepsItsOwnTrackingScope()
    {
        var registry = new CustomizedLaunchOriginRegistry();
        registry.Register("custom-only", () => CustomTrail());
        registry.Register("inactive", () => new CustomizedLaunchOrigin(
            CustomizedLaunchOriginKind.None, -1, -1, -1, false, false));
        Assert.IsFalse(registry.Capture().SupportsBuiltInOrigins);
        Assert.AreEqual(ExternalCustomizedOrigin.CustomTrail, registry.Capture().Origin);
    }

    [TestMethod]
    public void CallbacksRunOnTheCaptureThreadOutsideTheLockAndUseAStableProviderSnapshot()
    {
        var registry = new CustomizedLaunchOriginRegistry();
        int threadId = Environment.CurrentManagedThreadId;
        bool added = false;
        registry.Register("first", () =>
        {
            Assert.AreEqual(threadId, Environment.CurrentManagedThreadId);
            if (!added)
            {
                Task registration = Task.Run(() => registry.Register("new-provider", () => CustomTrail()));
                Assert.IsTrue(registration.Wait(TimeSpan.FromSeconds(5)), "Registry lock was held across a callback.");
                added = true;
            }
            return CustomTrail();
        });
        Assert.IsFalse(registry.Capture().IsInvalid, "A provider added during capture must start at the next capture.");
        Assert.IsTrue(registry.Capture().IsInvalid);
    }

    [TestMethod]
    [DataRow(CustomizedLaunchOriginKind.CustomTrail, -1, 90, 1, false, true)]
    [DataRow(CustomizedLaunchOriginKind.CustomTrail, -1, 92, 999, false, true)]
    [DataRow(CustomizedLaunchOriginKind.CustomTrail, -1, 89, 1, false, false)]
    [DataRow(CustomizedLaunchOriginKind.CustomTrail, -1, 93, 1, false, false)]
    [DataRow(CustomizedLaunchOriginKind.CustomTrail, -1, 90, 0, false, false)]
    [DataRow(CustomizedLaunchOriginKind.CoopTrail, -1, 0, 1, false, true)]
    [DataRow(CustomizedLaunchOriginKind.CoopTrail, -1, 3, 10, false, true)]
    [DataRow(CustomizedLaunchOriginKind.CoopTrail, -1, 4, 10, false, false)]
    [DataRow(CustomizedLaunchOriginKind.CoopTrail, -1, 3, 11, false, false)]
    [DataRow(CustomizedLaunchOriginKind.VanillaTrail, 0, 0, 0, true, true)]
    [DataRow(CustomizedLaunchOriginKind.VanillaTrail, 2, 0, 0, true, true)]
    [DataRow(CustomizedLaunchOriginKind.VanillaTrail, 3, 0, 0, true, false)]
    [DataRow(CustomizedLaunchOriginKind.VanillaTrail, 0, 0, 0, false, false)]
    [DataRow(CustomizedLaunchOriginKind.SandsOfTime, 11, 0, 0, true, true)]
    [DataRow(CustomizedLaunchOriginKind.SandsOfTime, 18, 0, 0, true, true)]
    [DataRow(CustomizedLaunchOriginKind.SandsOfTime, 19, 0, 0, true, false)]
    [DataRow(CustomizedLaunchOriginKind.SandsOfTime, 11, -1, 0, true, false)]
    [DataRow((CustomizedLaunchOriginKind)99, 0, 0, 0, true, false)]
    public void InvalidOriginEvidenceFailsClosed(CustomizedLaunchOriginKind kind, int trailType,
        int trailId, int missionId, bool builtIn, bool valid)
    {
        var registry = new CustomizedLaunchOriginRegistry();
        registry.Register("community.provider", () => new CustomizedLaunchOrigin(
            kind, trailType, trailId, missionId, false, true, builtIn));
        Assert.AreEqual(!valid, registry.Capture().IsInvalid);
    }

    private static CustomizedLaunchOrigin CustomTrail() => new(
        CustomizedLaunchOriginKind.CustomTrail, -1, 90, 1, false, true, supportsBuiltInOrigins: false);
}
