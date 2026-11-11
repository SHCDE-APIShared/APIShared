using System;
using System.Collections.Generic;
using System.Reflection;
using APIShared.Internal;
using APIShared.Localization;
using APIShared.Presentation;
using CrusaderDE;
using Noesis;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    // The shared GuiChecks publisher outlives the early BepInEx component. Only the
    // controls belonging to the current HUD instance are retained and event-bound.
    internal static partial class WeaponProductionHud
    {
        private static PanelView view;
        private static HUD_Buildings failedHud;
        private static object engine;
        private static bool initialized, observed;

        internal static bool TryInitialize(out string reason)
        {
            reason = null;
            if (initialized) return true;
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Weapon production HUD must initialize on Unity."; return false; }

            var contract = new ManagedMemberContract()
                .Field(typeof(MainViewModel), nameof(MainViewModel.viewModelLoaded), typeof(bool))
                .Field(typeof(MainViewModel), nameof(MainViewModel.HUDmain), typeof(HUD_Main))
                .Field(typeof(MainViewModel), nameof(MainViewModel.HUDBuildingPanel), typeof(HUD_Buildings))
                .Method(typeof(MainViewModel), "get_Instance", typeof(MainViewModel))
                .Method(typeof(MainViewModel), "get_Show_HUD_Building", typeof(bool))
                .Method(typeof(GameData), "get_Instance", typeof(GameData))
                .Method(typeof(GameData), "get_lastGameState", typeof(EngineInterface.PlayState))
                .Field(typeof(EngineInterface.PlayState), nameof(EngineInterface.PlayState.app_sub_mode), typeof(int))
                .Field(typeof(EngineInterface.PlayState), nameof(EngineInterface.PlayState.in_structure), typeof(int))
                .Field(typeof(EngineInterface.PlayState), nameof(EngineInterface.PlayState.production_no_resources), typeof(byte))
                .Field(typeof(KeyManager), nameof(KeyManager.instance), typeof(KeyManager))
                .Method(typeof(KeyManager), nameof(KeyManager.isShiftDown), typeof(bool))
                .Field(typeof(HUD_Main), nameof(HUD_Main.OtherString), typeof(string))
                .Method(typeof(HUD_Main), nameof(HUD_Main.SetRolloverOtherString), typeof(void),
                    typeof(string), typeof(int), typeof(int), typeof(string))
                .Method(typeof(Translate), "get_Instance", typeof(Translate))
                .Method(typeof(Translate), nameof(Translate.lookUpText), typeof(string), typeof(Enums.eTextSections), typeof(int));
            var members = new List<MemberInfo>();
            reason = contract.FindMismatch(members);
            if (reason != null) return false;
            foreach (MemberInfo member in members)
            {
                if ((member is FieldInfo field && !field.IsPublic) || (member is MethodInfo method && !method.IsPublic))
                { reason = "Weapon production HUD requires a public game member: " + member; return false; }
            }
            engine = EngineMonitor.Current;
            if (engine == null) { reason = "Weapon production HUD needs the engine monitor."; return false; }

            initialized = PresentationEvents.TryRegisterWhenActive(PresentationOperation.GuiChecks,
                APISharedPlugin.PluginGuid, "weapon-production-hud", null, OnGuiChecks, out reason, 0, HasDemand);
            return initialized;
        }

        // One final pass restores the Vanilla controls after the active catalog is retired.
        private static bool HasDemand() => WeaponProductionRuntime.HasHudDemand || view != null || failedHud != null;

        private static void OnGuiChecks(PresentationPostEventArgs args)
        {
            if (!args.OriginalCompleted || !UnityMainThreadDispatch.IsMainThread) return;
            if (!WeaponProductionRuntime.HasHudDemand)
            {
                failedHud = null;
                ReleaseView();
                return;
            }
            MainViewModel main = MainViewModel.viewModelLoaded ? MainViewModel.Instance : null;
            HUD_Buildings hud = main?.HUDBuildingPanel;
            if (view != null && !ReferenceEquals(view.Hud, hud)) ReleaseView();
            if (failedHud != null && !ReferenceEquals(failedHud, hud)) failedHud = null;
            if (hud == null || ReferenceEquals(failedHud, hud)) return;

            try
            {
                if (!TryReadWorkshop(main, out Target target, out var options, out int allowed, out var current, out var next))
                { view?.Hide(); return; }

                if (view == null) view = new PanelView(hud);
                view.Show(main.HUDmain, target, options, allowed, current, next);
                if (!observed)
                {
                    observed = true;
                    NativeApiLog.Info(ApiSharedRuntime.ProcessInstance.Logger, "Weapon production HUD showed its first active workshop.");
                }
            }
            catch (Exception error)
            {
                view?.Hide();
                failedHud = hud;
                NativeApiLog.Error(ApiSharedRuntime.ProcessInstance.Logger, "Weapon production HUD could not update: " + error);
            }
        }

        private static void ReleaseView()
        {
            PanelView old = view;
            view = null;
            old?.Unbind();
        }

        // Reads the workshop, its permitted slots and its selection under one engine lock;
        // the Unity-side checks before it need no lock.
        private static bool TryReadWorkshop(MainViewModel main, out Target target,
            out IReadOnlyList<WeaponProductionOption> options, out int allowed,
            out WeaponProductionOption current, out WeaponProductionOption next)
        {
            target = default; options = null; allowed = 0; current = next = null;
            if (!TryReadTargetType(main, out EngineInterface.PlayState state, out eStructs type)) return false;
            lock (engine)
            {
                if (!TryReadTarget(state, type, out target)) return false;
                options = WeaponProduction.GetOptions(target.Type);
                return TryGetAllowedSlots(options, out allowed) &&
                    WeaponProductionRuntime.TryGetSelection(target.Id, target.GlobalId, out current, out next);
            }
        }

        // Caller holds the engine lock.
        private static bool TryGetAllowedSlots(IReadOnlyList<WeaponProductionOption> options, out int allowed)
        {
            allowed = 0;
            if (options == null || options.Count == 0 || options.Count > 4) return false;
            bool extra = false;
            for (int i = 0; i < options.Count; ++i)
                if (WeaponProductionRuntime.IsOptionAllowed(options[i]))
                {
                    allowed |= 1 << i;
                    extra |= !options[i].IsVanilla;
                }
            return extra;
        }

        private static bool TryReadTarget(MainViewModel main, out Target target)
        {
            target = default;
            if (!TryReadTargetType(main, out EngineInterface.PlayState state, out eStructs type)) return false;
            lock (engine) return TryReadTarget(state, type, out target);
        }

        private static bool TryReadTargetType(MainViewModel main, out EngineInterface.PlayState state, out eStructs type)
        {
            state = null; type = default;
            if (main?.HUDmain == null || main.HUDBuildingPanel == null || !main.Show_HUD_Building) return false;
            state = GameData.Instance?.lastGameState;
            if (state == null || state.in_structure <= 0) return false;
            switch ((Enums.InBuildingModes)state.app_sub_mode)
            {
                case Enums.InBuildingModes.INSIDE_FLETCHERS_WORKSHOP: type = eStructs.STRUCT_FLETCHERS_WORKSHOP; break;
                case Enums.InBuildingModes.INSIDE_POLETURNERS_WORKSHOP: type = eStructs.STRUCT_POLETURNERS_WORKSHOP; break;
                case Enums.InBuildingModes.INSIDE_BLACKSMITHS_WORKSHOP: type = eStructs.STRUCT_BLACKSMITHS_WORKSHOP; break;
                case Enums.InBuildingModes.INSIDE_ARMOURERS_WORKSHOP: type = eStructs.STRUCT_ARMOURERS_WORKSHOP; break;
                case Enums.InBuildingModes.INSIDE_TANNERS_WORKSHOP: type = eStructs.STRUCT_TANNERS_WORKSHOP; break;
                default: return false;
            }
            return true;
        }

        // Caller holds the engine lock.
        private static unsafe bool TryReadTarget(EngineInterface.PlayState state, eStructs type, out Target target)
        {
            target = default;
            int player = PlayerPerspectiveAPI.GetControlledPlayerId();
            if (player <= 0 || PlayerPerspectiveAPI.GetViewedPlayerId() != player) return false;
            var manager = GameBuildingManagerAPI.Instance;
            if (manager == null || !manager.TryGetBuildingById(state.in_structure, out GameBuilding* building) || building == null ||
                building->r_AliveState != AliveState.IsAlive || building->r_BuildingType != type ||
                building->r_PlayerIdOwner != player || building->r_GlobalId == 0) return false;
            target = new Target(state.in_structure, building->r_GlobalId, type, state.production_no_resources != 0);
            return true;
        }

        private readonly struct Target
        {
            internal Target(int id, uint globalId, eStructs type, bool lackingResources)
            { Id = id; GlobalId = globalId; Type = type; LackingResources = lackingResources; }
            internal readonly int Id;
            internal readonly uint GlobalId;
            internal readonly eStructs Type;
            internal readonly bool LackingResources;
            internal bool Matches(Target other) => Id == other.Id && GlobalId == other.GlobalId && Type == other.Type;
        }
    }
}
