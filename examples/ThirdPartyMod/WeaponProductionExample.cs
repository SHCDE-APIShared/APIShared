using APIShared.Economy;
using SHCDESE.Interop;

namespace ThirdPartyMod
{
    // Call once during Unity startup on every peer. The shared building HUD exposes
    // the recipe once it is admitted into a mission. This example is opt-in.
    internal static class WeaponProductionExample
    {
        internal static bool Register(string localizedName, out WeaponProductionRegistration registration, out string reason)
        {
            var option = new WeaponProductionOptionDefinition(
                eStructs.STRUCT_FLETCHERS_WORKSHOP, eGoods.STORED_CROSSBOWS,
                eGoods.STORED_CROSSBOWS, localizedName, baseAmount: 2, workTicks: 60, priority: 10)
                .WithIngredientAmount(4);
            return WeaponProduction.TryRegister(ExamplePlugin.Guid, "fletcher-crossbow-batch", option,
                out registration, out reason);
        }
    }
}
