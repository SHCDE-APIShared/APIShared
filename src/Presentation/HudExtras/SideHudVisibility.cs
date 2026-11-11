using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CrusaderDE;
using HarmonyLib;

namespace APIShared.Presentation
{
    /// <summary>Vanilla visibility of the in-game objectives and resources side HUD.</summary>
    public enum SideHudVisibilityState
    {
        /// <summary>The side HUD is unavailable for a reason other than the HUD mode alone.</summary>
        Hidden,
        /// <summary>The current in-game view allows the side HUD.</summary>
        Visible,
        /// <summary>The side HUD is eligible and only the HUD mode prevents it from appearing.</summary>
        HiddenByHudMode
    }

    /// <summary>Read-only access to the Vanilla side-HUD visibility rules.</summary>
    public static class SideHudVisibility
    {
        private static readonly Lazy<FieldInfo> ExtrasField = new Lazy<FieldInfo>(() => ResolveExtrasField(
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.Show_HUD_Extras))?.GetGetMethod(),
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.Show_InGameUI))?.GetGetMethod()));

        /// <summary>Reads the current view on the Unity thread. False means the view or validated
        /// game contract is unavailable. HiddenByHudMode means the side HUD could show with the
        /// HUD mode restored. Subscribe to the view's PropertyChanged to refresh an overlay.
        /// The query does not retain the view or change any game state.</summary>
        public static bool TryGetState(MainViewModel view, out SideHudVisibilityState state)
        {
            state = SideHudVisibilityState.Hidden;
            if (view == null || ExtrasField.Value is not FieldInfo field) return false;
            if (!view.Show_InGame || view.Show_HUD_Briefing || view.Show_HUD_MissionOver || view.Show_HUD_IngameMenu) return true;

            bool hudVisible = view.Show_InGameUI;
            bool extrasEnabled = hudVisible ? view.Show_HUD_Extras : (bool)field.GetValue(view);
            var controls = MainControls.instance;
            state = Classify(hudVisible, extrasEnabled, !hudVisible && controls != null && !controls.IsUIVisible);
            return true;
        }

        internal static SideHudVisibilityState Classify(bool hudVisible, bool extrasEnabled, bool hiddenByHudMode)
        {
            if (!extrasEnabled) return SideHudVisibilityState.Hidden;
            if (hudVisible) return SideHudVisibilityState.Visible;
            return hiddenByHudMode ? SideHudVisibilityState.HiddenByHudMode : SideHudVisibilityState.Hidden;
        }

        internal static FieldInfo ResolveExtrasField(MethodInfo extrasGetter, MethodInfo hudGetter)
        {
            if (extrasGetter == null || hudGetter == null) return null;
            try
            {
                var extras = PatchProcessor.GetOriginalInstructions(extrasGetter).ToArray();
                var hud = PatchProcessor.GetOriginalInstructions(hudGetter).ToArray();
                // Show_HUD_Extras returns the HUD getter's backing value when the independent
                // extras flag is set, and false otherwise. Resolve the actual fields from IL.
                if (extras.Length != 8 || hud.Length != 3 ||
                    !extras[0].IsLdarg(0) || extras[1].opcode != OpCodes.Ldfld ||
                    !(extras[2].opcode == OpCodes.Brfalse || extras[2].opcode == OpCodes.Brfalse_S) ||
                    !(extras[2].operand is Label target) || !extras[6].labels.Contains(target) ||
                    !extras[3].IsLdarg(0) || extras[4].opcode != OpCodes.Ldfld || extras[5].opcode != OpCodes.Ret ||
                    extras[6].opcode != OpCodes.Ldc_I4_0 || extras[7].opcode != OpCodes.Ret ||
                    !hud[0].IsLdarg(0) || hud[1].opcode != OpCodes.Ldfld || hud[2].opcode != OpCodes.Ret)
                    return null;

                var extrasField = extras[1].operand as FieldInfo;
                var hudField = hud[1].operand as FieldInfo;
                return IsPrivateBoolean(extrasField, extrasGetter.DeclaringType) &&
                    IsPrivateBoolean(hudField, hudGetter.DeclaringType) && !Equals(extrasField, hudField) &&
                    Equals(extras[4].operand, hudField) ? extrasField : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsPrivateBoolean(FieldInfo field, Type owner) => field != null && field.IsPrivate &&
            !field.IsStatic && field.DeclaringType == owner && field.FieldType == typeof(bool);
    }
}
