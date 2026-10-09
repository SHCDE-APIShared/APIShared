using APIShared.Pathfinding;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public class RouteSearchEventTests
{
    private static RouteSearchContext Context() => new("community.route-publisher", 1, 2, 3, 4, 5,
        RouteSearchTerrain.FriendlyMoat, 1998, 10000);

    [TestMethod]
    public void OwnerLocalIdsAndOrdinalOrderAllowIndependentMods()
    {
        var registry = new RouteSearchEventRegistry();
        var calls = new List<string>();
        Assert.IsTrue(registry.TryRegister("z", "same", a => { calls.Add("z"); a.MoatEdgeCost += 3; },
            a => calls.Add("post-z"), out _));
        Assert.IsTrue(registry.TryRegister("a", "same", a => { calls.Add("a"); a.MoatEdgeCost *= 2; },
            a => calls.Add("post-a"), out _));
        Assert.IsFalse(registry.TryRegister("a", "same", _ => { }, null, out _));
        var operation = registry.Begin(Context(), 10, 20);
        Assert.AreEqual(43L, operation.Pre.MoatEdgeCost);
        operation.Complete(true, "found", 5, 12);
        CollectionAssert.AreEqual(new[] { "a", "z", "post-a", "post-z" }, calls);
    }

    [TestMethod]
    public void SkippingTheAdditionalSearchDoesNotProducePost()
    {
        var registry = new RouteSearchEventRegistry();
        int post = 0;
        registry.TryRegister("author", "veto", a => a.SkipOriginalFunction = true, _ => post++, out _);
        var operation = registry.Begin(Context(), 10, 20);
        Assert.IsTrue(operation.Pre.SkipOriginalFunction);
        operation.Complete(false, "not-run", 0, 0);
        Assert.AreEqual(0, post);
    }

    [TestMethod]
    public void FailingPreRollsBackPartialCostAndCancellationChanges()
    {
        var registry = new RouteSearchEventRegistry();
        registry.TryRegister("a", "valid", a => a.MoatEdgeCost = 30, null, out _);
        registry.TryRegister("b", "throw", a => {
            a.MoatEdgeCost = 999; a.SkipOriginalFunction = true; throw new InvalidOperationException();
        }, null, out _);
        var operation = registry.Begin(Context(), 10, 20);
        Assert.AreEqual(30L, operation.Pre.MoatEdgeCost);
        Assert.IsFalse(operation.Pre.SkipOriginalFunction);
        Assert.AreEqual(1L, registry.CallbackFailures);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    public void InvalidCostContributionKeepsLastValidCosts(long cost)
    {
        var registry = new RouteSearchEventRegistry();
        registry.TryRegister("author", "cost", a => a.GroundEdgeCost = cost, null, out _);
        var operation = registry.Begin(Context(), 10, 20);
        Assert.AreEqual(10L, operation.Pre.GroundEdgeCost);
        Assert.AreEqual(1L, registry.CallbackFailures);
    }

    [TestMethod]
    public void LateRegistrationDoesNotJoinAnInFlightOperation()
    {
        var registry = new RouteSearchEventRegistry();
        int late = 0;
        registry.TryRegister("a", "first", args => registry.TryRegister("z", "late", null, result => late++, out _), null, out _);
        registry.Begin(Context(), 10, 20).Complete(false, "no-route", 0, 12);
        Assert.AreEqual(0, late);
        registry.Begin(Context(), 10, 20).Complete(false, "no-route", 0, 12);
        Assert.AreEqual(1, late);
    }

    [TestMethod]
    public void PostFailuresAreIsolatedAndCompletionIsSingleUse()
    {
        var registry = new RouteSearchEventRegistry();
        int completed = 0;
        registry.TryRegister("a", "throw", null, _ => throw new InvalidOperationException(), out _);
        registry.TryRegister("z", "result", null, a => {
            Assert.IsFalse(a.Success); Assert.AreEqual("no-route", a.Reason);
            Assert.AreEqual(0, a.RouteLength); Assert.AreEqual(12, a.ExpandedNodes); completed++;
        }, out _);
        var operation = registry.Begin(Context(), 10, 20);
        operation.Complete(false, "no-route", 0, 12);
        operation.Complete(true, "different", 1, 0);
        Assert.AreEqual(1, completed);
        Assert.AreEqual(1L, registry.CallbackFailures);
    }

    [TestMethod]
    public void ExpiredPreArgumentsCannotChangeAnInFlightSearch()
    {
        var registry = new RouteSearchEventRegistry();
        registry.TryRegister("author", "pre", _ => { }, null, out _);
        var operation = registry.Begin(Context(), 10, 20);
        Assert.ThrowsExactly<InvalidOperationException>(() => operation.Pre.MoatEdgeCost = 25);
        Assert.ThrowsExactly<InvalidOperationException>(() => operation.Pre.SkipOriginalFunction = true);
    }

    [TestMethod]
    public void NestedCalculationsDuringNotificationsAreSkippedWithoutRecursion()
    {
        var registry = new RouteSearchEventRegistry();
        int calls = 0;
        registry.TryRegister("author", "nested", _ => {
            calls++;
            var nested = registry.Begin(Context(), 10, 20);
            Assert.IsTrue(nested.Pre.SkipOriginalFunction);
        }, null, out _);
        Assert.IsFalse(registry.Begin(Context(), 10, 20).Pre.SkipOriginalFunction);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(0L, registry.CallbackFailures);
        Assert.IsFalse(registry.Begin(Context(), 10, 20).Pre.SkipOriginalFunction);
        Assert.AreEqual(2, calls);
        Assert.AreEqual(0L, registry.CallbackFailures);
    }

    [TestMethod]
    public void EmptyRegistryAddsNoInvocationAndRejectsInvalidRegistrations()
    {
        var registry = new RouteSearchEventRegistry();
        Assert.IsNull(registry.Begin(Context(), 10, 20));
        Assert.IsFalse(registry.TryRegister("", "id", _ => { }, null, out _));
        Assert.IsFalse(registry.TryRegister("owner", "", _ => { }, null, out _));
        Assert.IsFalse(registry.TryRegister("owner", "id", null, null, out _));
    }
}
