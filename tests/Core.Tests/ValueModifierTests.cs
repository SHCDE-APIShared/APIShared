using System;
using APIShared.Modifiers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class ValueModifierTests
{
    [TestMethod]
    public void StagesAreIndependentOfFactorRegistrationOrderAndFixedLastWins()
    {
        var before=ValueModifierContribution.Factor(2);
        var after=ValueModifierContribution.Factor(3,ValueModifierStage.AfterFixedValue);
        var fixedValue=ValueModifierContribution.FixedValue(7);
        foreach(var values in new[]{new[]{before,fixedValue,after},new[]{after,fixedValue,before},new[]{fixedValue,after,before}})
            Assert.AreEqual(21m,ValueModifierCalculator.Calculate(10,values,-100,100));
        Assert.AreEqual(60m,ValueModifierCalculator.Calculate(10,new[]{after,before},-100,100));
        Assert.AreEqual(-6m,ValueModifierCalculator.Calculate(10,new[]{fixedValue,ValueModifierContribution.FixedValue(-2),after},-100,100));
    }
    [TestMethod]
    public void ExactProductsRoundAndClampOnlyOnceEvenAfterOverflowAndReciprocal()
    {
        const decimal huge=10000000000000000000000000000m;
        var values=new[]{ValueModifierContribution.Factor(huge),ValueModifierContribution.Factor(huge),
            ValueModifierContribution.Factor(0.0000000000000000000000000001m),ValueModifierContribution.Factor(0.0000000000000000000000000001m)};
        Assert.AreEqual(3m,ValueModifierCalculator.Calculate(3,values,-100,100));
        Assert.AreEqual(1m,ValueModifierCalculator.Calculate(1,new[]{ValueModifierContribution.Factor(.5m)},-100,100));
        Assert.AreEqual(-1m,ValueModifierCalculator.Calculate(-1,new[]{ValueModifierContribution.Factor(.5m)},-100,100));
        Assert.AreEqual(0m,ValueModifierCalculator.Calculate(1,new[]{ValueModifierContribution.Factor(.49m),ValueModifierContribution.Factor(.6m)},-100,100));
        Assert.AreEqual(123m,ValueModifierCalculator.Calculate(decimal.MaxValue,new[]{ValueModifierContribution.Factor(decimal.MaxValue)},-123,123));
        Assert.AreEqual(0m,ValueModifierCalculator.Calculate(huge,new[]{ValueModifierContribution.Factor(huge),ValueModifierContribution.Factor(0)},-100,100));
        Assert.AreEqual(2m,ValueModifierCalculator.Calculate(1,new[]{ValueModifierContribution.FixedValue(2),ValueModifierContribution.Factor(huge)},-100,100));
    }
    [TestMethod]
    public void InvalidInputsAreRejectedAndCompiledCommonPathDoesNotAllocate()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>ValueModifierContribution.Factor(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>ValueModifierContribution.Factor(1,(ValueModifierStage)7));
        Assert.ThrowsExactly<ArgumentException>(()=>ValueModifierCalculator.Calculate(1,null,2,1));
        Assert.ThrowsExactly<ArgumentException>(()=>ValueModifierCalculator.Calculate(1,null,.1m,10));
        var plan=ValueModifierPlan.Create(new[]{ValueModifierContribution.Factor(1.5m)});
        for(int i=0;i<100;i++)Assert.AreEqual(15m,plan.Calculate(10,-100,100));
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)plan.Calculate(10,-100,100);
        Assert.AreEqual(before,GC.GetAllocatedBytesForCurrentThread());
        Assert.AreEqual(5m,plan.CalculateRatio(85,25,-100,100));
        before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)plan.CalculateRatio(85,25,-100,100);
        Assert.AreEqual(before,GC.GetAllocatedBytesForCurrentThread());
    }
    [TestMethod]
    public void CallbackCheckpointRestoresFixedWritesAndTypedContributions()
    {
        var value=new StagedInt32Value(10);
        value.Current=7;
        var checkpoint=value.Capture();
        value.Contribute(ValueModifierContribution.Factor(100,ValueModifierStage.AfterFixedValue));value.Current=3;
        value.Restore(checkpoint);
        value.Contribute(ValueModifierContribution.Factor(2));
        Assert.AreEqual(7,value.Resolve());
        value.Contribute(ValueModifierContribution.Factor(3,ValueModifierStage.AfterFixedValue));
        Assert.AreEqual(21,value.Resolve());
    }
}
