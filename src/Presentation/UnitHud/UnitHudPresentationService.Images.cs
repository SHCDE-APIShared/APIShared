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
        private void ApplyImageOverrides(MainViewModel self, int colour, bool arabic)
        {
            ImageRegistration[] registrations;
            lock (sync) registrations = imageView;
            foreach (UnitHudImageSlot slot in ImageSlots)
            {
                ImageSource vanilla = GetImage(self, slot);
                ImageSource current = vanilla;
                foreach (ImageRegistration registration in registrations)
                {
                    if (registration.Definition.Slot != slot) continue;
                    lock (sync) { if (!registration.Active || !OwnerActive(registration.Owner)) continue; }
                    try
                    {
                        ImageSource next = registration.Resolver(new UnitHudImageOverrideContext(colour, arabic, slot, vanilla, current));
                        if (next != null) current = next;
                    }
                    catch (Exception ex) { LogCallbackFailure("image override " + registration.Owner + ":" + registration.Definition.OverrideId, ex); }
                }
                if (!ReferenceEquals(current, vanilla))
                {
                    originalImages[slot] = vanilla;
                    appliedImages[slot] = current;
                    SetImage(self, slot, current);
                }
            }
        }

        private static bool IsImageOverrideContextReady()
        {
            int localPlayerId = GamePlayerManagerAPI.Instance?.GetLocalPlayerId() ?? 0;
            if (localPlayerId > 0) return true;
            return EditorDirector.instance != null && EditorDirector.instance.ActivePlayerID > 0;
        }

        private ImageSource ResolveCategoryImage(
            CategoryRegistration category,
            UnitHudSurface surface,
            HUD_Troops troopPanel = null,
            HUD_ControlGroups groupPanel = null)
        {
            try
            {
                ImageSource source = category.Definition.ImageResolver?.Invoke();
                if (source != null) return source;
            }
            catch (Exception ex) { LogCallbackFailure("category image " + category.Key, ex); }
            MainViewModel main = MainViewModel.Instance;
            if (category.Definition.BaseUnitType == (int)eChimps.CHIMP_TYPE_ARCHER)
            {
                if (surface == UnitHudSurface.TroopSelection)
                {
                    Button vanillaButton = troopPanel?.FindName("ArchersSelected") as Button;
                    ImageSource source = vanillaButton == null ? null : PropEx.GetSprite1(vanillaButton) as ImageSource;
                    return source ?? main?.UIButtonsK023;
                }
                if (surface == UnitHudSurface.Recruitment)
                {
                    ToggleButton vanillaButton = main?.HUDBuildingPanel?.RefRecruitArcherButton;
                    ImageSource source = vanillaButton == null ? null : PropEx.GetSprite1(vanillaButton) as ImageSource;
                    return source ?? main?.UIButtonsO001;
                }
            }
            int summary = ToSummaryType(category.Definition.BaseUnitType);
            HUD_ControlGroups resolver = groupPanel ?? main?.HUDControlGroups;
            if (summary >= 0 && resolver != null)
                return GroupSpriteMethod.Invoke(resolver, new object[] { summary }) as ImageSource;
            return main?.UIBuildingsO001;
        }

        private static void ApplyButtonImage(Button button, ImageSource source)
        {
            PropEx.SetSprite1(button, source); PropEx.SetSprite2(button, source); PropEx.SetSprite3(button, source); PropEx.SetSprite4(button, source);
        }

        private static void ApplyTroopButtonImages(Button target, CategoryRegistration category, HUD_Troops panel, ImageSource fallback)
        {
            if (category.Definition.ImageResolver != null ||
                category.Definition.BaseUnitType != (int)eChimps.CHIMP_TYPE_ARCHER)
            {
                ApplyButtonImage(target, fallback);
                return;
            }
            Button vanilla = panel?.FindName("ArchersSelected") as Button;
            if (vanilla == null)
            {
                ApplyButtonImage(target, fallback);
                return;
            }
            PropEx.SetSprite1(target, PropEx.GetSprite1(vanilla) as ImageSource ?? fallback);
            PropEx.SetSprite2(target, PropEx.GetSprite2(vanilla) as ImageSource ?? fallback);
            PropEx.SetSprite3(target, PropEx.GetSprite3(vanilla) as ImageSource ?? fallback);
            PropEx.SetSprite4(target, PropEx.GetSprite4(vanilla) as ImageSource ?? fallback);
        }

        private static void SetPageButtons(HUD_Troops panel, int page, int pages)
        {
            Button next = panel.FindName("ButtonTroopPanelPage1") as Button;
            Button previous = panel.FindName("ButtonTroopPanelPage2") as Button;
            if (next != null) PropEx.SetButtonVisibility(next, pages > 1 && page < pages - 1 ? Visibility.Visible : Visibility.Hidden);
            if (previous != null) PropEx.SetButtonVisibility(previous, pages > 1 && page > 0 ? Visibility.Visible : Visibility.Hidden);
        }

        private static ImageSource GetImage(MainViewModel main, UnitHudImageSlot slot)
        {
            switch (slot)
            {
                case UnitHudImageSlot.UIBuildingsO011: return main.UIBuildingsO011;
                case UnitHudImageSlot.UIBuildingsO012: return main.UIBuildingsO012;
                case UnitHudImageSlot.UIButtonsK007: return main.UIButtonsK007;
                case UnitHudImageSlot.UIButtonsK008: return main.UIButtonsK008;
                case UnitHudImageSlot.UIButtonsO016: return main.UIButtonsO016;
                case UnitHudImageSlot.UIButtonsO017: return main.UIButtonsO017;
                case UnitHudImageSlot.UIButtonsO018: return main.UIButtonsO018;
                default: return null;
            }
        }

        private static void SetImage(MainViewModel main, UnitHudImageSlot slot, ImageSource image)
        {
            switch (slot)
            {
                case UnitHudImageSlot.UIBuildingsO011: main.UIBuildingsO011 = image; break;
                case UnitHudImageSlot.UIBuildingsO012: main.UIBuildingsO012 = image; break;
                case UnitHudImageSlot.UIButtonsK007: main.UIButtonsK007 = image; break;
                case UnitHudImageSlot.UIButtonsK008: main.UIButtonsK008 = image; break;
                case UnitHudImageSlot.UIButtonsO016: main.UIButtonsO016 = image; break;
                case UnitHudImageSlot.UIButtonsO017: main.UIButtonsO017 = image; break;
                case UnitHudImageSlot.UIButtonsO018: main.UIButtonsO018 = image; break;
            }
        }
    }
}
