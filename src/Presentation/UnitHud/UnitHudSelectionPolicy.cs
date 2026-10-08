using APIShared.GameModes;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using Noesis;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.MapLoader;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace APIShared
{
    internal static class UnitHudSelectionPolicy
    {
        internal static int[] ResolveVanillaTroopCounts(
            int[] vanillaCounts,
            IReadOnlyList<UnitHudUnitSnapshot> selected,
            ISet<int> claimedUnitIds,
            bool selectionComplete,
            int[] reusableCounts = null)
        {
            if (vanillaCounts == null) throw new ArgumentNullException(nameof(vanillaCounts));
            int[] result = reusableCounts != null && reusableCounts.Length == vanillaCounts.Length
                ? reusableCounts : new int[vanillaCounts.Length];
            Array.Copy(vanillaCounts, result, vanillaCounts.Length);
            if (!selectionComplete || selected == null || claimedUnitIds == null)
                return result;
            Array.Clear(result, 0, result.Length);
            foreach (UnitHudUnitSnapshot unit in selected)
            {
                if (unit == null || claimedUnitIds.Contains(unit.GameId) ||
                    unit.VanillaType < 0 || unit.VanillaType >= result.Length ||
                    !IsVanillaTroopHudType(unit.VanillaType))
                    continue;
                result[unit.VanillaType]++;
            }
            return result;
        }

        internal static bool SelectionIdentityEquals(
            int[] leftIds,
            int[] leftTypes,
            int[] rightIds,
            int[] rightTypes,
            int rightCount)
        {
            if (leftIds == null || leftTypes == null || rightIds == null || rightTypes == null ||
                rightCount < 0 || leftIds.Length != leftTypes.Length || leftIds.Length != rightCount ||
                rightIds.Length < rightCount || rightTypes.Length < rightCount)
                return false;
            for (int i = 0; i < rightCount; i++)
                if (leftIds[i] != rightIds[i] || leftTypes[i] != rightTypes[i]) return false;
            return true;
        }

        private static bool IsVanillaTroopHudType(int unitType) =>
            unitType == (int)eChimps.CHIMP_TYPE_TUNNELER ||
            unitType >= (int)eChimps.CHIMP_TYPE_ARCHER && unitType <= (int)eChimps.CHIMP_TYPE_ENGINEER ||
            unitType == (int)eChimps.CHIMP_TYPE_MONK ||
            unitType >= (int)eChimps.CHIMP_TYPE_CATAPULT && unitType <= (int)eChimps.CHIMP_TYPE_MANGONEL ||
            unitType >= (int)eChimps.CHIMP_TYPE_SIEGE_TOWER && unitType <= (int)eChimps.CHIMP_TYPE_BALLISTA ||
            unitType >= (int)eChimps.CHIMP_TYPE_ARAB_BOW && unitType <= (int)eChimps.CHIMP_TYPE_BEDOUIN_DEMOLISHER;
    }
}
