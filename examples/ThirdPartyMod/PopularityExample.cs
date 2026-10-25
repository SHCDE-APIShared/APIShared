using APIShared.Economy;
using System;
using System.Collections.Generic;

namespace ThirdPartyMod
{
    // Register after native initialization; the caller owns synchronized mission activation.
    internal static class PopularityExample
    {
        internal static PopularityOverrideRegistration RegisterSources(string pluginGuid)
        {
            if (!PopularityOverrides.TryRegister(pluginGuid,"popularity-sources",out var handle,out var diagnostic))
                throw new InvalidOperationException(diagnostic);
            handle.SetContributions(new Dictionary<PopularitySource,PopularityOverrideValues> {
                [PopularitySource.RationsPopModifierNone] = new PopularityOverrideValues(-12,-8),
                [PopularitySource.TaxPopModifierLow] = new PopularityOverrideValues(0,null)
            });
            // Retain the handle and SetContributions(null) at mission end/disable.
            return handle;
        }
        internal static void TargetFear(FearPopularityRegistration handle)
        {
            handle.SetTargetedLevelContributions(new Dictionary<int,PopularityOverrideValues> {
                [-1] = new PopularityOverrideValues(-2,-1),
                [-2] = new PopularityOverrideValues(-4,null)
            });
        }
        internal static FearPopularityRegistration Register(string pluginGuid)
        {
            if (!FearPopularity.TryRegister(pluginGuid, "fear-coefficient", out var handle, out var diagnostic))
                throw new InvalidOperationException(diagnostic);
            handle.SetLevelContributions(new Dictionary<int, int>
            { [-1]=-2, [-2]=-4, [-3]=-6, [-4]=-8, [-5]=-10 });
            // Positive levels are unspecified and retain the preceding registration/SE calculation.
            // SetMultiplier(1.1m) remains available and replaces this handle's table completely.
            // Retain the handle; call SetLevelContributions(null) at mission end or logical disable.
            return handle;
        }
    }
}
