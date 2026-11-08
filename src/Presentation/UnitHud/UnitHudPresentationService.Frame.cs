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
        private void OnBeforeRender()
        {
            if (activeSurfaces == UnitHudSurface.None && !hasActionButtons && !pendingPresentation && !refreshRequested && recruitmentLease == null) return;
            try { OnActiveBeforeRender(); }
            finally { UpdateFrameDemand(); }
        }

        private void UpdateFrameDemand()
        {
            lock (sync)
            {
                if (frameWork == null) return;
                bool demand = activeSurfaces != UnitHudSurface.None || hasActionButtons ||
                    pendingPresentation || refreshRequested || recruitmentLease != null;
                if (demand == frameDemand) return;
                frameDemand = demand; frameWork.SetContinuous(demand);
            }
        }

        private void OnActiveBeforeRender()
        {
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
            TryApplyFrameArea("own-troop action buttons", () => ApplyActionButtons(main), FailClosedActionButtons);
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
                        DrawSprites(main, clearRecorded: false, applyOverrides: true);
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
                        DrawSprites(main, clearRecorded: true, applyOverrides: activeImages);
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
                lock (sync) { restoreSurfaces |= surfaces; restoreImages |= images; pendingPresentation = true; UpdateFrameDemand(); }
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
    }
}
