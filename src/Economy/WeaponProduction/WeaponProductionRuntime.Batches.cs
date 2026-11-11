using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.Networking;
using APIShared.Simulation;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    internal static unsafe partial class WeaponProductionRuntime
    {
        internal static bool TryGetOrLatch(int id, uint gid, int unitId, uint unitGid, out WeaponProductionOption option)
        {
            option = null;
            if (!HasHudDemand || unitId <= 0 || unitId > MaxUnitId || unitGid == 0 || !TryBuilding(id, gid, out var building)) return false;
            var state = FindState(id, gid, building->r_BuildingType);
            if (state == null) return false;
            var existing = FindBatch(id, gid, unitId, unitGid);
            if (state.Current?.Phase == BatchPhase.LocalProduct && HasBufferedProduct(building, state.Current.Option))
            {
                if (!TryAdoptFinishedBatch(id, gid, unitId, unitGid, out option)) return false;
                ResetManufacturing(id, gid, unitId, unitGid);
                return true;
            }
            if (existing?.Phase == BatchPhase.Manufacturing) { option = existing.Option; return true; }
            option = state.Next;
            if (!IsOptionAllowed(option)) { option = null; return false; }
            if (HasNativeSelection(building->r_BuildingType) && ReadGood(building, currentOffset) != option.TemplateGood)
            {
                // A raw SE field setter can bypass the Chore publisher. Its
                // different native template takes precedence at the next latch.
                state.Next = null;
                UpdateDemand(id, state);
                option = null;
                return false;
            }
            var batch = new Batch { BuildingId = id, BuildingGlobalId = gid, UnitId = unitId, UnitGlobalId = unitGid, Option = option };
            workers[unitId] = batch;
            state.Current = batch;
            PublishWorker(batch);
            PublishBuilding(id, state);
            return true;
        }

        internal static bool TryAdoptFinishedBatch(int id, uint gid, int unitId, uint unitGid, out WeaponProductionOption option)
        {
            option = null;
            if (unitId <= 0 || unitId > MaxUnitId || unitGid == 0 || !TryBuilding(id, gid, out var building)) return false;
            var state = FindState(id, gid, building->r_BuildingType);
            if (state?.Current?.Phase != BatchPhase.LocalProduct) return false;
            var previous = state.Current;
            option = previous.Option;
            if (FindBatch(id, gid, unitId, unitGid) == previous) return true;
            if (workers.TryGetValue(previous.UnitId, out var prior) && ReferenceEquals(prior, previous))
            {
                workers.Remove(previous.UnitId);
                WorkerWire(previous.UnitId).Clear();
                ((ushort*)WorkerWireKindsAddress)[previous.UnitId] = 0;
            }
            var batch = new Batch
            {
                BuildingId = id, BuildingGlobalId = gid, UnitId = unitId, UnitGlobalId = unitGid,
                Option = option, Phase = BatchPhase.LocalProduct
            };
            state.Current = workers[unitId] = batch;
            PublishWorker(batch);
            PublishBuilding(id, state);
            return true;
        }

        internal static void MarkLocalProduct(int id, uint gid, int unitId, uint unitGid) =>
            SetPhase(id, gid, unitId, unitGid, BatchPhase.LocalProduct);

        internal static void MarkCargo(int id, uint gid, int unitId, uint unitGid)
        {
            var batch = FindBatch(id, gid, unitId, unitGid);
            if (batch == null) return;
            var state = FindState(id, gid, batch.Option.BuildingType);
            if (batch.Phase == BatchPhase.LocalProduct && state?.Current == batch &&
                TryBuilding(id, gid, out var building) && HasBufferedProduct(building, batch.Option))
            {
                // The native building can retain several finished pieces while
                // the worker delivers one. The local lot and the trip can outlive
                // one another, so keep separate provenance until both drain.
                state.Current = new Batch
                {
                    BuildingId = id, BuildingGlobalId = gid, UnitId = unitId, UnitGlobalId = unitGid,
                    Option = batch.Option, Phase = BatchPhase.LocalProduct
                };
                PublishBuilding(id, state);
            }
            SetPhase(id, gid, unitId, unitGid, BatchPhase.Cargo);
        }

        private static bool HasBufferedProduct(GameBuilding* building, WeaponProductionOption option)
        {
            if (option.BuildingType == eStructs.STRUCT_ARMOURERS_WORKSHOP) return building->r_ArmouryMetalArmorAmount != 0;
            if (option.BuildingType != eStructs.STRUCT_BLACKSMITHS_WORKSHOP) return false;
            return option.TemplateGood == eGoods.STORED_SWORDS
                ? building->r_ArmourySwordsAmount != 0
                : building->r_ArmouryMacesAmount != 0;
        }

        private static void SetPhase(int id, uint gid, int unitId, uint unitGid, BatchPhase phase)
        {
            var batch = FindBatch(id, gid, unitId, unitGid);
            if (batch == null || batch.Phase == phase) return;
            batch.Phase = phase;
            PublishWorker(batch);
            var state = FindState(id, gid, batch.Option.BuildingType);
            if (state != null && ReferenceEquals(state.Current, batch)) PublishBuilding(id, state);
        }

        internal static bool TryGetBatch(int id, uint gid, int unitId, uint unitGid, out WeaponProductionOption option)
        {
            var batch = FindBatch(id, gid, unitId, unitGid);
            option = batch?.Option;
            return option != null;
        }

        private static WeaponProductionOption GetPendingIngredientOption(BuildingState state, GameBuilding* building)
        {
            var option = state?.Next;
            return IsOptionAllowed(option) &&
                (!HasNativeSelection(state.Type) || ReadGood(building, nextOffset) == option.TemplateGood) ? option : null;
        }

        internal static int GetIngredientAmount(int id, uint gid, int vanillaAmount)
        {
            if (!TryBuilding(id, gid, out var building)) return vanillaAmount;
            var state = FindState(id, gid, building->r_BuildingType);
            return GetPendingIngredientOption(state, building)?.IngredientAmount ?? vanillaAmount;
        }

        internal static bool TryGetIngredientOption(int id, uint gid, int unitId, uint unitGid, out WeaponProductionOption option)
        {
            option = null;
            if (!TryBuilding(id, gid, out var building)) return false;
            var state = FindState(id, gid, building->r_BuildingType);
            var current = state?.Current;
            if (current?.Phase == BatchPhase.LocalProduct && HasBufferedProduct(building, current.Option))
                option = current.Option;
            else
            {
                var batch = FindBatch(id, gid, unitId, unitGid);
                if (batch?.Phase == BatchPhase.Manufacturing)
                    option = batch.Option;
                // The blacksmith has already latched by the time it debits
                // iron. The fixed-good workshops latch after the debit.
                else if (state != null && !HasNativeSelection(state.Type))
                    option = GetPendingIngredientOption(state, building);
            }
            return option != null;
        }

        internal static void AbortUnchargedBatch(int id, uint gid, int unitId, uint unitGid)
        {
            if (FindBatch(id, gid, unitId, unitGid)?.Phase == BatchPhase.Manufacturing)
                CompleteBatch(id, gid, unitId, unitGid);
        }

        internal static void CompleteBatch(int id, uint gid, int unitId, uint unitGid)
        {
            var batch = FindBatch(id, gid, unitId, unitGid);
            if (batch == null) return;
            workers.Remove(unitId);
            WorkerWire(unitId).Clear();
            ((ushort*)WorkerWireKindsAddress)[unitId] = 0;
            var state = FindState(id, gid, batch.Option.BuildingType);
            if (state != null && ReferenceEquals(state.Current, batch))
            { state.Current = null; UpdateDemand(id, state); }
        }

        internal static bool CheckManufacturing(int id, uint gid, int unitId, uint unitGid, bool enabled, bool originalDone)
        {
            var batch = FindBatch(id, gid, unitId, unitGid);
            int? duration = batch?.Option.WorkTicks;
            if (!duration.HasValue) return originalDone;
            if (!enabled) { ResetManufacturing(id, gid, unitId, unitGid); return false; }
            if (engine == null || !Monitor.IsEntered(engine) || !SimulationTickSource.TryReadAdmittedNativeTick(out int tick))
            {
                WorkerProductionWorldSafety.Fault("Weapon-workshop manufacturing cannot read its native map tick.");
                return false;
            }
            if ((!batch.HasTick || batch.LastTick != tick) && batch.Elapsed < duration.Value)
            { batch.LastTick = tick; batch.HasTick = true; batch.Elapsed++; PublishWorker(batch); }
            return batch.Elapsed >= duration.Value;
        }

        internal static void ResetManufacturing(int id, uint gid, int unitId, uint unitGid)
        {
            var batch = FindBatch(id, gid, unitId, unitGid);
            if (batch != null && batch.HasTick)
            { batch.Elapsed = 0; batch.LastTick = 0; batch.HasTick = false; PublishWorker(batch); }
        }
    }
}
