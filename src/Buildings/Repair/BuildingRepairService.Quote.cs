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
        private EngineInterface.PlayState CopyStateHook(
            EngineInterface.PlayStateReturnData source, int[] selectedChimps)
        {
            EngineInterface.PlayState state = originalCopy(source, selectedChimps);
            if (Volatile.Read(ref activeOwners) == 0) return state;
            // EngineInterface.run holds threadLock around DLL_RunTick and this copy operation.
            try
            {
                BuildingRepairQuote quote = CaptureSelectedQuote(state);
                if (quote != null)
                {
                    snapshots.Remove(state);
                    snapshots.Add(state, quote);
                }
            }
            catch (Exception ex)
            {
                RecordError("Building repair snapshot failed: " + ex);
            }
            return state;
        }

        private BuildingRepairQuote CaptureSelectedQuote(EngineInterface.PlayState state)
        {
            if (state == null || !IsBuildingHud(state) || state.in_structure <= 0) return null;
            GameBuildingManagerAPI buildings = GameBuildingManagerAPI.Instance;
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            if (buildings == null || players == null ||
                !BuildingAccess.TryGetById(buildings, state.in_structure, out GameBuilding* building, out _) || building == null)
                return null;
            int playerId = players.GetLocalPlayerId();
            eStructs type = building->r_BuildingType;
            if (playerId <= 0 || building->r_PlayerIdOwner != playerId ||
                building->r_AliveState != AliveState.IsAlive || building->r_GlobalId == 0 ||
                building->r_MaxHealth == 0 || building->r_MaxHealth > short.MaxValue ||
                building->r_CurrentHealth < 0 || building->r_CurrentHealth > building->r_MaxHealth ||
                type <= eStructs.STRUCT_NULL || type > eStructs.STRUCT_GARDEN_LARGE ||
                state.in_structure_type != (int)type)
                return null;

            bool allowed = nativeCanRepair() != 0;
            IntPtr manager = (IntPtr)buildings.GetBuildingManager().Pointer;
            if (manager == IntPtr.Zero) return null;
            int wood = Marshal.ReadInt32(manager, WoodCostOffset);
            int stone = Marshal.ReadInt32(manager, StoneCostOffset);
            int baseIron = buildings.GetIronIngotCost(type);
            int basePitch = buildings.GetRawPitchCost(type);
            int baseGold = buildings.GetGoldCost(type);
            if (wood < 0 || stone < 0 || baseIron < 0 || basePitch < 0 || baseGold < 0)
                return null;
            int iron = RepairCost(baseIron, building->r_CurrentHealth, building->r_MaxHealth);
            int pitch = RepairCost(basePitch, building->r_CurrentHealth, building->r_MaxHealth);
            int gold = RepairCost(baseGold, building->r_CurrentHealth, building->r_MaxHealth);

            state.repair_wood_needed = wood;
            state.repair_stone_needed = stone;
            state.building_hps_for_repair = building->r_CurrentHealth;
            state.building_maxhps_for_repair = (short)building->r_MaxHealth;
            state.can_do_repairs = (short)(allowed ? 1 : 0);
            return new BuildingRepairQuote(state.in_structure, unchecked((int)building->r_GlobalId),
                state.app_sub_mode, allowed, building->r_CurrentHealth, building->r_MaxHealth,
                wood, stone, iron, pitch, gold,
                AvailableForTooltip(state, players, playerId, eGoods.STORED_WOOD_PLANKS),
                AvailableForTooltip(state, players, playerId, eGoods.STORED_STONE_BLOCKS),
                AvailableForTooltip(state, players, playerId, eGoods.STORED_IRON_INGOTS),
                AvailableForTooltip(state, players, playerId, eGoods.STORED_PITCH_RAW),
                AvailableForTooltip(state, players, playerId, eGoods.STORED_GOLD));
        }

        private static int AvailableForTooltip(EngineInterface.PlayState state,
            GamePlayerManagerAPI players, int playerId, eGoods good)
        {
            int index = (int)good;
            bool useKeep = state.game_type == (int)Enums.eGameTypeModes.GAMETYPE_SIEGE_THAT_BUILDER &&
                (good == eGoods.STORED_WOOD_PLANKS || good == eGoods.STORED_STONE_BLOCKS ||
                 good == eGoods.STORED_GOLD);
            int[] values = useKeep ? state.keep_storage : state.resources;
            return values != null && index >= 0 && index < values.Length
                ? values[index]
                : players.GetGoodAmount(playerId, good);
        }

        private static int RepairCost(int baseCost, int health, int maxHealth)
        {
            if (baseCost < 0 || health < 0 || maxHealth <= 0 || health >= maxHealth) return 0;
            if (baseCost == 0) return 0;
            long amount = (long)(maxHealth - health) * baseCost / maxHealth;
            return (int)Math.Min(int.MaxValue, Math.Max(1L, amount));
        }

        private static bool IsBuildingHud(EngineInterface.PlayState state)
        {
            if (state.app_mode != (int)Enums.AppModes.APP_MODE_IN_BUILDING) return false;
            int panel = state.app_sub_mode;
            if (panel == (int)Enums.InBuildingModes.INSIDE_PEACETIME ||
                panel == (int)Enums.InBuildingModes.INSIDE_CHIMP ||
                (panel >= (int)Enums.InBuildingModes.SUB_MODE_REPORTS_ARMY4 &&
                 panel <= (int)Enums.InBuildingModes.SUB_MODE_REPORTS_RELIGION)) return false;
            return panel >= (int)Enums.InBuildingModes.INSIDE_BARRACKS &&
                panel <= (int)Enums.InBuildingModes.INSIDE_CATHEDRAL;
        }

        private bool TryGetSelectedQuote(out BuildingRepairQuote quote)
        {
            quote = null;
            if (Volatile.Read(ref activeOwners) == 0) return false;
            EngineInterface.PlayState state = GameData.Instance?.lastGameState;
            if (state == null || !IsBuildingHud(state) ||
                !snapshots.TryGetValue(state, out BuildingRepairQuote stored) ||
                stored.BuildingId != state.in_structure || stored.Panel != state.app_sub_mode)
                return false;
            quote = stored;
            return true;
        }
    }
}
