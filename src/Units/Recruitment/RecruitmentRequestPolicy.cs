using System;

namespace APIShared.Recruitment
{
    /// <summary>Result of imposing a recruitment ceiling without duplicating Vanilla resource checks.</summary>
    public enum RecruitmentConstraintAction
    {
        /// <summary>Keep the supplied request, including Vanilla feedback.</summary>
        PreserveOriginal,
        /// <summary>Forward a concrete smaller amount.</summary>
        ForwardAmount,
        /// <summary>Do not send this request.</summary>
        Block
    }
    /// <summary>Immutable ceiling decision; it does not prove that soldiers were recruited.</summary>
    public readonly struct RecruitmentConstraintDecision
    {
        internal RecruitmentConstraintDecision(RecruitmentConstraintAction action, int requested, int forward)
        { Action = action; EffectiveRequestedAmount = requested; AmountToForward = forward; }
        /// <summary>Requested handling.</summary>
        public RecruitmentConstraintAction Action { get; }
        /// <summary>Nonnegative preview or concrete request.</summary>
        public int EffectiveRequestedAmount { get; }
        /// <summary>Amount to forward when the action calls for replacement.</summary>
        public int AmountToForward { get; }
    }
    /// <summary>Pure request arithmetic shared by limits, costs and other recruitment policies. Thread independent.</summary>
    public static class RecruitmentRequestPolicy
    {
        /// <summary>Vanilla UI's Ctrl request ceiling. Once explicitly replaced, even 1000 is a concrete amount.</summary>
        public const int VanillaCtrlAllAmount = 1000;
        /// <summary>Applies a maximum to the current request. A zero Vanilla preview preserves its feedback; nonpositive maxima block. No native query or mutation occurs.</summary>
        public static RecruitmentConstraintDecision ApplyMaximum(int incomingAmount, int vanillaCtrlAmount,
            int maximumAllowed, bool interpretCtrlSentinel = true)
        {
            int requested = interpretCtrlSentinel && incomingAmount == VanillaCtrlAllAmount
                ? Math.Max(0, vanillaCtrlAmount) : Math.Max(0, incomingAmount);
            if (maximumAllowed <= 0)
                return new RecruitmentConstraintDecision(RecruitmentConstraintAction.Block, requested, 0);
            if (requested <= 0 || maximumAllowed >= requested)
                return new RecruitmentConstraintDecision(RecruitmentConstraintAction.PreserveOriginal, requested, incomingAmount);
            return new RecruitmentConstraintDecision(RecruitmentConstraintAction.ForwardAmount, requested, maximumAllowed);
        }
        /// <summary>Bounds an owner's reservation by the final concrete amount. Reservations remain estimates until observed unit transitions.</summary>
        public static int ReconcilePendingAmount(int plannedAmount, int finalAmount, bool hasConcreteAmount) =>
            hasConcreteAmount ? Math.Min(Math.Max(0, plannedAmount), Math.Max(0, finalAmount)) : Math.Max(0, plannedAmount);
    }
}
