using APIShared;
using APIShared.Economy;
using APIShared.Savegames;
using System.Collections.Generic;
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

        // Alternative registration for a legacy producer whose native elapsed progress
        // must continue. The caller registers/enrolls storage first; this adds no hook.
        internal static bool TryRegisterNativeTimed(ModApiClient client, NativeStoredGood armour,
            string localizedName, out WeaponProductionRegistration registration, out string reason) =>
            WeaponProduction.TryRegister(client.OwnerGuid, "legacy-compatible-armour",
                new WeaponProductionOptionDefinition(eStructs.STRUCT_ARMOURERS_WORKSHOP,
                    eGoods.STORED_METAL_ARMOUR, eGoods.STORED_METAL_ARMOUR, localizedName,
                    baseAmount: 2, workTicks: null).WithIngredientAmount(1).WithStoredOutput(armour),
                out registration, out reason);

        // Call only inside this participant's exact Prepared callback after fully decoding
        // its legacy record and staging the physical import. Rows assert historical payment;
        // current native state alone is not that proof. No actor map is retired here.
        internal static bool TryStagePaidWork(ModApiClient client, CriticalSaveParticipant participant,
            VersionedSaveLoad load, NativeStorageImportPlan storageImport,
            IReadOnlyList<WeaponProductionPaidManufacturing> decodedPaidActors,
            out WeaponProductionManufacturingAdoption adoption, out string reason) =>
            WeaponProduction.TryPrepareManufacturingAdoption(client.OwnerGuid, participant, load,
                storageImport, decodedPaidActors, out adoption, out reason);
    }
}
