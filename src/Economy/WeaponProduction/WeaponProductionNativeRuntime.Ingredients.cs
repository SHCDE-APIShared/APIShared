using System;
using System.Runtime.InteropServices;
using SHCDESE.Interop;
using static APIShared.Economy.WeaponProductionNativeDefinition;

namespace APIShared.Economy
{
    internal static unsafe partial class WeaponProductionNativeRuntime
    {
        private const int TannerCowHides = 3, TannerStockCapacity = 5;
        private const ushort FindMaterialsState = 1, PrepareMaterialsState = 6, FadeState = 0x6D;
        private static readonly byte[] TannerVanillaYieldCall = { 0xE8, 0x44, 0xED, 0x04, 0x00 };
        private static int ingredientNextOffset;
        private static WorkshopCycleCall workshopCycle;
        private static SmithBeginLocalWork smithBeginLocalWork;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int WorkshopCycleCall(IntPtr manager, int buildingId, int mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SmithBeginLocalWork(IntPtr manager, int unitId);

        // ImprovedHunters changes the relationship between stored hides and the
        // number of meat deliveries at this call. The native three-hide contract
        // is required for recipes which consume more than one hide per work cycle.
        internal static bool CanOverrideTannerIngredients => module != IntPtr.Zero &&
            new ReadOnlySpan<byte>((void*)(module + TannerVanillaYieldCallRva), TannerVanillaYieldCall.Length)
                .SequenceEqual(TannerVanillaYieldCall);

        private sealed partial class Adapter
        {
            private bool TryCreateWorkshopIngredientCallback(WeaponProductionNativeOperation operation, out Delegate callback)
            {
                switch (operation)
                {
                    case WeaponProductionNativeOperation.IngredientIronStock:
                        if (Offset<GameUnit>(nameof(GameUnit.r_CurrentTilePositionX)) != 0xC0 ||
                            Offset<GameUnit>(nameof(GameUnit.r_CurrentTilePositionY)) != 0xC2 ||
                            Offset<GameUnit>(nameof(GameUnit.r_LinkedProductionBuildingTileEndX)) != 0x336 ||
                            Offset<GameUnit>(nameof(GameUnit.r_LinkedProductionBuildingTileEndY)) != 0x338 ||
                            Offset<GameUnit>(nameof(GameUnit.UnknownRelevant2)) != 0x32A ||
                            Offset<GameUnit>(nameof(GameUnit.UnknownRelevant2_2)) != 0x32B)
                            throw new InvalidOperationException("The iron workshop work tile or transition layout differs.");
                        if (role.Building == eStructs.STRUCT_BLACKSMITHS_WORKSHOP)
                            smithBeginLocalWork = Marshal.GetDelegateForFunctionPointer<SmithBeginLocalWork>(module + SmithBeginLocalWorkRva);
                        callback = (Filter)UseBufferedIron;
                        return true;
                    case WeaponProductionNativeOperation.IngredientWorkshopDebit:
                        if (Offset<GameBuilding>(nameof(GameBuilding.r_CowHidesAmount)) != 0x134 ||
                            Offset<GameBuilding>(nameof(GameBuilding.r_IronIngotsAmount)) != 0x138 ||
                            Offset<GameUnit>(nameof(GameUnit.r_AnimationTimer)) != 0x2AC ||
                            Offset<GameUnit>(nameof(GameUnit.r_AIState)) != 0x2BC ||
                            Offset<GameUnit>(nameof(GameUnit.r_ResumeAIState)) != 0x2BE ||
                            Offset<GameUnit>(nameof(GameUnit.r_TimeSinceDeathTicker)) != 0x2C4)
                            throw new InvalidOperationException("Weapon production ingredient stock or retry state layout differs.");
                        callback = (Filter)DebitWorkshopIngredients;
                        return true;
                    case WeaponProductionNativeOperation.IngredientTannerStock:
                        callback = (Filter)CheckTannerIngredients;
                        return true;
                    case WeaponProductionNativeOperation.IngredientTannerGrant:
                        callback = (Filter)AdjustTannerGrantCapacity;
                        return true;
                    default:
                        callback = null;
                        return false;
                }
            }

            private int DebitWorkshopIngredients(int phase)
            {
                try
                {
                    if (phase < 0)
                        throw new InvalidOperationException("The game rejected a weapon ingredient debit after checking local stock.");
                    int unitId = CurrentUnit;
                    if (!TryGetWorkshop(role, unitId, out GameUnit* worker, out GameBuilding* building))
                        return IngredientVanilla;
                    int id = worker->r_LinkedProductionBuildingId;
                    uint gid = building->r_GlobalId, unitGid = worker->r_GlobalId;
                    // The armourer shares one local output slot across all recipes.
                    // Recover its finished lot before a new recipe can claim that slot.
                    if (role.Building == eStructs.STRUCT_ARMOURERS_WORKSHOP && HasWorkshopProduct(building))
                        return IngredientRecover;
                    if (!WeaponProductionRuntime.TryGetIngredientOption(id, gid, unitId, unitGid, out WeaponProductionOption option) ||
                        !option.IngredientAmount.HasValue || option.IngredientAmount.Value == 1)
                        return IngredientVanilla;

                    bool tanner = role.Building == eStructs.STRUCT_TANNERS_WORKSHOP;
                    int stock = unchecked((int)(tanner ? building->r_CowHidesAmount : building->r_IronIngotsAmount));
                    int amount = option.IngredientAmount.Value;
                    if (stock >= amount) return amount;
                    if (!tanner && HasWorkshopProduct(building)) return IngredientRecover;

                    // The native recovery route creates cargo even when its decrement
                    // fails. With no product, clean up the cycle and request materials.
                    if (worker->r_AIState != PrepareMaterialsState)
                        throw new InvalidOperationException("The weapon worker left its audited debit state.");
                    WeaponProductionRuntime.AbortUnchargedBatch(id, gid, unitId, unitGid);
                    if (workshopCycle == null)
                        workshopCycle = Marshal.GetDelegateForFunctionPointer<WorkshopCycleCall>(module + CycleRva);
                    workshopCycle(module + WorkerProductionBonusNativeDefinition.BuildingManagerRva, id, 0);
                    worker->r_AnimationTimer = 0;
                    // Living workers use this 16-bit slot as the workshop's state timer.
                    worker->r_TimeSinceDeathTicker = 0;
                    worker->r_ResumeAIState = 0;
                    worker->r_AIState = FindMaterialsState;
                    return IngredientRetry;
                }
                catch (Exception error) { Report(error); return IngredientRetry; }
            }

            private int UseBufferedIron(int unused)
            {
                bool started = false;
                try
                {
                    int unitId = CurrentUnit;
                    if (!TryGetWorkshop(role, unitId, out GameUnit* worker, out GameBuilding* building) ||
                        worker->r_AIState != FindMaterialsState ||
                        worker->r_CurrentTilePositionX != worker->r_LinkedProductionBuildingTileEndX ||
                        worker->r_CurrentTilePositionY != worker->r_LinkedProductionBuildingTileEndY)
                        return 0;
                    int id = worker->r_LinkedProductionBuildingId;
                    int amount = WeaponProductionRuntime.GetIngredientAmount(id, building->r_GlobalId, 1);
                    if (unchecked((int)building->r_IronIngotsAmount) < amount) return 0;

                    // State 1 already cleared carry and the animation timer. Match
                    // the native state-3 arrival without granting another iron bar.
                    started = true;
                    worker->r_TimeSinceDeathTicker = 0;
                    if (role.Building == eStructs.STRUCT_BLACKSMITHS_WORKSHOP)
                    {
                        worker->r_AIState = PrepareMaterialsState;
                        *(short*)((byte*)building + currentOffset) = *(short*)((byte*)building + ingredientNextOffset);
                        ObserveLatch();
                        smithBeginLocalWork(module + WorkerProductionBonusNativeDefinition.UnitManagerRva, unitId);
                    }
                    else
                    {
                        worker->r_AIState = FadeState;
                        worker->r_ResumeAIState = PrepareMaterialsState;
                        worker->UnknownRelevant2 = 0;
                        worker->UnknownRelevant2_2 = 2;
                    }
                    return 1;
                }
                catch (Exception error) { Report(error); return started ? 1 : 0; }
            }

            private bool HasWorkshopProduct(GameBuilding* building)
            {
                if (role.Building == eStructs.STRUCT_ARMOURERS_WORKSHOP)
                    return unchecked((int)building->r_ArmouryMetalArmorAmount) > 0;
                switch ((eGoods)(*(ushort*)((byte*)building + currentOffset)))
                {
                    case eGoods.STORED_SWORDS: return unchecked((int)building->r_ArmourySwordsAmount) > 0;
                    case eGoods.STORED_MACES: return unchecked((int)building->r_ArmouryMacesAmount) > 0;
                    default: return false;
                }
            }

            private int CheckTannerIngredients(int unused)
            {
                try
                {
                    int unitId = CurrentUnit;
                    if (!TryGetWorkshop(role, unitId, out GameUnit* worker, out GameBuilding* building))
                        return IngredientVanilla;
                    int required = WeaponProductionRuntime.GetIngredientAmount(worker->r_LinkedProductionBuildingId,
                        building->r_GlobalId, 1);
                    if (required == 1) return IngredientVanilla;
                    return unchecked((int)building->r_CowHidesAmount) >= required ? 1 : 0;
                }
                catch (Exception error) { Report(error); return IngredientVanilla; }
            }

            private int AdjustTannerGrantCapacity(int unused)
            {
                try
                {
                    if (!WeaponProductionRuntime.HasTannerIngredientOverrides ||
                        !TryGetWorkshop(role, CurrentUnit, out _, out GameBuilding* building))
                        return 0;
                    uint stock = building->r_CowHidesAmount;
                    // A hunt starting below a recipe's 2 or 3 hide requirement can
                    // bring one cow back to 1 or 2 remaining hides, including on a switch.
                    return stock > 0 && stock < TannerCowHides ? TannerStockCapacity : 0;
                }
                catch (Exception error) { Report(error); return 0; }
            }
        }
    }
}
