using System;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal static unsafe partial class WeaponProductionNativeRuntime
    {
        private sealed partial class Adapter
        {
            private int FilterIngredientThreshold(int vanillaAmount)
            {
                try
                {
                    if (TryGetWorkshop(role, CurrentUnit, out GameUnit* worker, out GameBuilding* building))
                        return WeaponProductionRuntime.GetIngredientAmount(worker->r_LinkedProductionBuildingId,
                            building->r_GlobalId, vanillaAmount);
                }
                catch (Exception error) { Report(error); }
                return vanillaAmount;
            }

            private int UseBufferedWood(int vanillaAmount)
            {
                try
                {
                    if (TryGetWorkshop(role, CurrentUnit, out GameUnit* worker, out GameBuilding* building))
                    {
                        int requested = WeaponProductionRuntime.GetIngredientAmount(worker->r_LinkedProductionBuildingId,
                            building->r_GlobalId, vanillaAmount);
                        return requested > 0 && building->r_WoodPlanksAmount >= (uint)requested ? 1 : 0;
                    }
                }
                catch (Exception error) { Report(error); }
                return 0;
            }

            private int ConsumeWood(int _)
            {
                try
                {
                    int unitId = CurrentUnit;
                    if (TryGetWorkshop(role, unitId, out GameUnit* worker, out GameBuilding* building))
                    {
                        int home = worker->r_LinkedProductionBuildingId;
                        if (WeaponProductionRuntime.TryGetBatch(home, building->r_GlobalId, unitId, worker->r_GlobalId,
                            out WeaponProductionOption option) && option.IngredientAmount is int required)
                        {
                            if (building->r_WoodPlanksAmount >= (uint)required)
                                return checked((int)(building->r_WoodPlanksAmount - (uint)required));

                            // Vanilla's no-wood continuation returns to gathering. A batch
                            // that could not debit its planks can reconsider the next choice.
                            WeaponProductionRuntime.AbortUnchargedBatch(home, building->r_GlobalId, unitId, worker->r_GlobalId);
                            return -1;
                        }
                    }
                }
                catch (Exception error) { Report(error); }
                return WeaponProductionNativeDefinition.IngredientVanilla;
            }
        }
    }
}
