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
        private bool[] GroupCategoryTypes(CategoryRegistration[] view)
        {
            if (ReferenceEquals(groupTypeView, view)) return groupCategoryTypes;
            int size = 0;
            foreach (CategoryRegistration category in view)
                if (HasSurface(category, UnitHudSurface.ControlGroups) && category.Definition.BaseUnitType >= size)
                    size = category.Definition.BaseUnitType + 1;
            var types = new bool[size];
            foreach (CategoryRegistration category in view)
                if (HasSurface(category, UnitHudSurface.ControlGroups) && category.Definition.BaseUnitType >= 0)
                    types[category.Definition.BaseUnitType] = true;
            groupCategoryTypes = types;
            groupTypeView = view;
            return types;
        }

        private ImageSource GetGroupSprite(HUD_ControlGroups panel, int summaryType)
        {
            if (!groupSpriteResolved)
            {
                groupSpriteResolved = true;
                // Typed open-instance delegate for the private Vanilla getter; any signature
                // difference keeps the previous reflection call.
                if (!GroupSpriteMethod.IsStatic && GroupSpriteMethod.ReturnType == typeof(ImageSource))
                    groupSprite = Delegate.CreateDelegate(typeof(Func<HUD_ControlGroups, int, ImageSource>),
                        GroupSpriteMethod, false) as Func<HUD_ControlGroups, int, ImageSource>;
            }
            return groupSprite != null ? groupSprite(panel, summaryType)
                : GroupSpriteMethod.Invoke(panel, new object[] { summaryType }) as ImageSource;
        }

        private void RenderGroups(HUD_ControlGroups panel)
        {
            Image[,] images = GroupImagesField.GetValue(panel) as Image[,];
            TextBlock[,] values = GroupValuesField.GetValue(panel) as TextBlock[,];
            TextBlock[] extras = GroupExtraField.GetValue(panel) as TextBlock[];
            if (images == null || values == null || extras == null || images.GetLength(0) < GroupCount || images.GetLength(1) < GroupVisibleSlots)
                throw new InvalidOperationException("Vanilla control-group HUD fields have an unexpected layout.");
            EnsureGroupTints(panel, images);
            bool[] categoryTypes = GroupCategoryTypes(CategoryCopy());
            bool unitManager = GameUnitManagerAPI.Instance != null;
            for (int group = 0; group < GroupCount; group++)
            {
                groupCategoryEntries.Clear();
                groupVanillaEntries.Clear();
                int total = 0;
                int* start = groupRecords + group * GroupCapacity * GroupRecordWidth;
                for (int index = 0; index < GroupCapacity; index++)
                {
                    int unitId = start[index * GroupRecordWidth];
                    if (unitId <= 0 || !unitManager) continue;
                    int globalId = start[index * GroupRecordWidth + 1];
                    if (!APIShared.UnitAccess.TryGetById(unitId, out GameUnit* unit, out _) || unit == null ||
                        !APIShared.UnitAccess.IsReallyAlive(unit) || unit->r_GlobalId == 0 ||
                        unchecked((int)unit->r_GlobalId) != globalId) continue;
                    total++;
                    int vanillaType = (int)unit->r_UnitChimp;
                    CategoryRegistration category = (uint)vanillaType < (uint)categoryTypes.Length && categoryTypes[vanillaType]
                        ? Classify(new UnitHudUnitSnapshot(unitId, unit->r_GlobalId, vanillaType, unit->r_ControllableForPlayerId, true), UnitHudSurface.ControlGroups)
                        : null;
                    int summaryType = ToSummaryType(vanillaType);
                    if (summaryType < 0 && category == null) continue;
                    GroupEntry entry;
                    if (category != null)
                    {
                        if (!groupCategoryEntries.TryGetValue(category, out entry))
                            groupCategoryEntries[category] = entry = new GroupEntry(category, summaryType, vanillaType);
                    }
                    else if (!groupVanillaEntries.TryGetValue(summaryType, out entry))
                        groupVanillaEntries[summaryType] = entry = new GroupEntry(null, summaryType, vanillaType);
                    entry.Count++;
                }
                groupVisible.Clear();
                groupVisible.AddRange(groupVanillaEntries.Values);
                groupVisible.AddRange(groupCategoryEntries.Values);
                // The keys (base type, Vanilla first, sort key) are unique, so this order equals
                // the former stable LINQ ordering.
                groupVisible.Sort(GroupOrder);
                int visibleCount = Math.Min(groupVisible.Count, GroupVisibleSlots);
                int shown = 0;
                for (int slot = 0; slot < GroupVisibleSlots; slot++)
                {
                    if (slot < visibleCount)
                    {
                        GroupEntry entry = groupVisible[slot];
                        ImageSource source = entry.Category != null
                            ? ResolveCategoryImage(entry.Category, UnitHudSurface.ControlGroups, null, panel)
                            : GetGroupSprite(panel, entry.SummaryType);
                        images[group, slot].Source = source;
                        images[group, slot].Visibility = Visibility.Visible;
                        if (entry.Category != null)
                        {
                            ApplyTint(groupTints[group, slot], entry.Category.Definition.Tint, source);
                            groupTints[group, slot].Visibility = Visibility.Visible;
                        }
                        else groupTints[group, slot].Visibility = Visibility.Hidden;
                        values[group, slot].Text = entry.Count.ToString();
                        values[group, slot].Visibility = Visibility.Visible;
                        shown += entry.Count;
                    }
                    else
                    {
                        images[group, slot].Visibility = Visibility.Hidden;
                        groupTints[group, slot].Visibility = Visibility.Hidden;
                        values[group, slot].Visibility = Visibility.Hidden;
                    }
                }
                int remainder = Math.Max(0, total - shown);
                extras[group].Text = remainder > 0 ? "+" + remainder : string.Empty;
                extras[group].Visibility = remainder > 0 ? Visibility.Visible : Visibility.Hidden;
            }
        }
        private void EnsureGroupTints(HUD_ControlGroups panel, Image[,] images)
        {
            if (ReferenceEquals(activeGroupPanel, panel) && groupTints != null) return;
            var resolved = new Border[GroupCount, GroupVisibleSlots];
            for (int group = 0; group < GroupCount; group++)
            {
                for (int slot = 0; slot < GroupVisibleSlots; slot++)
                {
                    Image image = images[group, slot];
                    Panel parent = image == null ? null : VisualTreeHelper.GetParent(image) as Panel;
                    if (parent == null) throw new MissingMemberException($"CG{group}_TroopImage{slot + 1} parent");
                    var tint = new Border
                    {
                        Width = image.Width,
                        Height = image.Height,
                        Margin = image.Margin,
                        HorizontalAlignment = image.HorizontalAlignment,
                        VerticalAlignment = image.VerticalAlignment,
                        Visibility = Visibility.Hidden,
                        IsHitTestVisible = false
                    };
                    int imageIndex = parent.Children.IndexOf(image);
                    if (imageIndex < 0) throw new MissingMemberException($"CG{group}_TroopImage{slot + 1} child index");
                    parent.Children.Insert(imageIndex + 1, tint);
                    resolved[group, slot] = tint;
                }
            }
            activeGroupPanel = panel;
            groupTints = resolved;
        }

        private IReadOnlyList<UnitHudControlGroupSnapshot> CaptureControlGroups()
        {
            lock (sync)
            {
                if (!groupRecordsAvailable) return Array.Empty<UnitHudControlGroupSnapshot>();
                var result = new List<UnitHudControlGroupSnapshot>(GroupCount);
                for (int group = 0; group < GroupCount; group++)
                {
                    var members = new List<UnitHudUnitSnapshot>();
                    int* start = groupRecords + group * GroupCapacity * GroupRecordWidth;
                    for (int index = 0; index < GroupCapacity; index++)
                    {
                        int gameId = start[index * GroupRecordWidth];
                        int globalId = start[index * GroupRecordWidth + 1];
                        if (TryCapture(gameId, out UnitHudUnitSnapshot unit) &&
                            unchecked((int)unit.GlobalId) == globalId)
                        {
                            members.Add(unit);
                        }
                    }
                    result.Add(new UnitHudControlGroupSnapshot(group, members));
                }
                return result;
            }
        }

        private bool RemoveUnitFromControlGroups(
            int unitId,
            out int removedCount,
            out NativeCapabilityDiagnostic diagnostic)
        {
            removedCount = 0;
            if (unitId <= 0)
            {
                diagnostic = new NativeCapabilityDiagnostic(
                    NativeCapabilityIds.UnitHudPresentation,
                    NativeCapabilityState.ValidationFailed,
                    binaryHash,
                    "A positive one-based unit game ID is required.");
                return false;
            }
            lock (sync)
            {
                if (!groupRecordsAvailable)
                {
                    diagnostic = new NativeCapabilityDiagnostic(
                        NativeCapabilityIds.UnitHudPresentation,
                        NativeCapabilityState.UnsupportedBuild,
                        binaryHash,
                        "Native control-group storage is unavailable for this build.");
                    return false;
                }
                for (int group = 0; group < GroupCount; group++)
                {
                    int* start = groupRecords + group * GroupCapacity * GroupRecordWidth;
                    for (int index = 0; index < GroupCapacity; index++)
                    {
                        int* record = start + index * GroupRecordWidth;
                        if (record[0] != unitId)
                            continue;
                        record[0] = -1;
                        removedCount++;
                    }
                }
            }
            diagnostic = new NativeCapabilityDiagnostic(
                NativeCapabilityIds.UnitHudPresentation,
                NativeCapabilityState.Available,
                binaryHash,
                $"Removed unit ID {unitId} from {removedCount} native control-group records.");
            return true;
        }

        private sealed class GroupEntry
        {
            internal GroupEntry(CategoryRegistration category, int summaryType, int baseType) { Category = category; SummaryType = summaryType; BaseType = baseType; }
            internal CategoryRegistration Category { get; } internal int SummaryType { get; } internal int BaseType { get; } internal int Count { get; set; }
            // Computed once per entry; the comparison runs several times per frame.
            internal string SortKey => sortKey ?? (sortKey = Category != null ? "1:" + Category.Key : "0:" + SummaryType.ToString("D2"));
            private string sortKey;
        }
    }
}
