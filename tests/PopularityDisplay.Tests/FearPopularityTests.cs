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
            (1,-1.5m,-2), (5,5m,25), (5,-5m,-25), (0,decimal.MaxValue,0), (5,2m,10), (-5,2m,-10), (5,0m,0)
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
            Assert.AreEqual(-119, FearPopularity.MultiplySwapped(-7,17), "Dormant reordered multiplication preserves SE");
            first.SetMultiplier(1.1m);
            Assert.AreEqual(150, FearPopularity.Multiply(17,5), "5.5 rounds to 6 points, then scales");
            Assert.AreEqual(150, FearPopularity.MultiplySwapped(5,17), "Reordered IL operands use the same rounded result");
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

    [TestMethod]
    public void FixedLevelsSelectTheInputLevelAndMatchEveryReportDelta()
    {
        Assert.IsTrue(FearPopularity.TryRegister("levels." + Guid.NewGuid(), "points", out var handle, out _));
        // Specification covers every Vanilla level, including zero and reversed output signs.
        var points = new Dictionary<int, int> { [-5]=-25, [-4]=3, [-3]=-9, [-2]=0, [-1]=-2,
            [1]=1, [2]=-7, [3]=9, [4]=20, [5]=25 };
        try
        {
            handle.SetLevelContributions(points);
            foreach (var entry in points)
            {
                Assert.AreEqual(entry.Value * 25, FearPopularity.Multiply(17, entry.Key));
                Assert.AreEqual(entry.Value * 25, FearPopularity.MultiplySwapped(entry.Key, 17));
                var original = new PopularityReport { FearLevel=entry.Key, Fear=entry.Key * 25, Total=1000 };
                var corrected = original.Correct(new Dictionary<string,int>());
                Assert.AreEqual(entry.Value * 25, corrected.Fear);
                Assert.AreEqual(1000 + entry.Value * 25 - original.Fear, corrected.Total);
                Assert.AreEqual(entry.Key * 25, original.Fear);
            }
            Assert.AreEqual(0, FearPopularity.Multiply(17,0), "Neutral level remains neutral");
            Assert.AreEqual(119, FearPopularity.Multiply(17,7), "Missing levels preserve the original SE coefficient");
            var extreme = new PopularityReport { FearLevel=-3, Fear=-75, Total=1000 };
            var fixedReport = extreme.Correct(new Dictionary<string,int> { ["GoodBadThingPopModifierMultiplier"]=int.MaxValue });
            Assert.AreEqual(FearPopularity.Multiply(int.MaxValue,-3), fixedReport.Fear,
                "A replaced foreign coefficient cannot overflow before the fixed contribution is selected");
        }
        finally { handle.SetLevelContributions(null); }
    }

    [TestMethod]
    public void SparseTablesComposeByExistingOrderAndSettersReplaceEachOther()
    {
        string owner = "composition." + Guid.NewGuid();
        Assert.IsTrue(FearPopularity.TryRegister(owner,"early",out var early,out _, order:-10));
        Assert.IsTrue(FearPopularity.TryRegister(owner,"middle",out var middle,out _, order:0));
        Assert.IsTrue(FearPopularity.TryRegister(owner,"late",out var late,out _, order:10));
        try
        {
            early.SetLevelContributions(new Dictionary<int,int> { [-3]=-6, [3]=6 });
            middle.SetMultiplier(1.1m);
            late.SetLevelContributions(new Dictionary<int,int> { [-3]=-12 });
            Assert.AreEqual(-300, FearPopularity.Multiply(17,-3), "Later table overrides the earlier uniform coefficient");
            Assert.AreEqual(75, FearPopularity.Multiply(17,3), "Uniform coefficient supersedes earlier tables");
            middle.SetMultiplier(null);
            Assert.AreEqual(150, FearPopularity.Multiply(17,3), "Unspecified level reveals the earlier table");
            late.SetLevelContributions(new Dictionary<int,int> { [3]=0 });
            Assert.AreEqual(-150, FearPopularity.Multiply(17,-3), "Replacing a table removes its old keys");
            Assert.AreEqual(0, FearPopularity.Multiply(17,3), "Explicit zero is active");
            late.SetMultiplier(2m);
            Assert.AreEqual(150, FearPopularity.Multiply(17,3), "Multiplier replaces the table");
            late.SetLevelContributions(new Dictionary<int,int>());
            Assert.AreEqual(150, FearPopularity.Multiply(17,3), "Empty table disables even a prior multiplier");
            early.SetLevelContributions(null);
            Assert.AreEqual(51, FearPopularity.Multiply(17,3), "All disabled restores the foreign SE product");
        }
        finally { early.SetMultiplier(null); middle.SetMultiplier(null); late.SetMultiplier(null); }
    }

    [TestMethod]
    public void CallerMutationAndInvalidUpdatesCannotChangeThePublishedTable()
    {
        Assert.IsTrue(FearPopularity.TryRegister("validation." + Guid.NewGuid(), "points", out var handle, out _));
        var points = new Dictionary<int,int> { [-2]=-7, [2]=4 };
        try
        {
            handle.SetLevelContributions(points);
            points[-2] = 100;
            points.Clear();
            Assert.AreEqual(-175, FearPopularity.Multiply(17,-2), "Input is defensively copied");
            Assert.ThrowsExactly<ArgumentException>(() => handle.SetLevelContributions(new Dictionary<int,int> { [-2]=9, [0]=1 }));
            Assert.AreEqual(-175, FearPopularity.Multiply(17,-2), "Invalid neutral entry cannot publish a partial update");
            Assert.ThrowsExactly<OverflowException>(() => handle.SetLevelContributions(new Dictionary<int,int> { [-2]=9, [2]=int.MaxValue }));
            Assert.AreEqual(-175, FearPopularity.Multiply(17,-2), "Overflow leaves all previous levels intact");
            Assert.AreEqual(100, FearPopularity.Multiply(17,2));
            handle.SetMultiplier(1.5m);
            Assert.ThrowsExactly<OverflowException>(() => handle.SetLevelContributions(new Dictionary<int,int> { [2]=int.MinValue }));
            Assert.AreEqual(125, FearPopularity.Multiply(17,3), "A failed table update also preserves a previous multiplier");
        }
        finally { handle.SetMultiplier(null); }
    }

    [TestMethod]
    public void PublishedTableAndDormantLookupAllocateNothing()
    {
        Assert.IsTrue(FearPopularity.TryRegister("allocation." + Guid.NewGuid(), "points", out var handle, out _));
        try
        {
            handle.SetLevelContributions(new Dictionary<int,int> { [-3]=-6 });
            for (int i=0; i<1000; i++) { FearPopularity.TryCalculate(-3,out _); FearPopularity.TryCalculate(3,out _); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i=0; i<1000; i++) { FearPopularity.TryCalculate(-3,out _); FearPopularity.TryCalculate(3,out _); }
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread()-before);
            handle.SetLevelContributions(null);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i=0; i<1000; i++) FearPopularity.TryCalculate(-3,out _);
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread()-before);
        }
        finally { handle.SetMultiplier(null); }
    }
}
