using System;
using APIShared.Economy;

namespace ThirdPartyMod
{
    // Call once from LibraryLoaded, after APIShared's native initialization. Retain
    // the registration and apply the synchronized setting on EVERY simulation peer.
    internal static class AiMarketSellExample
    {
        private static AiMarketSellRegistration policy;
        internal static void Register(Action<string> log)
        {
            if (!AiMarketSellPolicies.TryRegister(ExamplePlugin.Guid, "market-stock-safety",
                AiMarketSellSafety.NonNegativeReserve | AiMarketSellSafety.LimitQuantityToStock,
                out policy, out string error))
                log("Sell policy unavailable: " + error);
            // Initially inactive: the example requests no gameplay change by itself.
        }
        internal static void ApplySynchronizedSetting(bool enabled) => policy?.SetEnabled(enabled);
    }
}
