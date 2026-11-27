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

        // Opt-in read from an existing callback that already holds the actual engine monitor.
        // Keep art/role policy in the consumer; a false read retains its ordinary presentation.
        // This reads no quantity and must not become a second worker/cargo registry.
        internal static bool TryReadCarrierForPresentation(int unitId, uint globalId, NativeStoredGood registeredOutput,
            out WeaponProductionWorkerBatch batch)
        {
            batch = default;
            if (registeredOutput == null || !WeaponProduction.TryGetWorkerBatch(unitId, globalId, out var observed) ||
                observed.Phase != WeaponProductionBatchPhase.Cargo ||
                !object.ReferenceEquals(observed.Option.StoredOutput, registeredOutput)) return false;
            batch = observed; return true;
        }
    }
}
