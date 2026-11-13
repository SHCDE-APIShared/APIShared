using System;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal enum WeaponProductionNativeOperation
    {
        Latch, LatchAfterCall, Manufacturing, StageLocalProduct, RecoverLocalProduct, Yield, Deposit, Complete,
        IngredientThreshold, IngredientFindWood, IngredientConsumeWood,
        IngredientWorkshopDebit, IngredientIronStock, IngredientTannerStock, IngredientTannerGrant
    }

    internal sealed class WeaponProductionNativeSite
    {
        internal WeaponProductionNativeSite(WeaponProductionNativeOperation operation, int rva, string bytes, string next,
            bool filterBeforeAdjacentCall = false, int alternativeRva = 0)
        {
            Operation = operation;
            Rva = rva;
            Bytes = WorkerProductionBonusNativeDefinition.Hex(bytes);
            Next = WorkerProductionBonusNativeDefinition.Hex(next);
            FilterBeforeAdjacentCall = filterBeforeAdjacentCall;
            AlternativeRva = alternativeRva;
        }

        internal WeaponProductionNativeOperation Operation { get; }
        internal int Rva { get; }
        internal byte[] Bytes { get; }
        internal byte[] Next { get; }
        internal bool FilterBeforeAdjacentCall { get; }
        internal int AlternativeRva { get; }

        internal int CallTarget => FilterBeforeAdjacentCall ? 0 : Operation == WeaponProductionNativeOperation.Deposit ||
            Operation == WeaponProductionNativeOperation.StageLocalProduct || Operation == WeaponProductionNativeOperation.RecoverLocalProduct
            ? WeaponProductionNativeDefinition.AddGoodRva :
            Operation == WeaponProductionNativeOperation.Yield ? WeaponProductionNativeDefinition.YieldRva :
            Operation == WeaponProductionNativeOperation.IngredientFindWood ? WeaponProductionNativeDefinition.FindStockRva :
            Operation == WeaponProductionNativeOperation.Manufacturing || Operation == WeaponProductionNativeOperation.LatchAfterCall
                ? WeaponProductionNativeDefinition.CycleRva : 0;
    }

    internal sealed class WeaponProductionNativeRole
    {
        internal WeaponProductionNativeRole(eStructs building, eChimps worker, int workerRva, params WeaponProductionNativeSite[] sites)
        {
            Building = building;
            Worker = worker;
            WorkerRva = workerRva;
            Sites = sites;
        }

        internal eStructs Building { get; }
        internal eChimps Worker { get; }
        internal int WorkerRva { get; }
        internal WeaponProductionNativeSite[] Sites { get; }
    }

    // FBCB9319 native instruction and xref audit. All sites end at complete instructions,
    // with no incoming edges into their interiors. The five deposit calls use the same
    // Armoury capacity check (C6EA0), which does not read the requested eGoods argument.
    internal static partial class WeaponProductionNativeDefinition
    {
        internal const int CycleRva = 0xB6240, YieldRva = 0x18D940, AddGoodRva = 0xB6100;
        internal const int MaxBuildingId = 3999, MaxUnitId = 9999;
        internal const int BuildingStride = 0x32C;
        internal const int LinkedBuildingOffset = 0x990;

        internal static readonly WeaponProductionNativeRole[] Roles =
        {
            new WeaponProductionNativeRole(eStructs.STRUCT_FLETCHERS_WORKSHOP, eChimps.CHIMP_TYPE_FLETCHER, 0x12D230,
                S(WeaponProductionNativeOperation.Latch, 0x12D77B, "6641898429EA020000498BCCB805000000", "6641898018090000"),
                S(WeaponProductionNativeOperation.Manufacturing, 0x12D8EE, "448BC6420FBF942390090000488BCDE83E89F8FF", "85C0"),
                S(WeaponProductionNativeOperation.Yield, 0x12D954, "664289BC2320090000664289842318090000E8D5FF0500", "6642898423E0090000"),
                S(WeaponProductionNativeOperation.Deposit, 0x12DD15, "440FBF8C29EA020000488BCDE8DA83F8FF", "48630597258000"),
                S(WeaponProductionNativeOperation.Complete, 0x12DEB6, "664239BC23E00900000F8F16050000", "8BD6")),
            new WeaponProductionNativeRole(eStructs.STRUCT_POLETURNERS_WORKSHOP, eChimps.CHIMP_TYPE_POLETURNER, 0x13AAD0,
                S(WeaponProductionNativeOperation.Latch, 0x13B151, "664289B422200900006641898438EA020000", "E9420C0000"),
                S(WeaponProductionNativeOperation.Manufacturing, 0x13B2A8, "420FBF942390090000488BCF458BC5E884AFF7FF", "85C0"),
                S(WeaponProductionNativeOperation.Yield, 0x13B31D, "664289B42320090000E815260500", "6642898423E0090000"),
                S(WeaponProductionNativeOperation.Deposit, 0x13B6FE, "440FBF8C39EA020000488BCFE8F1A9F7FF", "486305AE4B7F00"),
                S(WeaponProductionNativeOperation.Complete, 0x13B894, "664239B423E00900000F8F07050000", "418BD5")),
            new WeaponProductionNativeRole(eStructs.STRUCT_BLACKSMITHS_WORKSHOP, eChimps.CHIMP_TYPE_BLACKSMITH, 0x13BDD0,
                S(WeaponProductionNativeOperation.Latch, 0x13C2BA, "66898431EA0200004A0FBF8C2390090000", "4C69C12C030000"),
                S(WeaponProductionNativeOperation.Manufacturing, 0x13C449, "448BC7420FBF942390090000488BCEE8E39DF7FF", "85C0"),
                S(WeaponProductionNativeOperation.RecoverLocalProduct, 0x13C54E, "440FBF8C31EA020000488BCEE8A19BF7FF", "4863055E3D7F00"),
                S(WeaponProductionNativeOperation.Yield, 0x13C5B0, "664289842318090000E882130500", "6642898423E0090000"),
                S(WeaponProductionNativeOperation.Deposit, 0x13C88C, "440FBF8C31EA020000488BCEE86398F7FF", "486305203A7F00"),
                S(WeaponProductionNativeOperation.Complete, 0x13CA2E, "664639AC23E00900000F8FC5040000", "8BD7")),
            new WeaponProductionNativeRole(eStructs.STRUCT_ARMOURERS_WORKSHOP, eChimps.CHIMP_TYPE_ARMOURER, 0x13CF30,
                // Starts after the Extender's produce context hook and before the same mode-2 call.
                S(WeaponProductionNativeOperation.LatchAfterCall, 0x13D6E6, "488BCBE8528BF7FF486305CF2B7F00", "4869C890040000"),
                S(WeaponProductionNativeOperation.Manufacturing, 0x13D5B9, "488D1DF0F53806488BCBE8788CF7FF", "85C0"),
                S(WeaponProductionNativeOperation.StageLocalProduct, 0x13D602, "448B841934010000488BCBE8EE8AF7FF", "486305AB2C7F00"),
                S(WeaponProductionNativeOperation.RecoverLocalProduct, 0x13D71A, "41B918000000E8DB89F7FF486305982B7F00", "4533C0"),
                S(WeaponProductionNativeOperation.Yield, 0x13D77D, "664289AC2B20090000E8B5010500", "664289842BE0090000"),
                S(WeaponProductionNativeOperation.Deposit, 0x13DA07, "468B8429F8060000488BCBE8E986F7FF", "486305A6287F00"),
                S(WeaponProductionNativeOperation.Complete, 0x13DB8C, "664339AC2EE00900000F8FEA040000", "8BD6")),
            new WeaponProductionNativeRole(eStructs.STRUCT_TANNERS_WORKSHOP, eChimps.CHIMP_TYPE_TANNER, 0x13E0B0,
                S(WeaponProductionNativeOperation.LatchAfterCall, 0x13ECD6, "420FBF942190090000488BCEE85975F7FF", "486305D6157F00"),
                S(WeaponProductionNativeOperation.Manufacturing, 0x13EB8C, "420FBF942390090000488BCE458BC5E8A076F7FF", "85C0"),
                // Keep the adjacent ImprovedHunters call/route hook reachable. Its meat
                // branch supplies its own EDX/R9D; the armoury branch keeps these inputs.
                new WeaponProductionNativeSite(WeaponProductionNativeOperation.Yield, 0x13EBE1,
                    "4869D990040000458BC5418BD5664289BC2320090000", "E844ED0400", filterBeforeAdjacentCall: true),
                new WeaponProductionNativeSite(WeaponProductionNativeOperation.Deposit, 0x13EF22,
                    "C74424283200000044896C2420420FBF94218E090000", "468B8421F8060000", filterBeforeAdjacentCall: true),
                S(WeaponProductionNativeOperation.Complete, 0x13F0BC, "664239BC21E00900000F8FF7F5FFFF", "664689AC21CC090000"))
        };

        private static WeaponProductionNativeSite S(WeaponProductionNativeOperation operation, int rva, string bytes, string next) =>
            new WeaponProductionNativeSite(operation, rva, bytes, next);
    }
}
