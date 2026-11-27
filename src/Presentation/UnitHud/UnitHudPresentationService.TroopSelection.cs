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
        private List<UnitHudUnitSnapshot> CaptureSelectedUnits()
        {
            TryCaptureSelectedUnits(out List<UnitHudUnitSnapshot> result);
            return result;
        }

        private static bool TryCaptureSelectedUnits(out List<UnitHudUnitSnapshot> result)
        {
            result = new List<UnitHudUnitSnapshot>();
            return TryCaptureSelectedUnits(result, new HashSet<int>());
        }

        private static bool TryCaptureSelectedUnits(List<UnitHudUnitSnapshot> result, HashSet<int> seen)
        {
            result.Clear();
            seen.Clear();
            int localPlayerId = LocalSelectionAPI.CurrentSelectionPlayerId();
            if (!LocalSelectionAPI.TryCapture(localPlayerId, out LocalSelectionSnapshot selection))
                return false;
            for (int i = 0; i < selection.Count; i++)
            {
                int unitId = selection[i].UnitId;
                if (!seen.Add(unitId) || !TryCapture(unitId, out UnitHudUnitSnapshot snapshot) ||
                    snapshot.VanillaType != selection[i].UnitType)
                    return false;
                result.Add(snapshot);
            }
            return true;
        }

        private bool HasRenderedTroopSelectionChanged()
        {
            if (!hasRenderedTroopSelection || !TryGetTroopSelection(out LocalSelectionSnapshot selection))
                return false;
            // Runs every frame. LocalSelectionAPI returns the same immutable snapshot while the
            // selection is unchanged, and the rendered arrays were copied from that snapshot.
            if (ReferenceEquals(selection, lastRenderedTroopSelection))
                return false;
            if (lastTroopSelectionIds.Length != selection.Count)
                return true;
            for (int index = 0; index < selection.Count; index++)
                if (lastTroopSelectionIds[index] != selection[index].UnitId ||
                    lastTroopSelectionTypes[index] != selection[index].UnitType)
                    return true;
            return false;
        }

        private void RememberRenderedTroopSelection()
        {
            if (!TryGetTroopSelection(out LocalSelectionSnapshot selection))
            {
                hasRenderedTroopSelection = false;
                return;
            }
            int count = selection.Count;
            if (lastTroopSelectionIds.Length != count)
            {
                lastTroopSelectionIds = new int[count];
                lastTroopSelectionTypes = new int[count];
            }
            for (int index = 0; index < count; index++)
            {
                lastTroopSelectionIds[index] = selection[index].UnitId;
                lastTroopSelectionTypes[index] = selection[index].UnitType;
            }
            lastRenderedTroopSelection = selection;
            hasRenderedTroopSelection = true;
        }

        private static bool TryGetTroopSelection(out LocalSelectionSnapshot selection)
        {
            int localPlayerId = LocalSelectionAPI.CurrentSelectionPlayerId();
            return LocalSelectionAPI.TryCapture(localPlayerId, out selection);
        }

        private static bool TryCapture(int unitId, out UnitHudUnitSnapshot snapshot)
        {
            snapshot = null;
            if (unitId <= 0 || GameUnitManagerAPI.Instance == null || !APIShared.UnitAccess.TryGetById(unitId, out GameUnit* unit, out _) || unit == null || !APIShared.UnitAccess.IsReallyAlive(unit) || unit->r_GlobalId == 0) return false;
            snapshot = new UnitHudUnitSnapshot(unitId, unit->r_GlobalId, (int)unit->r_UnitChimp, unit->r_ControllableForPlayerId, true);
            return true;
        }

        private IReadOnlyList<UnitHudCategorySnapshot> CaptureSelectedCategories()
        {
            if (!HasCategories(UnitHudSurface.TroopSelection)) return Array.Empty<UnitHudCategorySnapshot>();
            HudWorkBuffers buffers = RentHudBuffers();
            try
            {
                Dictionary<string, List<UnitHudUnitSnapshot>> grouped = buffers.Grouped;
                TryCaptureSelectedUnits(buffers.Selected, buffers.Seen);
                foreach (UnitHudUnitSnapshot unit in buffers.Selected)
                {
                    CategoryRegistration category = Classify(unit, UnitHudSurface.TroopSelection);
                    if (category == null) continue;
                    if (!grouped.TryGetValue(category.Key, out List<UnitHudUnitSnapshot> items)) grouped[category.Key] = items = buffers.RentGroup();
                    items.Add(unit);
                }
                foreach (CategoryRegistration category in CategoryCopy())
                    if (grouped.TryGetValue(category.Key, out List<UnitHudUnitSnapshot> units))
                        buffers.Categories.Add(Snapshot(category, units));
                return buffers.Categories.ToArray();
            }
            finally
            {
                buffers.Clear();
                lock (sync) hudBufferPool.Push(buffers);
            }
        }
    }
}
