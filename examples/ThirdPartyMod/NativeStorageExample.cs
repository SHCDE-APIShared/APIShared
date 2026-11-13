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

        // Opt-in observation while already holding the actual engine monitor in a known world.
        // Protected multiplayer reads need no mutation scope. This grants no purchase permission.
        internal static bool TryReadQuantity(ModApiClient client, NativeStoredGood armor, int buildingId,
            out int quantity, out string reason)
        {
            quantity = 0;
            if (armor == null) { reason = "The registered good is unavailable."; return false; }
            return client.NativeStorage.TryObserve(buildingId, out var storage, out reason) &&
                armor.TryGetQuantity(storage, out quantity, out reason);
        }

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

        // Opt-in initial purchase, after consumer authorization. Hold the actual engine monitor
        // throughout; read armor.TryGetLastSequence, reject Int64.MaxValue and use last + 1
        // immediately before this call. That read reserves no operation number.
        // Real multiplayer also needs the existing native-tick/authenticated-Chore boundary.
        internal static bool BuyHundredAcrossTwoArmories(ModApiClient client, NativeStoredGood armor,
            int firstBuildingId, int secondBuildingId, long sequence, int unitPrice,
            out NativeStorageChange[] originalChanges, out GoodsTransactionStatus status, out string reason)
        {
            originalChanges = null; status = GoodsTransactionStatus.InvalidRequest;
            long price = 100L * unitPrice;
            if (armor == null || armor.Pool != NativeStoragePool.Armory || firstBuildingId == secondBuildingId ||
                unitPrice < 0 || price > int.MaxValue)
            { reason = "The purchase needs two distinct Armories, an Armory good and a bounded nonnegative price."; return false; }
            if (!client.NativeStorage.TryObserve(firstBuildingId, out var first, out reason) ||
                !client.NativeStorage.TryObserve(secondBuildingId, out var second, out reason)) return false;
            originalChanges = new[] { new NativeStorageChange(armor, first, 50), new NativeStorageChange(armor, second, 50) };
            // For an in-world retry call TryExchangeBatch directly with these ORIGINAL changes,
            // the same sequence and aggregate price. Do not reobserve stores or allocate a new ID.
            return client.NativeStorage.TryExchangeBatch(sequence, originalChanges, -(int)price, out status, out reason);
        }
    }
}
