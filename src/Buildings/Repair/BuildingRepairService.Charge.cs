using APIShared.Presentation;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using Noesis;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Marshal = System.Runtime.InteropServices.Marshal;

namespace APIShared
{
    internal sealed unsafe partial class BuildingRepairService
    {
        private void OnBuildingRepair(BuildingRepairEventArgs args)
        {
            if (args.Phase == EventHookPhase.Pre)
            {
                pendingRepair = null;
                if (Volatile.Read(ref activeOwners) == 0 || args.SkipOriginalFunction) return;
                try
                {
                    GameBuildingManagerAPI buildings = GameBuildingManagerAPI.Instance;
                    GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
                    RepairRejectionReason rejection = ValidateRepair(buildings, players, args,
                        out GameBuilding* building, out RepairRejectionSnapshot snapshot);
                    if (rejection != RepairRejectionReason.None)
                    {
                        args.SkipOriginalFunction = true;
                        RecordRepairRejection(rejection, snapshot);
                        return;
                    }
                    if (building->r_CurrentHealth >= building->r_MaxHealth) return;

                    eStructs type = building->r_BuildingType;
                    if (!Buildings.ConstructionCosts.TryGetCurrent(Buildings.ConstructionCostTarget.FromStructure(type), out var constructionCost, out string costReason))
                        throw new InvalidOperationException("Building repair cost safety unavailable: " + costReason);
                    int baseIron = constructionCost.Iron;
                    int basePitch = constructionCost.Pitch;
                    int baseGold = constructionCost.Gold;
                    if (baseIron < 0 || basePitch < 0 || baseGold < 0)
                    {
                        args.SkipOriginalFunction = true;
                        RecordError("Building repair has an invalid negative construction cost; native repair skipped.");
                        return;
                    }
                    int iron = RepairCost(baseIron, building->r_CurrentHealth, building->r_MaxHealth);
                    int pitch = RepairCost(basePitch, building->r_CurrentHealth, building->r_MaxHealth);
                    int gold = RepairCost(baseGold, building->r_CurrentHealth, building->r_MaxHealth);
                    if (!players.HasGoodsAmount(args.PlayerId, eGoods.STORED_IRON_INGOTS, iron) ||
                        !players.HasGoodsAmount(args.PlayerId, eGoods.STORED_PITCH_RAW, pitch) ||
                        !players.HasGoodsAmount(args.PlayerId, eGoods.STORED_GOLD, gold))
                    {
                        args.SkipOriginalFunction = true;
                        return;
                    }

                    pendingRepair = new PendingRepair
                    {
                        PlayerId = args.PlayerId,
                        BuildingId = args.BuildingId,
                        GlobalId = args.BuildingGlobalId,
                        PreviousHealth = building->r_CurrentHealth,
                        Iron = iron,
                        Pitch = pitch,
                        Gold = gold
                    };
                }
                catch (Exception ex)
                {
                    args.SkipOriginalFunction = true;
                    RecordError("Building repair cost check failed closed: " + ex);
                }
                return;
            }

            if (args.Phase != EventHookPhase.Post) return;
            PendingRepair pending = pendingRepair;
            pendingRepair = null;
            if (pending == null || pending.PlayerId != args.PlayerId || pending.BuildingId != args.BuildingId ||
                pending.GlobalId != args.BuildingGlobalId) return;
            try
            {
                GameBuildingManagerAPI buildings = GameBuildingManagerAPI.Instance;
                if (!BuildingAccess.TryGetById(buildings, args.BuildingId, out GameBuilding* building, out _) || building == null ||
                    unchecked((int)building->r_GlobalId) != pending.GlobalId ||
                    pending.PreviousHealth >= building->r_MaxHealth ||
                    building->r_CurrentHealth != building->r_MaxHealth) return;
                GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
                if (pending.Iron > 0) players.RemoveGood(args.PlayerId, eGoods.STORED_IRON_INGOTS, pending.Iron);
                if (pending.Pitch > 0) players.RemoveGood(args.PlayerId, eGoods.STORED_PITCH_RAW, pending.Pitch);
                if (pending.Gold > 0 && !players.AddPlayerGold(args.PlayerId, -pending.Gold))
                    RecordError($"Building repair gold charge could not resolve player {args.PlayerId}.");
            }
            catch (Exception ex)
            {
                RecordError("Building repair additional cost charge failed: " + ex);
            }
        }
    }
}
