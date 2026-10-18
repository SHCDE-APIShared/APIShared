using APIShared.Economy;
using APIShared.ScriptExtenderFixes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;

[TestClass, DoNotParallelize]
public sealed class FearPopularityTests
{
    [TestMethod]
    public void ExactTenthsRoundOnlyAfterMultiplication()
    {
        var cases = new[] {
            (5,1.1m,6), (-5,1.1m,-6), (5,-1.1m,-6), (-5,-1.1m,6),
            (4,0.1m,0), (5,0.1m,1), (5,-0.1m,-1), (3,1.5m,5),
            (1,-1.5m,-2), (5,5m,25), (5,-5m,-25), (0,decimal.MaxValue,0)
        };
        foreach (var value in cases)
            Assert.AreEqual(value.Item3, FearPopularity.CalculatePoints(value.Item1,value.Item2));
    }
    [TestMethod]
    public void OwnerOrderingLogicalDisableForeignValuesAndDisplayAgree()
    {
        string owner = "third.party." + Guid.NewGuid();
        Assert.IsTrue(FearPopularity.TryRegister(owner,"a",out var first,out _));
        Assert.IsTrue(FearPopularity.TryRegister(owner,"b",out var last,out _));
        Assert.IsFalse(FearPopularity.TryRegister(owner,"a",out _,out _));
        try
        {
            Assert.AreEqual(-119, FearPopularity.Multiply(17,-7), "Dormant keeps foreign SE coefficient");
            first.SetMultiplier(1.1m);
            Assert.AreEqual(150, FearPopularity.Multiply(17,5), "5.5 rounds to 6 points, then scales");
            var original = new PopularityReport { FearLevel=5, Fear=125, Total=300 };
            var corrected = original.Correct(new Dictionary<string,int>());
            Assert.AreEqual(150, corrected.Fear);
            Assert.AreEqual(325, corrected.Total);
            Assert.AreEqual(125, original.Fear, "Presentation input is immutable");
            last.SetMultiplier(-0.1m);
            Assert.AreEqual(-25, FearPopularity.Multiply(17,5), "Deterministic last registration wins");
            last.SetMultiplier(null);
            Assert.AreEqual(150, FearPopularity.Multiply(17,5), "Disable reveals earlier contribution");
            first.SetMultiplier(decimal.MaxValue);
            Assert.AreEqual(34, FearPopularity.Multiply(17,2), "Overflow preserves original SE arithmetic");
        }
        finally { first.SetMultiplier(null); last.SetMultiplier(null); }
        Assert.AreEqual(85, FearPopularity.Multiply(17,5), "All disabled restores foreign SE coefficient");
    }
}
