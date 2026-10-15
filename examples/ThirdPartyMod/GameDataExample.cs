using System;
using APIShared;
using APIShared.GameTables;

namespace ThirdPartyMod
{
    // Market price multipliers as GameData layers instead of rewriting the trade price table: a factor of
    // 100 percent writes nothing, and other mods' layers (for example absolute prices) compose with these.
    // Register during startup on every multiplayer peer; the settings must be identical on all peers.
    internal static class GameDataExample
    {
        private static volatile int buyPercent = 125, sellPercent = 90;
        private static GameDataRegistration buy, sell;

        internal static void Register(ModApiClient api, Action<string> logger)
        {
            if (!GameDataLayers.TryRegister(api.OwnerGuid, "market-buy", GameDataTable.TradeBasePrices, GameDataLayer.Multiplier, 0,
                    Buy, out buy, out string reason))
                logger(reason);
            if (!GameDataLayers.TryRegister(api.OwnerGuid, "market-sell", GameDataTable.TradeBasePrices, GameDataLayer.Multiplier, 0,
                    Sell, out sell, out reason))
                logger(reason);
        }

        // Settings changed in a running mission: re-compose at a point that is the same on every peer.
        internal static void Configure(int buyPercentValue, int sellPercentValue)
        {
            buyPercent = buyPercentValue; sellPercent = sellPercentValue;
            GameDataLayers.Reapply(GameDataTable.TradeBasePrices);
        }

        private static bool Buy(GameDataCell cell, out GameDataContribution contribution)
        {
            contribution = GameDataContribution.Percent(buyPercent);
            return cell.Column == 0 && buyPercent != 100;
        }

        private static bool Sell(GameDataCell cell, out GameDataContribution contribution)
        {
            contribution = GameDataContribution.Percent(sellPercent);
            return cell.Column == 1 && sellPercent != 100;
        }

        internal static void Unregister()
        {
            buy?.Dispose();
            sell?.Dispose();
        }
    }
}
