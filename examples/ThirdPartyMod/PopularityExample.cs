using APIShared.Economy;
using System;

namespace ThirdPartyMod
{
    // Register after native initialization; the caller owns synchronized mission activation.
    internal static class PopularityExample
    {
        internal static FearPopularityRegistration Register(string pluginGuid)
        {
            if (!FearPopularity.TryRegister(pluginGuid, "fear-coefficient", out var handle, out var diagnostic))
                throw new InvalidOperationException(diagnostic);
            handle.SetMultiplier(1.1m);
            // Retain the handle; call SetMultiplier(null) at mission end or logical disable.
            return handle;
        }
    }
}
