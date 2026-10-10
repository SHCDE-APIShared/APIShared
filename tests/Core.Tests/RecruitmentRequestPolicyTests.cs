using APIShared.Recruitment;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public class RecruitmentRequestPolicyTests
{
    [TestMethod]
    public void CostsAndLimitsComposeForNormalAndCtrlRequests()
    {
        foreach (int request in new[] { 1, 5, 1000 })
        {
            var costs = RecruitmentRequestPolicy.ApplyMaximum(request, 17, 4);
            int forwarded = costs.Action == RecruitmentConstraintAction.ForwardAmount ? costs.AmountToForward : request;
            var limits = RecruitmentRequestPolicy.ApplyMaximum(forwarded, 17, 2, costs.Action != RecruitmentConstraintAction.ForwardAmount);
            Assert.AreEqual(request == 1 ? 1 : 2, limits.Action == RecruitmentConstraintAction.ForwardAmount ? limits.AmountToForward : forwarded);
        }
    }
    [TestMethod]
    public void ConcreteThousandIsNotReinterpretedAndZeroPreviewPreservesFeedback()
    {
        var concrete = RecruitmentRequestPolicy.ApplyMaximum(1000, 5, 8, false);
        Assert.AreEqual(RecruitmentConstraintAction.ForwardAmount, concrete.Action);
        Assert.AreEqual(8, concrete.AmountToForward);
        Assert.AreEqual(RecruitmentConstraintAction.PreserveOriginal, RecruitmentRequestPolicy.ApplyMaximum(1000, 0, 8).Action);
        Assert.AreEqual(RecruitmentConstraintAction.Block, RecruitmentRequestPolicy.ApplyMaximum(1000, 0, 0).Action);
    }
    [TestMethod]
    public void ReservationsCannotBecomeNegativeOrExceedFinalConcreteAmount()
    {
        Assert.AreEqual(3, RecruitmentRequestPolicy.ReconcilePendingAmount(9, 3, true));
        Assert.AreEqual(9, RecruitmentRequestPolicy.ReconcilePendingAmount(9, 1000, false));
        Assert.AreEqual(0, RecruitmentRequestPolicy.ReconcilePendingAmount(9, -1, true));
        Assert.AreEqual(0, RecruitmentRequestPolicy.ReconcilePendingAmount(-1, 4, false));
    }
}
