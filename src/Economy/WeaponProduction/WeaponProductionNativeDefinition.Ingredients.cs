using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal static partial class WeaponProductionNativeDefinition
    {
        internal const int IngredientVanilla = int.MinValue, IngredientRecover = -1, IngredientRetry = 0;
        internal const int SmithIngredientDebitRva = 0x13C4E2, SmithIngredientRetryRva = 0x13CF02,
            ArmourerIngredientDebitRva = 0x13D6AC, ArmourerIngredientRetryRva = 0x13E085,
            TannerIngredientDebitRva = 0x13EC84, TannerIngredientRetryRva = 0x13E6C2,
            TannerNativeDeltaRva = 0x13ECAD, TannerVanillaYieldCallRva = 0x13EBF7,
            SmithIngredientStockRva = 0x13BEE2, ArmourerIngredientStockRva = 0x13D05D,
            SmithBeginLocalWorkRva = 0x196F80,
            IronManagerStockOffset = 0x194, TannerManagerStockOffset = 0x190;

        private static WeaponProductionNativeSite[] AppendWorkshopIngredientSites(eStructs building, WeaponProductionNativeSite[] sites)
        {
            switch (building)
            {
                case eStructs.STRUCT_BLACKSMITHS_WORKSHOP:
                    return new[]
                    {
                        new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientIronStock,
                            SmithIngredientStockRva, "8D5705E896D3F7FF48630DD3437F00", "448BC7",
                            alternativeRva: SmithIngredientRetryRva),
                        new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientWorkshopDebit,
                            SmithIngredientDebitRva, "448B8431340100004439AC31940100007E5A41B906000000488BCEE8FE9BF7FF",
                            "486305BB3D7F00", alternativeRva: 0x13C54E)
                    };
                case eStructs.STRUCT_ARMOURERS_WORKSHOP:
                    return new[]
                    {
                        new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientIronStock,
                            ArmourerIngredientStockRva, "448BC6488D1D49FB3806488BCBE811C2F7FF", "4C63F0",
                            alternativeRva: ArmourerIngredientRetryRva),
                        new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientWorkshopDebit,
                            ArmourerIngredientDebitRva, "448B84193401000083BC199401000000488BCB7E5941B906000000E8348AF7FF",
                            "486305F12B7F00", alternativeRva: 0x13D71A)
                    };
                case eStructs.STRUCT_TANNERS_WORKSHOP:
                    return new[]
                    {
                        new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientTannerStock, 0x13E408,
                            "4869C82C03000039BC31900100007E1C", "42C78423180900006D000600", alternativeRva: 0x13E434),
                        new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientTannerGrant, 0x13EA38,
                            "4C69C22C030000458B843034010000E8B476F7FF", "48630571187F00"),
                        new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientWorkshopDebit,
                            TannerIngredientDebitRva, "488D3525DF380683BC3190010000007E7941B8FFFFFFFF44896C2430C744242830000000BB050000004489442420448BCB448B843134010000488BCEE83B74F7FF",
                            "486305F8157F00", alternativeRva: 0x13ED0E)
                    };
                default:
                    return sites;
            }
        }
    }
}
