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
        private void RenderTroopCategories(HUD_Troops panel)
        {
            HudWorkBuffers buffers = RentHudBuffers();
            try
            {
                EnsureCategoryButtons(panel);
                int[] vanillaCounts = SelectedCountsField.GetValue(panel) as int[];
                TranslateTransform[] positions = TroopPositionsField.GetValue(panel) as TranslateTransform[];
                if (vanillaCounts == null || positions == null || positions.Length < TroopSlotCount)
                    throw new InvalidOperationException("Vanilla troop HUD fields have an unexpected layout.");

                List<UnitHudUnitSnapshot> selected = buffers.Selected;
                bool selectionComplete = TryCaptureSelectedUnits(selected, buffers.Seen);
                List<DisplayEntry> entries = BuildEntries(selected, vanillaCounts, selectionComplete, UnitHudSurface.TroopSelection, buffers);
                int pages = Math.Max(1, (entries.Count + TroopSlotCount - 1) / TroopSlotCount);
                int page = Math.Max(0, Math.Min((int)CurrentPageField.GetValue(panel), pages - 1));
                PagesField.SetValue(panel, pages);
                CurrentPageField.SetValue(panel, page);
                SelectedTypeCountField.SetValue(panel, entries.Count);
                panel.HideAllSelectedTroops();
                panel.HideAllSelectedTroopsNumbers();
                HideCategoryButtons();
                SetPageButtons(panel, page, pages);

                List<UnitHudSlotSnapshot> snapshots = buffers.Slots;
                for (int slot = 0; slot < TroopSlotCount; slot++)
                {
                    int index = page * TroopSlotCount + slot;
                    if (index >= entries.Count) break;
                    DisplayEntry entry = entries[index];
                    if (entry.Category == null)
                    {
                        positions[slot].Y = panel.SetSelectedTroopVisible(entry.VanillaType);
                        panel.SetSelectedTroopPosition(entry.VanillaType, slot);
                        snapshots.Add(new UnitHudSlotSnapshot(slot, entry.VanillaType, null));
                    }
                    else
                    {
                        Grid host = categoryHosts[slot];
                        Button button = categoryButtons[slot];
                        button.Tag = entry.Category.Key;
                        button.ToolTip = ResolveText(entry.Category, UnitHudTextKind.DisplayName);
                        if (button.IsMouseOver) ShowTroopHover(button, button.ToolTip as string);
                        host.RenderTransform = positions[slot];
                        ImageSource source = ResolveCategoryImage(entry.Category, UnitHudSurface.TroopSelection, panel);
                        ApplyTroopButtonImages(button, entry.Category, panel, source);
                        ApplyTint(categoryTints[slot], entry.Category.Definition.Tint, source);
                        host.Visibility = Visibility.Visible;
                        UnitHudCategorySnapshot snapshot = Snapshot(entry.Category, entry.Units);
                        snapshots.Add(new UnitHudSlotSnapshot(slot, -1, snapshot));
                    }
                    panel.ShowSelectedTroopsNumber(slot, entry.Count);
                }
                lock (sync)
                {
                    visibleSlots.Clear();
                    visibleSlots.AddRange(snapshots);
                }
                RememberRenderedTroopSelection();
            }
            finally
            {
                buffers.Clear();
                lock (sync) hudBufferPool.Push(buffers);
            }
        }

        private static void ApplyTint(Border target, UnitHudTint tint, ImageSource source)
        {
            if (target == null) return;
            TintCache cache = TintCaches.GetValue(target, _ => new TintCache());
            if (cache.Tint == null || cache.Tint.Red != tint.Red || cache.Tint.Green != tint.Green || cache.Tint.Blue != tint.Blue)
                cache.Brush = new SolidColorBrush(Noesis.Color.FromArgb(byte.MaxValue, tint.Red, tint.Green, tint.Blue));
            if (!ReferenceEquals(cache.Source, source)) cache.Mask = source == null ? null : new ImageBrush(source);
            cache.Tint = tint;
            cache.Source = source;
            if (!ReferenceEquals(target.Background, cache.Brush)) target.Background = cache.Brush;
            if (!ReferenceEquals(target.OpacityMask, cache.Mask)) target.OpacityMask = cache.Mask;
            float opacity = source == null || tint.Alpha == 0 ||
                tint.Red == byte.MaxValue && tint.Green == byte.MaxValue && tint.Blue == byte.MaxValue
                ? 0.0f
                : (float)tint.Alpha / byte.MaxValue;
            if (target.Opacity != opacity) target.Opacity = opacity;
            if (target.IsHitTestVisible) target.IsHitTestVisible = false;
        }

        private sealed class TintCache
        {
            internal UnitHudTint Tint;
            internal ImageSource Source;
            internal SolidColorBrush Brush;
            internal ImageBrush Mask;
        }

        private static void SetVisibility(UIElement element, Visibility value)
        {
            if (element != null && element.Visibility != value) element.Visibility = value;
        }

        private void EnsureCategoryButtons(HUD_Troops panel)
        {
            if (ReferenceEquals(activeTroopPanel, panel) && categoryHosts != null && categoryButtons != null && categoryTints != null)
                return;

            var resolvedHosts = new Grid[TroopSlotCount];
            var resolvedButtons = new Button[TroopSlotCount];
            var resolvedTints = new Border[TroopSlotCount];
            for (int i = 0; i < resolvedButtons.Length; i++)
            {
                var host = panel.FindName("APISharedUnitHudSlotHost" + (i + 1)) as Grid;
                var button = panel.FindName("APISharedUnitHudSlot" + (i + 1)) as Button;
                var tint = panel.FindName("APISharedUnitHudSlotTint" + (i + 1)) as Border;
                if (host == null) throw new MissingMemberException("APISharedUnitHudSlotHost" + (i + 1));
                if (button == null) throw new MissingMemberException("APISharedUnitHudSlot" + (i + 1));
                if (tint == null) throw new MissingMemberException("APISharedUnitHudSlotTint" + (i + 1));
                resolvedHosts[i] = host;
                resolvedButtons[i] = button;
                resolvedTints[i] = tint;
            }
            foreach (Button button in resolvedButtons)
            {
                ToolTipService.SetIsEnabled(button, false);
                button.MouseEnter += (sender, args) => ShowTroopHover(button, button.ToolTip as string);
                button.MouseLeave += (sender, args) => troopHover.Close(button);
                button.PreviewMouseDown -= OnCategoryMouseDown;
                button.PreviewMouseDown += OnCategoryMouseDown;
            }
            activeTroopPanel = panel;
            categoryHosts = resolvedHosts;
            categoryButtons = resolvedButtons;
            categoryTints = resolvedTints;
        }

        private void HideCategoryButtons()
        {
            if (categoryHosts == null) return;
            foreach (Grid host in categoryHosts)
                if (host != null) host.Visibility = Visibility.Collapsed;
        }

        private void OnCategoryMouseDown(object sender, MouseButtonEventArgs args)
        {
            if (!(sender is Button button) || !(button.Tag is string key) || string.IsNullOrEmpty(key) || args == null)
                return;
            UnitHudMouseButton mouse;
            if (args.ChangedButton == MouseButton.Left) mouse = UnitHudMouseButton.Left;
            else if (args.ChangedButton == MouseButton.Right) mouse = UnitHudMouseButton.Right;
            else if (args.ChangedButton == MouseButton.Middle) mouse = UnitHudMouseButton.Middle;
            else return;
            CategoryRegistration category = GetCategory(key);
            if (category == null) return;
            List<UnitHudUnitSnapshot> matches = CaptureSelectedUnits().Where(x => ReferenceEquals(Classify(x, UnitHudSurface.TroopSelection), category)).ToList();
            UnitHudCategorySnapshot snapshot = Snapshot(category, matches);
            if (mouse == UnitHudMouseButton.Left)
                SetSelection(matches.Select(x => x.GameId));
            else if (mouse == UnitHudMouseButton.Right)
            {
                var ids = new HashSet<int>(matches.Select(x => x.GameId));
                SetSelection(CaptureSelectedUnits().Where(x => !ids.Contains(x.GameId)).Select(x => x.GameId));
            }
            NotifyInteraction(new UnitHudInteractionContext(mouse, snapshot));
            args.Handled = true;
        }

        // Runs after Vanilla's control-group populate every frame while the panel is open, over
        // all GroupCount x GroupCapacity native records. Only unit types with a registered
        // control-group category need a snapshot and classification; the dynamic activity,
        // matcher and conflict rules stay in Classify. Vanilla rewrites the slots before this
        // runs, so every visible value is still written each frame.
        private CategoryRegistration[] groupTypeView;
        private bool[] groupCategoryTypes = Array.Empty<bool>();
        private readonly Dictionary<CategoryRegistration, GroupEntry> groupCategoryEntries = new Dictionary<CategoryRegistration, GroupEntry>();
        private readonly Dictionary<int, GroupEntry> groupVanillaEntries = new Dictionary<int, GroupEntry>();
        private readonly List<GroupEntry> groupVisible = new List<GroupEntry>();
        private Func<HUD_ControlGroups, int, ImageSource> groupSprite;
        private bool groupSpriteResolved;
        private static readonly Comparison<GroupEntry> GroupOrder = (left, right) =>
        {
            int compare = left.BaseType.CompareTo(right.BaseType);
            if (compare != 0) return compare;
            compare = (left.Category == null ? 0 : 1).CompareTo(right.Category == null ? 0 : 1);
            return compare != 0 ? compare : string.CompareOrdinal(left.SortKey, right.SortKey);
        };

        private bool frameDemand;

        private void NotifyInteraction(UnitHudInteractionContext context)
        {
            InteractionRegistration[] copy; lock (sync) copy = interactionView;
            foreach (InteractionRegistration item in copy)
            {
                lock (sync) { if (!OwnerActive(item.Owner)) continue; }
                try { item.Handler(context); } catch (Exception ex) { LogCallbackFailure("interaction " + item.Owner + ":" + item.Id, ex); }
            }
        }

        private sealed class DisplayEntry
        {
            internal DisplayEntry(int type, int count) { VanillaType = type; Count = count; }
            internal DisplayEntry(CategoryRegistration category, List<UnitHudUnitSnapshot> units) { VanillaType = category.Definition.BaseUnitType; Category = category; Units = units; Count = units.Count; }
            internal int VanillaType { get; } internal int Count { get; } internal CategoryRegistration Category { get; } internal List<UnitHudUnitSnapshot> Units { get; }
        }
    }
}
