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
    internal sealed unsafe partial class UnitHudPresentationService
    {
        private CategoryRegistration Classify(UnitHudUnitSnapshot unit, UnitHudSurface surface)
        {
            CategoryRegistration match = null;
            foreach (CategoryRegistration category in CategoryCopy())
            {
                lock (sync) { if (!category.Active || !OwnerActive(category.Owner)) continue; }
                if (!HasSurface(category, surface) || category.Definition.BaseUnitType != unit.VanillaType || !Claims(category, unit)) continue;
                if (match != null)
                {
                    lock (sync)
                    {
                        // A seen key's string is already in loggedCategoryConflicts, so only a new
                        // conflict builds it; deduplication stays keyed by the former string.
                        if (seenCategoryConflicts.Add((unit.GameId, unit.GlobalId, (int)surface, match.Key, category.Key)))
                        {
                            string conflict = unit.GameId + ":" + unit.GlobalId + ":" + surface + ":" + match.Key + ":" + category.Key;
                            if (loggedCategoryConflicts.Add(conflict))
                                NativeApiLog.Error(log, $"Unit HUD category conflict for unit={unit.GameId}, global={unit.GlobalId}: {match.Key} and {category.Key}; Vanilla category retained.");
                        }
                    }
                    return null;
                }
                match = category;
            }
            return match;
        }

        private bool Claims(CategoryRegistration category, UnitHudUnitSnapshot unit)
        {
            try { return category.Matcher(unit); }
            catch (Exception ex) { LogCallbackFailure("category matcher " + category.Key, ex); return false; }
        }

        private bool IsClaimed(UnitHudUnitSnapshot unit, UnitHudSurface surface) => Classify(unit, surface) != null;
        private bool HasDerivedCategory(int type) => CategoryCopy().Any(x => x.Definition.BaseUnitType == type && HasSurface(x, UnitHudSurface.TroopSelection));
        private bool HasCategories(UnitHudSurface surface) => (activeSurfaces & surface) != 0;
        private static bool HasSurface(CategoryRegistration category, UnitHudSurface surface) => (category.Definition.Surfaces & surface) != 0;
        private CategoryRegistration[] CategoryCopy() { lock (sync) return categoryView; }
        private CategoryRegistration GetCategory(string key) => CategoryCopy().FirstOrDefault(x => x.Key == key);
        private UnitHudCategorySnapshot Snapshot(CategoryRegistration category, IReadOnlyList<UnitHudUnitSnapshot> units) => new UnitHudCategorySnapshot(category.Owner, category.Definition.CategoryId, ResolveText(category, UnitHudTextKind.DisplayName), units);

        private static int ToSummaryType(int unitType)
        {
            if (unitType == (int)eChimps.CHIMP_TYPE_TUNNELER) return TunnelSummaryType;
            if (unitType >= (int)eChimps.CHIMP_TYPE_ARCHER && unitType <= (int)eChimps.CHIMP_TYPE_ENGINEER)
                return unitType - (int)eChimps.CHIMP_TYPE_ARCHER + EuropeanTroopSummaryStart;
            if (unitType == (int)eChimps.CHIMP_TYPE_MONK) return MonkSummaryType;
            if (unitType >= (int)eChimps.CHIMP_TYPE_CATAPULT && unitType <= (int)eChimps.CHIMP_TYPE_MANGONEL)
                return unitType - (int)eChimps.CHIMP_TYPE_CATAPULT + SiegeEngineSummaryStart;
            if (unitType >= (int)eChimps.CHIMP_TYPE_SIEGE_TOWER && unitType <= (int)eChimps.CHIMP_TYPE_BALLISTA)
                return unitType - (int)eChimps.CHIMP_TYPE_SIEGE_TOWER + PortableSiegeSummaryStart;
            if (unitType >= (int)eChimps.CHIMP_TYPE_ARAB_BOW && unitType <= (int)eChimps.CHIMP_TYPE_BEDOUIN_DEMOLISHER)
                return unitType - (int)eChimps.CHIMP_TYPE_ARAB_BOW + ArabicTroopSummaryStart;
            return -1;
        }
    }
}
