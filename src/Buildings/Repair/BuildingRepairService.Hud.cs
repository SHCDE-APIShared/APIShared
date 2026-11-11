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
        private void HudUpdateHook(FatControler self)
        {
            try
            {
                FlushErrors();
                if (Volatile.Read(ref activeOwners) == 0) { tooltip.Clear(); return; }
                if (!MainViewModel.viewModelLoaded) return;
                MainViewModel view = MainViewModel.Instance;
                if (view?.HUDmain == null || view.HUDBuildingPanel == null) return;
                Button big = view.HUDBuildingPanel.RefButtonRepair;
                if (big != null && !ReferenceEquals(big, lastBigButton))
                {
                    big.MouseEnter += OnBigButtonEnter;
                    big.MouseLeave += OnBigButtonLeave;
                    lastBigButton = big;
                }
                if (!postStartupLogged)
                {
                    postStartupLogged = true;
                    NativeApiLog.Debug(log, "Building repair capability executed after startup cleanup on the persistent HUD hook.");
                }
                if (hoveredButton == BigButtonId && (lastBigButton == null || !lastBigButton.IsMouseOver))
                    hoveredButton = null;
                RefreshTooltip();
            }
            catch (Exception ex)
            {
                tooltip.Clear();
                RecordError("Building repair HUD refresh failed: " + ex);
            }
        }

        private void OnBigButtonEnter(object sender, MouseEventArgs args) => BeginHover(BigButtonId);
        private void OnBigButtonLeave(object sender, MouseEventArgs args) => EndHover(BigButtonId);

        private void BeginHover(string buttonId)
        {
            if (string.IsNullOrEmpty(buttonId)) return;
            hoveredButton = buttonId;
            try
            {
                if (buttonId != BigButtonId)
                    MainViewModel.Instance.ButtonRepairMouseEnterCommand.Execute(null);
                RefreshTooltip();
            }
            catch (Exception ex) { RecordError("Building repair hover failed: " + ex); tooltip.Clear(); }
        }

        private void EndHover(string buttonId)
        {
            if (!string.Equals(hoveredButton, buttonId, StringComparison.Ordinal)) return;
            hoveredButton = null;
            try
            {
                if (buttonId != BigButtonId)
                    MainViewModel.Instance.ButtonRepairMouseLeaveCommand.Execute(null);
                tooltip.Clear();
            }
            catch (Exception ex) { RecordError("Building repair hover cleanup failed: " + ex); }
        }

        private void RefreshTooltip()
        {
            if (hoveredButton == null || !TryGetSelectedQuote(out BuildingRepairQuote quote) ||
                quote.CurrentHealth >= quote.MaxHealth)
            {
                tooltip.Clear();
                return;
            }
            tooltip.Show(quote);
            TooltipPresentation.EnsureRepairPresentation();
        }
    }
}
