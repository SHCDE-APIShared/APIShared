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

        private void RenderGroups(HUD_ControlGroups panel)
        {
            Image[,] images = GroupImagesField.GetValue(panel) as Image[,];
            TextBlock[,] values = GroupValuesField.GetValue(panel) as TextBlock[,];
            TextBlock[] extras = GroupExtraField.GetValue(panel) as TextBlock[];
            if (images == null || values == null || extras == null || images.GetLength(0) < GroupCount || images.GetLength(1) < GroupVisibleSlots)
                throw new InvalidOperationException("Vanilla control-group HUD fields have an unexpected layout.");
            EnsureGroupTints(panel, images);
            for (int group = 0; group < GroupCount; group++)
            {
                var counts = new Dictionary<string, GroupEntry>(StringComparer.Ordinal);
                int total = 0;
                int* start = groupRecords + group * GroupCapacity * GroupRecordWidth;
                for (int index = 0; index < GroupCapacity; index++)
                {
                    int unitId = start[index * GroupRecordWidth];
                    int globalId = start[index * GroupRecordWidth + 1];
                    if (!TryCapture(unitId, out UnitHudUnitSnapshot snapshot) || unchecked((int)snapshot.GlobalId) != globalId) continue;
                    total++;
                    CategoryRegistration category = Classify(snapshot, UnitHudSurface.ControlGroups);
                    int summaryType = ToSummaryType(snapshot.VanillaType);
                    string key = category != null ? category.Key : "v:" + summaryType;
                    if (summaryType < 0 && category == null) continue;
                    if (!counts.TryGetValue(key, out GroupEntry entry)) counts[key] = entry = new GroupEntry(category, summaryType, snapshot.VanillaType);
                    entry.Count++;
                }
                GroupEntry[] visible = counts.Values.Where(x => x.Count > 0).OrderBy(x => x.BaseType).ThenBy(x => x.Category == null ? 0 : 1).ThenBy(x => x.SortKey, StringComparer.Ordinal).Take(GroupVisibleSlots).ToArray();
                int shown = 0;
                for (int slot = 0; slot < GroupVisibleSlots; slot++)
                {
                    if (slot < visible.Length)
                    {
                        GroupEntry entry = visible[slot];
                        ImageSource source = entry.Category != null
                            ? ResolveCategoryImage(entry.Category, UnitHudSurface.ControlGroups, null, panel)
                            : GroupSpriteMethod.Invoke(panel, new object[] { entry.SummaryType }) as ImageSource;
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

        private void OnBeforeRender()
        {
            if (activeSurfaces == UnitHudSurface.None && !pendingPresentation && !refreshRequested && recruitmentLease == null) return;
            if (lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            ExpireRecruitment();

            // Instance is a lazy constructor. Vanilla marks the view model loaded before
            // HUD_Main assigns HUDmain, so both signals are required before HUD work starts.
            if (!MainViewModel.viewModelLoaded) return;
            MainViewModel main = MainViewModel.Instance;
            if (main?.HUDmain == null) return;

            bool refresh;
            UnitHudSurface restore;
            bool images;
            lock (sync)
            {
                refresh = refreshRequested;
                refreshRequested = false;
                restore = restoreSurfaces;
                restoreSurfaces = UnitHudSurface.None;
                images = restoreImages;
                restoreImages = false;
                pendingPresentation = false;
            }
            RestorePresentation(main, restore, images);
            TryApplyFrameArea("refresh", () =>
            {
                bool troopSelectionChanged = main?.Show_HUD_Troops == true &&
                    HasCategories(UnitHudSurface.TroopSelection) &&
                    HasRenderedTroopSelectionChanged();
                if ((refresh && HasCategories(UnitHudSurface.TroopSelection)) || troopSelectionChanged)
                {
                    if (main?.Show_HUD_Troops == true) main.HUDTroopPanel?.SetupSelectedTroops();
                }
                if (refresh)
                {
                    if (HasCategories(UnitHudSurface.ControlGroups) && main?.Show_HUD_ControlGroups == true) main.HUDControlGroups?.Update();
                    if (activeImages && main != null && hasSpriteContext && IsImageOverrideContextReady())
                    {
                        // A refresh bypasses the public detour entry, preventing resolver-driven
                        // refresh requests or later hook chains from recursively re-entering us.
                        updateSpritesActive = true;
                        try
                        {
                            updateSpritesOriginal(main, lastSpriteColour, lastSpriteArabic);
                            ApplyImageOverrides(main, lastSpriteColour, lastSpriteArabic);
                        }
                        finally { updateSpritesActive = false; }
                    }
                }
            });
            TryApplyFrameArea("hover presentation", () => ApplyHover(main));
            TryApplyFrameArea("army-report presentation", () => ApplyArmyReport(main), HideArmyHosts);
            TryApplyFrameArea("recruitment presentation", () => ApplyRecruitmentPresentation(main), HideRecruitmentControls);
            TryApplyFrameArea("unit-detail presentation", () => ApplyUnitDetails(main), HideUnitDetailControls);
        }

        private void ExpireRecruitment()
        {
            lock (sync)
            {
                if (recruitmentLease == null || Time.realtimeSinceStartup < recruitmentLease.ExpiresAt) return;
                NativeApiLog.Error(log, $"Unit HUD recruitment ticket {recruitmentLease.Ticket.TicketId} timed out; recruitment lock released.");
                recruitmentLease = null;
            }
        }

        private void RestorePresentation(MainViewModel main, UnitHudSurface surfaces, bool images)
        {
            if (surfaces == UnitHudSurface.None && !images) return;
            try
            {
                if ((surfaces & UnitHudSurface.TroopSelection) != 0)
                {
                    HideCategoryButtons();
                    lock (sync) visibleSlots.Clear();
                    hasRenderedTroopSelection = false;
                    if (main.HUDTroopPanel != null) main.HUDTroopPanel.SetupSelectedTroops();
                }
                if ((surfaces & UnitHudSurface.ControlGroups) != 0)
                {
                    if (groupTints != null) foreach (Border tint in groupTints) SetVisibility(tint, Visibility.Hidden);
                    if (main.HUDControlGroups != null) main.HUDControlGroups.Update();
                }
                if ((surfaces & UnitHudSurface.UnitHover) != 0) RestoreHover();
                if ((surfaces & UnitHudSurface.ArmyReport) != 0)
                {
                    HideArmyHosts();
                    var state = GameData.Instance?.lastGameState;
                    if (writtenArmyIndices.Count > 0 && state?.troop_counts == null && main.Show_HUD_Building)
                        throw new InvalidOperationException("Awaiting Vanilla army counts for HUD restoration.");
                    foreach (int index in writtenArmyIndices)
                        if (state?.troop_counts != null && index > 0 && index < main.AllTroops.Count && index - 1 < state.troop_counts.Length && main.AllTroops[index] != state.troop_counts[index - 1])
                            main.AllTroops[index] = state.troop_counts[index - 1];
                    writtenArmyIndices.Clear();
                }
                if ((surfaces & UnitHudSurface.Recruitment) != 0) { HideRecruitmentControls(); RestoreRecruitmentText(); }
                if ((surfaces & UnitHudSurface.UnitDetails) != 0) HideUnitDetailControls();
                if (images && hasSpriteContext)
                {
                    if (IsImageOverrideContextReady())
                    {
                        updateSpritesActive = true;
                        try
                        {
                            updateSpritesOriginal(main, lastSpriteColour, lastSpriteArabic);
                            originalImages.Clear(); appliedImages.Clear();
                            if (activeImages) ApplyImageOverrides(main, lastSpriteColour, lastSpriteArabic);
                        }
                        finally { updateSpritesActive = false; }
                    }
                    else
                    {
                        // After map unload there may be no valid player for Vanilla's sprite update.
                        // Restore only properties still holding our exact image, never another writer's result.
                        foreach (var pair in appliedImages)
                            if (ReferenceEquals(GetImage(main, pair.Key), pair.Value)) SetImage(main, pair.Key, originalImages[pair.Key]);
                        originalImages.Clear(); appliedImages.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                lock (sync) { restoreSurfaces |= surfaces; restoreImages |= images; pendingPresentation = true; }
                LogCallbackFailure("activation restoration", ex);
            }
        }

        private void RestoreHover()
        {
            if (hoverMain != null && hoverMain.ChimpTypeText == hoverApplied) hoverMain.ChimpTypeText = hoverOriginal;
            hoverMain = null;
            hoverOriginal = hoverApplied = null;
        }

        private void TryApplyFrameArea(string area, Action action, Action failureCleanup = null)
        {
            try { action(); }
            catch (Exception ex)
            {
                try { failureCleanup?.Invoke(); }
                catch (Exception cleanupEx) { LogCallbackFailure(area + " cleanup", cleanupEx); }
                LogCallbackFailure(area, ex);
            }
        }

        private void ApplyHover(MainViewModel main)
        {
            if (!HasCategories(UnitHudSurface.UnitHover)) return;
            EngineInterface.PlayState state = GameData.Instance?.lastGameState;
            if (state == null || main == null || !TryCapture(state.in_chimp, out UnitHudUnitSnapshot unit)) { RestoreHover(); return; }
            CategoryRegistration category = Classify(unit, UnitHudSurface.UnitHover);
            if (category == null) { RestoreHover(); return; }
            if (!ReferenceEquals(main, hoverMain) || main.ChimpTypeText != hoverApplied) hoverOriginal = main.ChimpTypeText;
            hoverMain = main;
            hoverApplied = ResolveText(category, UnitHudTextKind.DisplayName);
            if (main.ChimpTypeText != hoverApplied) main.ChimpTypeText = hoverApplied;
        }

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
            int localPlayerId = GetSelectionPlayerId();
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
            hasRenderedTroopSelection = true;
        }

        private static bool TryGetTroopSelection(out LocalSelectionSnapshot selection)
        {
            int localPlayerId = GetSelectionPlayerId();
            return LocalSelectionAPI.TryCapture(localPlayerId, out selection);
        }

        private static int GetSelectionPlayerId() => APIShared.GameModes.GameModeHelper.IsMapEditor()
            ? EditorDirector.instance?.ActivePlayerID ?? -1
            : GamePlayerManagerAPI.Instance?.GetLocalPlayerId() ?? -1;

        private static bool TryCapture(int unitId, out UnitHudUnitSnapshot snapshot)
        {
            snapshot = null;
            if (unitId <= 0 || GameUnitManagerAPI.Instance == null || !APIShared.UnitAccess.TryGetById(unitId, out GameUnit* unit, out _) || unit == null || !APIShared.UnitAccess.IsReallyAlive(unit) || unit->r_GlobalId == 0) return false;
            snapshot = new UnitHudUnitSnapshot(unitId, unit->r_GlobalId, (int)unit->r_UnitChimp, unit->r_ControllableForPlayerId, true);
            return true;
        }

        private CategoryRegistration Classify(UnitHudUnitSnapshot unit, UnitHudSurface surface)
        {
            CategoryRegistration match = null;
            foreach (CategoryRegistration category in CategoryCopy())
            {
                lock (sync) { if (!category.Active || !OwnerActive(category.Owner)) continue; }
                if (!HasSurface(category, surface) || category.Definition.BaseUnitType != unit.VanillaType || !Claims(category, unit)) continue;
                if (match != null)
                {
                    string conflict = unit.GameId + ":" + unit.GlobalId + ":" + surface + ":" + match.Key + ":" + category.Key;
                    lock (sync)
                    {
                        if (loggedCategoryConflicts.Add(conflict))
                            NativeApiLog.Error(log, $"Unit HUD category conflict for unit={unit.GameId}, global={unit.GlobalId}: {match.Key} and {category.Key}; Vanilla category retained.");
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

        private void NotifyInteraction(UnitHudInteractionContext context)
        {
            InteractionRegistration[] copy; lock (sync) copy = interactionView;
            foreach (InteractionRegistration item in copy)
            {
                lock (sync) { if (!OwnerActive(item.Owner)) continue; }
                try { item.Handler(context); } catch (Exception ex) { LogCallbackFailure("interaction " + item.Owner + ":" + item.Id, ex); }
            }
        }

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
        private sealed class DisplayEntry
        {
            internal DisplayEntry(int type, int count) { VanillaType = type; Count = count; }
            internal DisplayEntry(CategoryRegistration category, List<UnitHudUnitSnapshot> units) { VanillaType = category.Definition.BaseUnitType; Category = category; Units = units; Count = units.Count; }
            internal int VanillaType { get; } internal int Count { get; } internal CategoryRegistration Category { get; } internal List<UnitHudUnitSnapshot> Units { get; }
        }
        private sealed class GroupEntry
        {
            internal GroupEntry(CategoryRegistration category, int summaryType, int baseType) { Category = category; SummaryType = summaryType; BaseType = baseType; }
            internal CategoryRegistration Category { get; } internal int SummaryType { get; } internal int BaseType { get; } internal int Count { get; set; }
            internal string SortKey => Category != null ? "1:" + Category.Key : "0:" + SummaryType.ToString("D2");
        }
    }
}
