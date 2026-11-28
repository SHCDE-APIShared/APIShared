using System;

namespace APIShared.WorldMutations
{
    internal static partial class WorldMutationRuntime
    {
        internal static int InvokeAiv(AivBuildStepContext context, Func<Func<int>, int> dispatch, Func<int> original)
        {
            if (!HasDemand(WorldMutationSource.All)) return dispatch(original);
            return InvokeObservedAiv(context, dispatch, original);
        }
        private static int InvokeObservedAiv(AivBuildStepContext context, Func<Func<int>, int> dispatch, Func<int> original)
        {
            long batch = Enter(WorldMutationSource.AivBuildStep);
            var state = Begin(WorldMutationSource.AivBuildStep, new[] { unchecked((long)context.AivStateAddress),
                (long)context.PlayerId, context.FrameIndex, context.RestrictedMode, context.FreeOrForced });
            bool invoked = false, returned = false, wrapperReturned = false;
            int result = 0; Exception failure = null;
            try
            {
                int outerResult = dispatch(() =>
                {
                    invoked = true;
                    try { result = original(); returned = true; return result; }
                    catch (Exception originalError) { failure = originalError; throw; }
                });
                wrapperReturned = true;
                return outerResult;
            }
            catch (Exception wrapperError) { if (!invoked && failure == null) failure = wrapperError; throw; }
            finally
            {
                Complete(state, invoked, returned, result, failure);
                Leave(batch, wrapperReturned);
            }
        }
    }
}
