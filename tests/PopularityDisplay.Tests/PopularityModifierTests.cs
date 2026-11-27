using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using APIShared.Economy;
using APIShared.Modifiers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass,DoNotParallelize]
public sealed class PopularityModifierTests
{
    [TestMethod]
    public void OrdinaryFixedAndFactorPoliciesComposeByStageAndPreserveRoleAndLegacyOutputs()
    {
        string owner=Guid.NewGuid().ToString();var source=PopularitySource.RationsPopModifierNone;
        PopularityOverrides.TryRegister(owner,"before",out var before,out _,0);
        PopularityOverrides.TryRegister(owner,"fixed",out var fixedValue,out _,10);
        PopularityOverrides.TryRegister(owner,"after",out var after,out _,20);
        try {
            before.SetModifiers(new Dictionary<PopularitySource,PopularityModifierValues>{{source,new PopularityModifierValues(ValueModifierContribution.Factor(2),ValueModifierContribution.None)}});
            fixedValue.SetContributions(new Dictionary<PopularitySource,PopularityOverrideValues>{{source,new PopularityOverrideValues(-3,4)}});
            after.SetModifiers(new Dictionary<PopularitySource,PopularityModifierValues>{{source,new PopularityModifierValues(ValueModifierContribution.Factor(1.5m,ValueModifierStage.AfterFixedValue),ValueModifierContribution.None)}});
            Assert.AreEqual(-125,PopularitySelection.Resolve(-200,PopularitySelection.ForKind(1),(int)source));
            Assert.AreEqual(100,PopularitySelection.Resolve(-200,PopularitySelection.ForKind(2),(int)source));
            Assert.AreEqual(-200,PopularitySelection.Resolve(-200,PopularitySelection.ForKind(0),(int)source));
            fixedValue.SetContributions(null);
            Assert.AreEqual(-600,PopularitySelection.Resolve(-200,PopularitySelection.ForKind(1),(int)source));
            before.SetContributions(null);after.SetContributions(null);
            Assert.AreEqual(-200,PopularitySelection.Resolve(-200,PopularitySelection.ForKind(1),(int)source));
        }finally{before.SetContributions(null);fixedValue.SetContributions(null);after.SetContributions(null);}
    }
    [TestMethod]
    public void FearAbsoluteLegacyCoefficientAndOptInFactorsRemainDistinct()
    {
        string owner=Guid.NewGuid().ToString();
        FearPopularity.TryRegister(owner,"before",out var before,out _,0);
        FearPopularity.TryRegister(owner,"legacy",out var legacy,out _,10);
        FearPopularity.TryRegister(owner,"after",out var after,out _,20);
        try {
            before.SetFactor(2);legacy.SetMultiplier(1.1m);after.SetFactor(1.5m,ValueModifierStage.AfterFixedValue);
            Assert.AreEqual(225,FearPopularity.Multiply(25,5)); // legacy six whole points, then *1.5
            Assert.AreEqual(-225,FearPopularity.Multiply(25,-5));
            Assert.AreEqual(0,FearPopularity.Multiply(25,0));
            // Warm the full sampling routine: first-use tiered JIT/harness allocations are not
            // per-read allocations. The neighboring targeted-policy test uses the same warm-up contract.
            for(int i=0;i<4;i++)MeasureCommonFearReads(out _);
            long allocated=MeasureCommonFearReads(out int checksum);
            Assert.AreEqual(225*10000,checksum,"Every sampled read must execute the configured plan.");
            Assert.AreEqual(0L,allocated,"Compiled common-level fear plans allocate nothing per read.");
            legacy.SetMultiplier(null);
            Assert.AreEqual(375,FearPopularity.Multiply(25,5));
            after.SetMultiplier(null);before.SetMultiplier(null);
            Assert.AreEqual(85,FearPopularity.Multiply(17,5));
            before.SetLevelModifiers(new Dictionary<int,PopularityModifierValues>{{3,new PopularityModifierValues(ValueModifierContribution.FixedValue(-2),ValueModifierContribution.Factor(2))}});
            Assert.AreEqual(-50,FearPopularity.MultiplyTargeted(25,3,PopularitySelection.ForKind(1)));
            Assert.AreEqual(150,FearPopularity.MultiplyTargeted(25,3,PopularitySelection.ForKind(2)));
            Assert.AreEqual(75,FearPopularity.Multiply(25,3));
            Assert.ThrowsExactly<ArgumentException>(()=>before.SetLevelModifiers(new Dictionary<int,PopularityModifierValues>{{0,default}}));
            Assert.AreEqual(-50,FearPopularity.MultiplyTargeted(25,3,PopularitySelection.ForKind(1)));
        }finally{before.SetMultiplier(null);legacy.SetMultiplier(null);after.SetMultiplier(null);}
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureCommonFearReads(out int checksum)
    {
        long before=GC.GetAllocatedBytesForCurrentThread();
        checksum=0;
        for(int i=0;i<10000;i++)checksum+=FearPopularity.Multiply(25,5);
        return GC.GetAllocatedBytesForCurrentThread()-before;
    }
    [TestMethod]
    public void SupersededLegacyOverflowDoesNotDiscardLaterFixedOrStagedFactors()
    {
        string owner=Guid.NewGuid().ToString();
        FearPopularity.TryRegister(owner,"overflow",out var overflow,out _,0);
        FearPopularity.TryRegister(owner,"winner",out var winner,out _,10);
        FearPopularity.TryRegister(owner,"after",out var after,out _,20);
        try {
            overflow.SetMultiplier(decimal.MaxValue);
            winner.SetLevelContributions(new Dictionary<int,int>{{5,3}});
            after.SetFactor(2,ValueModifierStage.AfterFixedValue);
            Assert.AreEqual(150,FearPopularity.Multiply(25,5));
            Assert.AreEqual(-125,FearPopularity.Multiply(25,-5),"A winning overflowing legacy coefficient retains its original fallback.");
            winner.SetLevelModifiers(new Dictionary<int,PopularityModifierValues>{{-5,new PopularityModifierValues(ValueModifierContribution.FixedValue(decimal.MinValue),ValueModifierContribution.None)}});
            Assert.AreEqual(PopularityModifierMath.MinimumPoints*25,FearPopularity.MultiplyTargeted(25,-5,PopularitySelection.ForKind(1)));
        }finally{overflow.SetMultiplier(null);winner.SetMultiplier(null);after.SetMultiplier(null);}
    }
}
