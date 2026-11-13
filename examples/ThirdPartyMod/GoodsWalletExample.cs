using System;
using APIShared;
using APIShared.Economy;
using APIShared.Goods;

namespace ThirdPartyMod
{
    internal static class GoodsWalletExample
    {
        // Call only from an authorized engine-protected boundary; multiplayer also requires
        // a shared native tick or authenticated Chore, and matching prices/catalogs on every peer.
        internal static GoodsWalletResult BuyOne(ModApiClient client, CustomGoodsClient goods,
            CustomGoodId armor, int player, long operationSequence, int price)
        {
            if (price < 0) throw new ArgumentOutOfRangeException(nameof(price));
            return client.GoodsWallet.Exchange(goods, operationSequence,
                new[] { new GoodsChange(armor, player, 1) }, player, -price);
        }
    }
}
