using System;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal static partial class WeaponProductionNativeDefinition
    {
        internal const int FindStockRva = 0xB9280;
        internal const int WorkshopPlanksOffset = 0x184;
        internal const int FletcherFindWoodAtHomeRva = 0x12D357;
        internal const int PoleturnerFindWoodAtHomeRva = 0x13ACFF;
        internal const int FletcherConsumeWoodRva = 0x12D9E7;

        private static readonly WeaponProductionNativeSite[] FletcherIngredients =
        {
            S(WeaponProductionNativeOperation.IngredientThreshold, 0x12D755,
                "4A0FBF8422900900004C69C82C03000041398C2984010000", "7C70"),
            new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientConsumeWood, FletcherConsumeWoodRva,
                "83BC2984010000007E3E33FF458BC589BC2984010000", "488BCD", alternativeRva: 0x12DA2F),
            new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientFindWood, FletcherFindWoodAtHomeRva,
                "42888423470A0000460FBF8C23EE060000E813BFF8FF", "48630D502F8000", alternativeRva: 0x12D71B),
            new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientFindWood, 0x12DF50,
                "42888423470A0000460FBF8C23EE060000E81AB3F8FF", "4C63F0", alternativeRva: 0x12DFDE)
        };

        private static readonly WeaponProductionNativeSite[] PoleturnerIngredients =
        {
            S(WeaponProductionNativeOperation.IngredientThreshold, 0x13B10F,
                "4A0FBF8422900900004C69C02C03000041398C3884010000", "7C3F"),
            new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientConsumeWood, 0x13B3A7,
                "39B439840100007E3C89B43984010000", "458BC7", alternativeRva: 0x13B3EC),
            new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientFindWood, PoleturnerFindWoodAtHomeRva,
                "42888423470A0000460FBF8C23EE060000E86BE5F7FF", "48630DA8557F00", alternativeRva: 0x13B0DB),
            new WeaponProductionNativeSite(WeaponProductionNativeOperation.IngredientFindWood, 0x13B906,
                "42888423470A0000460FBF8C23EE060000E864D9F7FF", "4863E8", alternativeRva: 0x13B998)
        };

        internal static WeaponProductionNativeSite[] GetIngredientSites(eStructs building)
        {
            switch (building)
            {
                case eStructs.STRUCT_FLETCHERS_WORKSHOP: return FletcherIngredients;
                case eStructs.STRUCT_POLETURNERS_WORKSHOP: return PoleturnerIngredients;
                default: return AppendWorkshopIngredientSites(building, Array.Empty<WeaponProductionNativeSite>());
            }
        }
    }
}
