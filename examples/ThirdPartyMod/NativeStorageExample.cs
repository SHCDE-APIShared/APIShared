using APIShared;
using APIShared.Economy;
using APIShared.Goods;

namespace ThirdPartyMod
{
    internal static class NativeStorageExample
    {
        // Call on initialized Unity startup, before the first mission. This sample helper is
        // compiled but not automatically activated by ExamplePlugin.
        internal static bool Register(ModApiClient client, out NativeStoredGood armor, out string reason) =>
            client.NativeStorage.TryRegisterGood("medium-armor", NativeStoragePool.Armory, out armor, out reason);

        // The consumer has authenticated/authorized the player and is already inside a native
        // tick or authenticated Chore boundary holding the engine monitor. This is not a GUI command.
        internal static bool Buy(ModApiClient client, NativeStoredGood armor, int buildingId, long sequence,
            int quantity, int unitPrice, out NativeStorageSnapshot storage,
            out GoodsTransactionStatus status, out string reason)
        {
            storage = default;
            status = GoodsTransactionStatus.InvalidRequest;
            long price = (long)quantity * unitPrice;
            if (quantity <= 0 || unitPrice < 0 || price > int.MaxValue)
            { reason = "Invalid purchase quantity or price."; return false; }
            if (!client.NativeStorage.TryObserve(buildingId, out storage, out reason)) return false;
            // This helper starts a new buy. An in-world retry calls armor.TryExchange directly
            // with this returned storage plus the original sequence, quantity and total price.
            return armor.TryExchange(storage, sequence, quantity, -(int)price, out status, out reason);
        }
    }
}
