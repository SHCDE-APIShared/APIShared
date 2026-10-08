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
{    internal sealed unsafe partial class UnitHudPresentationService
    {
        private void ApplyUnitDetails(MainViewModel main)
        {
            if (main?.HUDBuildingPanel == null || !HasCategories(UnitHudSurface.UnitDetails)) return;
            if (!main.Show_HUD_Building || main.HUDBuildingPanel.RefChimpPanel.Visibility != Visibility.Visible) { HideUnitDetailControls(); return; }
            EnsureDetailControls(main);
            EngineInterface.PlayState state = GameData.Instance?.lastGameState;
            CategoryRegistration category = state != null && TryCapture(state.in_chimp, out UnitHudUnitSnapshot unit)
                ? Classify(unit, UnitHudSurface.UnitDetails)
                : null;
            if (category == null)
            {
                SetVisibility(unitDetailHost, Visibility.Collapsed);
                return;
            }
            ImageSource source = ResolveCategoryImage(category, UnitHudSurface.UnitDetails);
            if (!ReferenceEquals(unitDetailImage.Source, source)) unitDetailImage.Source = source;
            ApplyTint(unitDetailTint, category.Definition.Tint, source);
            string description = ResolveText(category, UnitHudTextKind.Description);
            if (unitDetailDescription.Text != description) unitDetailDescription.Text = description;
            SetVisibility(unitDetailHost, Visibility.Visible);
        }

        private void EnsureDetailControls(MainViewModel main)
        {
            if (ReferenceEquals(detailPanel, main.HUDBuildingPanel) && unitDetailHost != null) return;
            Grid host = main.HUDBuildingPanel.FindName("APISharedUnitDetailHost") as Grid;
            Image image = main.HUDBuildingPanel.FindName("APISharedUnitDetailImage") as Image;
            Border tint = main.HUDBuildingPanel.FindName("APISharedUnitDetailTint") as Border;
            TextBlock description = main.HUDBuildingPanel.FindName("APISharedUnitDetailDescription") as TextBlock;
            if (host == null || image == null || tint == null || description == null) throw new MissingMemberException("APIShared unit detail controls");
            unitDetailHost = host; unitDetailImage = image; unitDetailTint = tint; unitDetailDescription = description;
            detailPanel = main.HUDBuildingPanel;
        }

        private void HideUnitDetailControls()
        {
            if (unitDetailHost != null) SetVisibility(unitDetailHost, Visibility.Collapsed);
        }

        private string ResolveText(CategoryRegistration category, UnitHudTextKind kind)
        {
            UnitHudTextProfile profile = category.Definition.TextProfile;
            try
            {
                string resolved = profile?.Resolver?.Invoke(kind);
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
            }
            catch (Exception ex) { LogCallbackFailure("text resolver " + category.Key, ex); }
            if (kind == UnitHudTextKind.ShortLabel && !string.IsNullOrWhiteSpace(profile?.ShortLabelFallback)) return profile.ShortLabelFallback;
            if (kind == UnitHudTextKind.Description) return profile?.DescriptionFallback ?? string.Empty;
            return !string.IsNullOrWhiteSpace(profile?.DisplayNameFallback) ? profile.DisplayNameFallback : category.Definition.DisplayName;
        }

        private void ApplyArmyReport(MainViewModel main)
        {
            if (!HasCategories(UnitHudSurface.ArmyReport)) return;
            HUD_Buildings panel = main?.HUDBuildingPanel;
            if (panel == null || !main.Show_HUD_Building ||
                !(panel.RefReportsArmy1Panel.Visibility == Visibility.Visible || panel.RefReportsArmy2Panel.Visibility == Visibility.Visible ||
                  panel.RefReportsArmy3Panel.Visibility == Visibility.Visible || panel.RefReportsArmy4Panel.Visibility == Visibility.Visible)) return;
            EngineInterface.PlayState state = GameData.Instance?.lastGameState;
            if (main == null || state == null || state.troop_counts == null || !HasCategories(UnitHudSurface.ArmyReport)) return;
            if (!ReferenceEquals(armyPanel, panel) || armyHost == null)
            {
                Panel resolved = panel.FindName("APISharedArmyCategoriesHost") as Panel;
                if (resolved == null) throw new MissingMemberException("APISharedArmyCategoriesHost");
                armyEntries.Clear();
                armyPanel = panel;
                armyHost = resolved;
            }
            Panel host = armyHost;
            int local = PlayerPerspectiveAPI.GetViewedPlayerId();
            ArmyWorkBuffers buffers = armyBufferPool.Count == 0 ? new ArmyWorkBuffers() : armyBufferPool.Pop();
            try
            {
                var custom = buffers.Counts;
                var reductions = buffers.Reductions;
                var desiredCounts = buffers.Desired;
                foreach (CategoryRegistration registration in CategoryCopy())
                {
                    if (!HasSurface(registration, UnitHudSurface.ArmyReport)) continue;
                    int reportIndex = ToArmyReportIndex(registration.Definition.BaseUnitType);
                    if (reportIndex > 0 && reportIndex < main.AllTroops.Count && reportIndex - 1 < state.troop_counts.Length)
                        desiredCounts[reportIndex] = state.troop_counts[reportIndex - 1];
                }
                foreach (int unitId in APIShared.UnitAccess.GetAllReallyAliveUnits())
                {
                    if (!TryCapture(unitId, out UnitHudUnitSnapshot unit) || unit.OwnerPlayerId != local) continue;
                    CategoryRegistration category = Classify(unit, UnitHudSurface.ArmyReport);
                    if (category == null) continue;
                    custom[category.Key] = custom.TryGetValue(category.Key, out int categoryCount) ? categoryCount + 1 : 1;
                    reductions[unit.VanillaType] = reductions.TryGetValue(unit.VanillaType, out int count) ? count + 1 : 1;
                }
                foreach (KeyValuePair<int, int> reduction in reductions)
                {
                    int reportIndex = ToArmyReportIndex(reduction.Key);
                    if (desiredCounts.TryGetValue(reportIndex, out int vanillaCount))
                        desiredCounts[reportIndex] = Math.Max(0, vanillaCount - reduction.Value);
                }
                RenderArmyHosts(host, custom);
                foreach (KeyValuePair<int, int> desired in desiredCounts)
                {
                    if (main.AllTroops[desired.Key] != desired.Value) main.AllTroops[desired.Key] = desired.Value;
                    writtenArmyIndices.Add(desired.Key);
                }
            }
            finally { buffers.Counts.Clear(); buffers.Reductions.Clear(); buffers.Desired.Clear(); armyBufferPool.Push(buffers); }
        }
        private sealed class ArmyWorkBuffers
        {
            internal readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);
            internal readonly Dictionary<int, int> Reductions = new Dictionary<int, int>();
            internal readonly Dictionary<int, int> Desired = new Dictionary<int, int>();
        }
        private void HideArmyHosts()
        {
            foreach (Noesis.Grid grid in armyEntries.Values)
                if (grid != null) SetVisibility(grid, Visibility.Collapsed);
        }

        private void RenderArmyHosts(Panel host, Dictionary<string, int> custom)
        {
            foreach (var entry in armyEntries)
                if (!custom.ContainsKey(entry.Key)) SetVisibility(entry.Value, Visibility.Collapsed);
            foreach (CategoryRegistration category in CategoryCopy().Where(x => HasSurface(x, UnitHudSurface.ArmyReport)))
            {
                if (!custom.TryGetValue(category.Key, out int countValue) || countValue == 0) continue;
                if (!armyEntries.TryGetValue(category.Key, out Noesis.Grid grid))
                {
                    grid = new Noesis.Grid { Width = 64, Height = 76, Margin = new Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
                    var image = new Image { Width = 52, Height = 52, VerticalAlignment = VerticalAlignment.Top };
                    var tint = new Border { Width = 52, Height = 52, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
                    var count = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Foreground = new SolidColorBrush(Noesis.Color.FromArgb(byte.MaxValue, 174, 214, byte.MaxValue)), FontSize = 18 };
                    grid.Children.Add(image); grid.Children.Add(tint); grid.Children.Add(count);
                    host.Children.Add(grid);
                    armyEntries[category.Key] = grid;
                }
                ImageSource source = ResolveCategoryImage(category, UnitHudSurface.ArmyReport);
                if (!ReferenceEquals(((Image)grid.Children[0]).Source, source)) ((Image)grid.Children[0]).Source = source;
                ApplyTint((Border)grid.Children[1], category.Definition.Tint, source);
                TextBlock label = (TextBlock)grid.Children[2];
                if (!(label.Tag is int previousCount) || previousCount != countValue)
                { label.Text = countValue.ToString(); label.Tag = countValue; }
                SetVisibility(grid, Visibility.Visible);
            }
        }

        private List<DisplayEntry> BuildEntries(
            List<UnitHudUnitSnapshot> selected,
            int[] vanillaCounts,
            bool selectionComplete,
            UnitHudSurface surface,
            HudWorkBuffers buffers)
        {
            Dictionary<string, List<UnitHudUnitSnapshot>> claimed = buffers.Grouped;
            HashSet<int> claimedIds = buffers.ClaimedIds;
            if (buffers.Reduction.Length != vanillaCounts.Length) buffers.Reduction = new int[vanillaCounts.Length];
            int[] reduction = buffers.Reduction;
            foreach (UnitHudUnitSnapshot unit in selected)
            {
                CategoryRegistration category = Classify(unit, surface);
                if (category == null) continue;
                if (!claimed.TryGetValue(category.Key, out List<UnitHudUnitSnapshot> list)) claimed[category.Key] = list = buffers.RentGroup();
                list.Add(unit);
                claimedIds.Add(unit.GameId);
                if (unit.VanillaType >= 0 && unit.VanillaType < reduction.Length) reduction[unit.VanillaType]++;
            }
            int[] effectiveCounts = UnitHudSelectionPolicy.ResolveVanillaTroopCounts(
                vanillaCounts,
                selected,
                claimedIds,
                selectionComplete,
                buffers.EffectiveCounts);
            buffers.EffectiveCounts = effectiveCounts;
            List<DisplayEntry> result = buffers.Entries;
            CategoryRegistration[] registrations = CategoryCopy();
            for (int type = 0; type < effectiveCounts.Length; type++)
            {
                int normal = selectionComplete
                    ? effectiveCounts[type]
                    : Math.Max(0, effectiveCounts[type] - reduction[type]);
                if (normal > 0) result.Add(new DisplayEntry(type, normal));
                foreach (CategoryRegistration category in registrations)
                    if (category.Definition.BaseUnitType == type && HasSurface(category, surface) &&
                        claimed.TryGetValue(category.Key, out List<UnitHudUnitSnapshot> units) && units.Count > 0) result.Add(new DisplayEntry(category, units));
            }
            return result;
        }

        private static int ToArmyReportIndex(int unitType)
        {
            int summary = ToSummaryType(unitType);
            return summary >= 0 ? summary + 1 : -1;
        }

        private HudWorkBuffers RentHudBuffers()
        {
            lock (sync) return hudBufferPool.Count == 0 ? new HudWorkBuffers() : hudBufferPool.Pop();
        }

        // A callback may reenter the service. Never share a live workspace between calls,
        // and copy public snapshots before clearing these internal lists.
        private sealed class HudWorkBuffers
        {
            internal readonly List<UnitHudUnitSnapshot> Selected = new List<UnitHudUnitSnapshot>();
            internal readonly HashSet<int> Seen = new HashSet<int>();
            internal readonly HashSet<int> ClaimedIds = new HashSet<int>();
            internal readonly Dictionary<string, List<UnitHudUnitSnapshot>> Grouped =
                new Dictionary<string, List<UnitHudUnitSnapshot>>(StringComparer.Ordinal);
            internal readonly List<DisplayEntry> Entries = new List<DisplayEntry>();
            internal readonly List<UnitHudSlotSnapshot> Slots = new List<UnitHudSlotSnapshot>();
            internal readonly List<UnitHudCategorySnapshot> Categories = new List<UnitHudCategorySnapshot>();
            internal int[] Reduction = Array.Empty<int>();
            internal int[] EffectiveCounts = Array.Empty<int>();
            private readonly Stack<List<UnitHudUnitSnapshot>> groupPool = new Stack<List<UnitHudUnitSnapshot>>();

            internal List<UnitHudUnitSnapshot> RentGroup() =>
                groupPool.Count == 0 ? new List<UnitHudUnitSnapshot>() : groupPool.Pop();

            internal void Clear()
            {
                foreach (List<UnitHudUnitSnapshot> group in Grouped.Values)
                {
                    group.Clear();
                    groupPool.Push(group);
                }
                Grouped.Clear();
                Selected.Clear();
                Seen.Clear();
                ClaimedIds.Clear();
                Entries.Clear();
                Slots.Clear();
                Categories.Clear();
                Array.Clear(Reduction, 0, Reduction.Length);
                Array.Clear(EffectiveCounts, 0, EffectiveCounts.Length);
            }
        }
    }
}
