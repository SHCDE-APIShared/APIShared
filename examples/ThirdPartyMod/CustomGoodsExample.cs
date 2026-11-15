using APIShared.Goods;

namespace ThirdPartyMod
{
    // One instance per authoritative mission. The consumer supplies already-verified simulation
    // events/IDs and owns mission replacement, native locking and network command agreement.
    internal sealed class CustomGoodsExample
    {
        private readonly CustomGoodsInventory inventory = new CustomGoodsInventory();
        private readonly CustomGoodsClient goods;
        private readonly CustomGoodId armor;

        internal CustomGoodsExample(string ownerGuid)
        {
            goods = inventory.ForOwner(ownerGuid);
            if (!goods.TryRegister("medium-armor", 200, out armor, out string reason))
                throw new System.InvalidOperationException(reason);
        }

        // A shared authoritative sequencer assigns this delivery's stable positive sequence.
        // Retained retries return AlreadyApplied; expired retries never duplicate goods.
        internal GoodsTransactionStatus AcceptVerifiedDelivery(int playerId, long deliverySequence, int quantity)
        {
            if (quantity <= 0) return GoodsTransactionStatus.InvalidRequest;
            return goods.Apply(deliverySequence, new[] { new GoodsChange(armor, playerId, quantity) });
        }

        // An existing native purchase adapter must own payment/creation/confirmation. A hold
        // prevents another virtual-stock consumer spending the equipment meanwhile. Commit only
        // after confirmed native success; Release on confirmed cancellation. Neither action
        // makes native gold/payment atomic with this ledger or rolls back a native recruit.
        internal GoodsTransactionStatus HoldEquipment(int playerId, long orderSequence, out GoodsReservation hold) =>
            goods.Reserve(orderSequence, new[] { new GoodsChange(armor, playerId, -1) }, out hold);

        // Store these bytes in one existing VersionedSaveData entry. On a rejected load, keep
        // the loaded bytes and disable mutation until migrated; do not save empty replacement data.
        internal byte[] Capture() => inventory.Capture();
        internal bool Restore(byte[] data, out string reason) => inventory.TryRestore(data, out reason);
    }
}
