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
        private void TryBeginRecruitment(int baseType, int amount)
        {
            RecruitmentRegistration registration = GetActiveRecruitment(baseType);
            if (registration == null || amount <= 0) return;
            int playerId = GamePlayerManagerAPI.Instance?.GetLocalPlayerId() ?? 0;
            if (playerId <= 0) { ResetActiveRecruitment(baseType); return; }
            UnitHudRecruitmentTicket ticket;
            lock (sync)
            {
                if (recruitmentLease != null) return;
                ticket = new UnitHudRecruitmentTicket(++nextRecruitmentTicketId, registration.Category.Owner,
                    registration.Category.Definition.CategoryId, playerId, baseType, amount);
            }
            bool accepted = false;
            try { accepted = registration.Handler(ticket); }
            catch (Exception ex) { LogCallbackFailure("recruitment " + registration.Category.Key, ex); }
            lock (sync)
            {
                if (accepted) recruitmentLease = new RecruitmentLease(ticket, Time.realtimeSinceStartup + 10f);
                else
                {
                    activeRecruitment.Remove(baseType);
                    refreshRequested = true;
                }
            }
        }

        private RecruitmentRegistration GetActiveRecruitment(int baseType)
        {
            lock (sync)
            {
                if (!activeRecruitment.TryGetValue(baseType, out string key)) return null;
                return RecruitmentCopy(baseType).FirstOrDefault(x => x.Category.Key == key);
            }
        }

        private void ResetActiveRecruitment(int baseType)
        {
            lock (sync) { activeRecruitment.Remove(baseType); refreshRequested = true; }
        }

        private void ResetRecruitment()
        {
            lock (sync)
            {
                activeRecruitment.Clear();
                recruitmentLease = null;
                restoreSurfaces |= activeSurfaces;
                restoreImages |= activeImages;
                refreshRequested = activeSurfaces != UnitHudSurface.None || activeImages;
                pendingPresentation = restoreSurfaces != UnitHudSurface.None || restoreImages || refreshRequested;
            }
        }

        private readonly UnitHudHoverTextPresenter recruitmentHover = new UnitHudHoverTextPresenter();

        private void ApplyRecruitmentPresentation(MainViewModel main)
        {
            if (!activeRecruitmentHandlers || main?.HUDBuildingPanel == null) return;
            if (!main.Show_HUD_Building || main.HUDBuildingPanel.RefBarracksPanel.Visibility != Visibility.Visible) { HideRecruitmentControls(); return; }
            EnsureRecruitmentControls(main);
            RecruitmentRegistration active = GetActiveRecruitment((int)eChimps.CHIMP_TYPE_ARCHER);
            bool any = RecruitmentCopy((int)eChimps.CHIMP_TYPE_ARCHER).Length > 0;
            SetVisibility(archerVariantHost, any && main.Show_BarracksArcher ? Visibility.Visible : Visibility.Collapsed);
            string activeName = active == null ? "Vanilla Archer" : ResolveText(active.Category, UnitHudTextKind.DisplayName);
            string previousTip = "Previous unit variant\n" + activeName;
            string nextTip = "Next unit variant\n" + activeName;
            if (!Equals(archerVariantPrevious.ToolTip, previousTip)) archerVariantPrevious.ToolTip = previousTip;
            if (!Equals(archerVariantNext.ToolTip, nextTip)) archerVariantNext.ToolTip = nextTip;
            bool enabled = recruitmentLease == null;
            if (archerVariantPrevious.IsEnabled != enabled) archerVariantPrevious.IsEnabled = enabled;
            if (archerVariantNext.IsEnabled != enabled) archerVariantNext.IsEnabled = enabled;
            if (recruitmentHover.IsOwnedBy(main.HUDBuildingPanel))
                ShowRecruitmentHover(archerVariantPrevious.IsMouseOver ? archerVariantPrevious : archerVariantNext);
            if (active == null)
            {
                RestoreRecruitmentText();
                SetVisibility(archerVariantTint, Visibility.Collapsed);
                return;
            }
            ImageSource source = ResolveCategoryImage(active.Category, UnitHudSurface.Recruitment);
            ApplyTint(archerVariantTint, active.Category.Definition.Tint, source);
            SetVisibility(archerVariantTint, main.Show_BarracksArcher ? Visibility.Visible : Visibility.Collapsed);
            if (main.lastTroopBuildChimp == Enums.eChimps.CHIMP_TYPE_ARCHER) ApplyRecruitmentText(main, active);
        }

        private void ApplyRecruitmentText(MainViewModel main, RecruitmentRegistration active)
        {
            if (main != null && recruitmentHover.IsOwnedBy(main.HUDBuildingPanel)) return;
            if (active == null || main == null) { RestoreRecruitmentText(); return; }
            string suffix = main.lastTroopsAmountToMake > 1 ? " x" + main.lastTroopsAmountToMake : string.Empty;
            if (!ReferenceEquals(main, recruitmentTextMain) || main.TroopNameCostText != recruitmentTextApplied)
                recruitmentTextOriginal = main.TroopNameCostText;
            recruitmentTextMain = main;
            recruitmentTextApplied = ResolveText(active.Category, UnitHudTextKind.DisplayName) + suffix;
            if (main.TroopNameCostText != recruitmentTextApplied) main.TroopNameCostText = recruitmentTextApplied;
        }

        private void RestoreRecruitmentText()
        {
            if (recruitmentTextMain != null && recruitmentTextMain.TroopNameCostText == recruitmentTextApplied)
                recruitmentTextMain.TroopNameCostText = recruitmentTextOriginal;
            recruitmentTextMain = null;
            recruitmentTextOriginal = recruitmentTextApplied = null;
        }

        private void EnsureRecruitmentControls(MainViewModel main)
        {
            if (ReferenceEquals(recruitmentPanel, main.HUDBuildingPanel) && archerVariantHost != null) return;
            Grid host = RequireBuildingElement<Grid>(main, "APISharedArcherVariantHost");
            Button previous = RequireBuildingElement<Button>(main, "APISharedArcherVariantPrevious");
            Button next = RequireBuildingElement<Button>(main, "APISharedArcherVariantNext");
            Border tint = RequireBuildingElement<Border>(main, "APISharedArcherVariantTint");
            if (!ReferenceEquals(archerVariantPrevious, previous) || !ReferenceEquals(archerVariantNext, next))
            {
                if (archerVariantPrevious != null) archerVariantPrevious.PreviewMouseDown -= OnRecruitmentPreviousMouseDown;
                if (archerVariantNext != null) archerVariantNext.PreviewMouseDown -= OnRecruitmentNextMouseDown;
                archerVariantPrevious = previous;
                archerVariantNext = next;
                archerVariantPrevious.PreviewMouseDown += OnRecruitmentPreviousMouseDown;
                archerVariantNext.PreviewMouseDown += OnRecruitmentNextMouseDown;
            }
            recruitmentHover.Close();
            foreach (Button button in new[] { previous, next })
            {
                ToolTipService.SetIsEnabled(button, false);
                button.MouseEnter += (sender, args) => ShowRecruitmentHover(button);
                button.MouseLeave += (sender, args) => recruitmentHover.Close(button);
            }
            archerVariantHost = host;
            archerVariantTint = tint;
            recruitmentPanel = main.HUDBuildingPanel;
            if (!recruitmentControlsLogged)
            {
                recruitmentControlsLogged = true;
                NativeApiLog.Debug(log, "Unit HUD recruitment controls resolved: host, previous, next, tint.");
            }
        }

        private void ShowRecruitmentHover(Button button)
        {
            if (button == null || !MainViewModel.viewModelLoaded) return;
            MainViewModel main = MainViewModel.Instance;
            if (main?.HUDBuildingPanel == null || archerVariantHost?.Visibility != Visibility.Visible ||
                !main.Show_HUD_Building || main.HUDBuildingPanel.RefBarracksPanel.Visibility != Visibility.Visible) return;
            HUD_Buildings panel = main.HUDBuildingPanel;
            string original = null;
            recruitmentHover.Show(panel, button, button.ToolTip as string,
                () => ReferenceEquals(main.HUDBuildingPanel, panel) ? main.TroopNameCostText : null,
                value => { original = main.TroopNameCostText; main.TroopNameCostText = value; },
                () => main.TroopNameCostText = original);
        }
        private static T RequireBuildingElement<T>(MainViewModel main, string name) where T : class
        {
            T element = main?.HUDBuildingPanel?.FindName(name) as T;
            if (element == null) throw new MissingMemberException(name);
            return element;
        }

        private void HideRecruitmentControls()
        {
            recruitmentHover.Close();
            if (archerVariantHost != null) SetVisibility(archerVariantHost, Visibility.Collapsed);
        }

        private void OnRecruitmentPreviousMouseDown(object sender, MouseButtonEventArgs args) => ChangeRecruitmentVariant(args, -1);
        private void OnRecruitmentNextMouseDown(object sender, MouseButtonEventArgs args) => ChangeRecruitmentVariant(args, 1);

        private void ChangeRecruitmentVariant(MouseButtonEventArgs args, int direction)
        {
            if (args == null) return;
            if (args.ChangedButton != MouseButton.Left)
            {
                if (args.ChangedButton == MouseButton.Right) args.Handled = true;
                return;
            }
            const int baseType = (int)eChimps.CHIMP_TYPE_ARCHER;
            RecruitmentRegistration[] choices = RecruitmentCopy(baseType);
            if (choices.Length == 0) return;
            lock (sync)
            {
                if (recruitmentLease != null) { args.Handled = true; return; }
                int current = -1;
                if (activeRecruitment.TryGetValue(baseType, out string key)) current = Array.FindIndex(choices, x => x.Category.Key == key);
                int next = direction > 0 ? current + 1 : current < 0 ? choices.Length - 1 : current - 1;
                if (next < 0 || next >= choices.Length) activeRecruitment.Remove(baseType);
                else activeRecruitment[baseType] = choices[next].Category.Key;
                refreshRequested = true;
            }
            args.Handled = true;
        }

        private RecruitmentRegistration[] RecruitmentCopy(int baseType)
        {
            lock (sync) return recruitmentViews.TryGetValue(baseType, out RecruitmentRegistration[] view) ? view : Array.Empty<RecruitmentRegistration>();
        }
    }
}
