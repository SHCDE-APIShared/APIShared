using APIShared.Economy;
using APIShared.ScriptExtenderFixes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;

[TestClass]
public sealed class TargetedPopularityTests
{
    [TestMethod]
    public void RoleTablesAreIndependentCopiedAtomicAndPreserveForeignFallbacks()
    {
        string owner=Guid.NewGuid().ToString();
        Assert.IsTrue(PopularityOverrides.TryRegister(owner,"first",out var first,out _,10));
        Assert.IsTrue(PopularityOverrides.TryRegister(owner,"later",out var later,out _,20));
        try {
            var source=PopularitySource.RationsPopModifierNone;
            var input=new Dictionary<PopularitySource,PopularityOverrideValues>();
            foreach(PopularitySource key in Enum.GetValues(typeof(PopularitySource)))input[key]=new PopularityOverrideValues(0,-7);
            first.SetContributions(input); input.Clear();
            foreach(PopularitySource key in Enum.GetValues(typeof(PopularitySource))) {
                Assert.AreEqual(0,PopularitySelection.Resolve(777,PopularitySelection.ForKind(1),(int)key));
                Assert.AreEqual(-175,PopularitySelection.Resolve(777,PopularitySelection.ForKind(2),(int)key));
                Assert.AreEqual(777,PopularitySelection.Resolve(777,PopularitySelection.ForKind(0),(int)key));
            }
            later.SetContributions(new Dictionary<PopularitySource,PopularityOverrideValues>{{source,new PopularityOverrideValues(null,12)}});
            Assert.AreEqual(0,PopularitySelection.Resolve(777,PopularitySelection.ForKind(1),(int)source));
            Assert.AreEqual(300,PopularitySelection.Resolve(777,PopularitySelection.ForKind(2),(int)source));
            var old=PopularitySelection.ForKind(2);
            Assert.ThrowsExactly<OverflowException>(()=>later.SetContributions(new Dictionary<PopularitySource,PopularityOverrideValues>{{source,new PopularityOverrideValues(3,int.MaxValue)}}));
            Assert.AreSame(old,PopularitySelection.ForKind(2));
            Assert.ThrowsExactly<ArgumentException>(()=>later.SetContributions(new Dictionary<PopularitySource,PopularityOverrideValues>{{(PopularitySource)int.MaxValue,new PopularityOverrideValues(3,4)}}));
            Assert.AreSame(old,PopularitySelection.ForKind(2));
            later.SetContributions(null);
            Assert.AreEqual(-175,PopularitySelection.Resolve(777,PopularitySelection.ForKind(2),(int)source));
            var selected=PopularitySelection.ForKind(1);
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<100000;i++)PopularitySelection.Resolve(777,selected,(int)source);
            Assert.AreEqual(before,GC.GetAllocatedBytesForCurrentThread());
        } finally {first.SetContributions(null);later.SetContributions(null);}
        Assert.IsFalse(PopularitySelection.NeedsPlayer);
    }
    [TestMethod]
    public void MixedFearApisResolveEachRoleAndLevelAndSettersReplaceTheWholePolicy()
    {
        string owner=Guid.NewGuid().ToString();
        Assert.IsTrue(FearPopularity.TryRegister(owner,"legacy",out var legacy,out _,30));
        Assert.IsTrue(FearPopularity.TryRegister(owner,"target",out var target,out _,40));
        Assert.IsTrue(FearPopularity.TryRegister(owner,"last",out var last,out _,50));
        try {
            legacy.SetMultiplier(2m);
            var input=new Dictionary<int,PopularityOverrideValues>();
            for(int level=-5;level<=5;level++)if(level!=0)input[level]=new PopularityOverrideValues(level<0?0:-(int?)level,level<0?7:(int?)null);
            target.SetTargetedLevelContributions(input);input.Clear();
            for(int level=-5;level<=5;level++) {
                var h=PopularitySelection.ForKind(1);var ai=PopularitySelection.ForKind(2);
                int human=level<0?0:level>0?-level*25:0, computer=level<0?175:level*50;
                Assert.AreEqual(human,FearPopularity.MultiplyTargeted(999,level,h));
                Assert.AreEqual(computer,FearPopularity.MultiplyTargeted(999,level,ai));
                var report=new PopularityReport {FearLevel=level,Fear=level*25,Total=level*25};
                Assert.AreEqual(human,report.Correct(new Dictionary<string,int>(),h).Fear);
                Assert.AreEqual(computer,report.Correct(new Dictionary<string,int>(),ai).Fear);
                Assert.AreEqual(level*50,FearPopularity.MultiplyTargeted(999,level,PopularitySelection.ForKind(0)));
            }
            var old=PopularitySelection.ForKind(1);
            Assert.ThrowsExactly<ArgumentException>(()=>target.SetTargetedLevelContributions(new Dictionary<int,PopularityOverrideValues>{{0,new PopularityOverrideValues(2,2)}}));
            Assert.AreSame(old,PopularitySelection.ForKind(1));
            Assert.ThrowsExactly<OverflowException>(()=>target.SetTargetedLevelContributions(new Dictionary<int,PopularityOverrideValues>{{-1,new PopularityOverrideValues(2,int.MinValue)}}));
            Assert.AreSame(old,PopularitySelection.ForKind(1));
            last.SetLevelContributions(new Dictionary<int,int>{{-1,-9}});
            Assert.AreEqual(-225,FearPopularity.MultiplyTargeted(25,-1,PopularitySelection.ForKind(1)));
            Assert.AreEqual(-225,FearPopularity.MultiplyTargeted(25,-1,PopularitySelection.ForKind(2)));
            last.SetMultiplier(3m);
            Assert.AreEqual(-75,FearPopularity.MultiplyTargeted(25,-1,PopularitySelection.ForKind(1)));
            last.SetMultiplier(null);target.SetMultiplier(1m);
            Assert.AreEqual(-25,FearPopularity.MultiplyTargeted(25,-1,PopularitySelection.ForKind(2)));
            target.SetTargetedLevelContributions(null);legacy.SetMultiplier(null);
            Assert.AreEqual(-369,FearPopularity.MultiplyTargeted(123,-3,PopularitySelection.ForKind(2)));
        } finally {legacy.SetMultiplier(null);target.SetMultiplier(null);last.SetMultiplier(null);}
        Assert.IsFalse(PopularitySelection.NeedsPlayer);
    }
}
