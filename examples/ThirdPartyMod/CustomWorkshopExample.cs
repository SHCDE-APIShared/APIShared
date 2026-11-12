using APIShared;
using APIShared.Economy;
using SHCDESE.Interop;

namespace ThirdPartyMod
{
    internal static class CustomWorkshopExample
    {
        // Called explicitly on initialized Unity startup before the first mission.
        // This compiled example is not automatically activated by ExamplePlugin.
        internal static bool TryRegister(ModApiClient client, string localizedName,
            out WeaponProductionRegistration registration, out string reason)
        {
            registration = null;
            if (!client.NativeStorage.TryRegisterGood("alternate-armour", NativeStoragePool.Armory,
                    out var armour, out reason)) return false;
            var definition = new WeaponProductionOptionDefinition(eStructs.STRUCT_ARMOURERS_WORKSHOP,
                eGoods.STORED_METAL_ARMOUR, eGoods.STORED_METAL_ARMOUR, localizedName,
                baseAmount: 2, workTicks: 60)
                .WithIngredientAmount(1)
                .WithStoredOutput(armour);
            return WeaponProduction.TryRegister(client.OwnerGuid, "alternate-armour", definition,
                out registration, out reason);
        }
    }
}
