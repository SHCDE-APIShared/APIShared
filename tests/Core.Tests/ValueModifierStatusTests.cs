using APIShared.Modifiers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests;

[TestClass]
public sealed class ValueModifierStatusTests
{
    [TestMethod]
    public void ASecondServiceCanUseTheSameEntryPointWithNoLiveObservation()
    {
        var service = new TestValueService();
        ValueModifierStatusQuery query = service.CreateQuery(slot: 2);
        Assert.IsFalse(ValueModifiers.TryGetStatus(query, out var unavailable, out var reason));
        Assert.IsNull(unavailable);
        Assert.AreEqual("Slot 2 is unavailable.", reason);

        service.Publish(3);
        Assert.IsTrue(ValueModifiers.TryGetStatus(query, out var first, out reason), reason);
        Assert.AreEqual(4m, first.BaselineValue);
        Assert.AreEqual(12m, first.ResolvedValue);
        Assert.IsNull(first.Observation);
        Assert.IsNull(first.Bounds);

        service.Publish(5);
        Assert.IsTrue(ValueModifiers.TryGetStatus(query, out var second, out reason), reason);
        Assert.AreEqual(20m, second.ResolvedValue);
        Assert.AreEqual(12m, first.ResolvedValue);
    }

    [TestMethod]
    public void ANullQueryReturnsADiagnostic()
    {
        Assert.IsFalse(ValueModifiers.TryGetStatus(null, out var status, out var reason));
        Assert.IsNull(status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
    }

    private sealed class TestValueService
    {
        private ValueModifierStatus latest;

        internal void Publish(decimal factor)
        {
            const decimal baseline = 4;
            var contribution = ValueModifierContribution.Factor(factor);
            latest = new ValueModifierStatus(baseline, baseline * factor, null, null, null,
                new[] { new ValueModifierContributionStatus("test", "factor", 0, contribution, false, false) });
        }

        internal ValueModifierStatusQuery CreateQuery(int slot) => new SlotQuery(this, slot);

        private sealed class SlotQuery(TestValueService owner, int slot) : ValueModifierStatusQuery
        {
            internal override bool TryRead(out ValueModifierStatus status, out string reason)
            {
                status = slot == 2 ? owner.latest : null;
                reason = status == null ? $"Slot {slot} is unavailable." : string.Empty;
                return status != null;
            }
        }
    }
}
