using APIShared.Economy;
using System;
using System.Collections.Generic;

namespace ThirdPartyMod
{
    // Register after native initialization; the caller owns synchronized mission activation.
    internal static class PopularityExample
    {
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
