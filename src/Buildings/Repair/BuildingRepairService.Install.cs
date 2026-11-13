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
        // Script Extender and game members this service's own code binds; checked once at creation (issue #7).
        internal static ManagedMemberContract MemberContract() => new ManagedMemberContract()
            .Field(typeof(global::CrusaderDE.HUD_Buildings), "RefButtonRepair", typeof(global::Noesis.Button))
            .Field(typeof(global::CrusaderDE.MainViewModel), "HUDBuildingPanel", typeof(global::CrusaderDE.HUD_Buildings))
            .Field(typeof(global::CrusaderDE.MainViewModel), "HUDmain", typeof(global::CrusaderDE.HUD_Main))
            .Field(typeof(global::CrusaderDE.MainViewModel), "viewModelLoaded", typeof(bool))
            .Field(typeof(global::EngineInterface.PlayState), "app_mode", typeof(int))
            .Field(typeof(global::EngineInterface.PlayState), "app_sub_mode", typeof(int))
            .Field(typeof(global::EngineInterface.PlayState), "building_hps_for_repair", typeof(short))
            .Field(typeof(global::EngineInterface.PlayState), "building_maxhps_for_repair", typeof(short))
            .Field(typeof(global::EngineInterface.PlayState), "can_do_repairs", typeof(short))
            .Field(typeof(global::EngineInterface.PlayState), "game_type", typeof(byte))
            .Field(typeof(global::EngineInterface.PlayState), "in_structure", typeof(int))
            .Field(typeof(global::EngineInterface.PlayState), "in_structure_type", typeof(int))
            .Field(typeof(global::EngineInterface.PlayState), "keep_storage", typeof(int[]))
            .Field(typeof(global::EngineInterface.PlayState), "repair_stone_needed", typeof(int))
            .Field(typeof(global::EngineInterface.PlayState), "repair_wood_needed", typeof(int))
            .Field(typeof(global::EngineInterface.PlayState), "resources", typeof(int[]))
            .Field(typeof(global::SHCDESE.EventAPI.BuildingR3EventHooks), "OnBuildingRepair", typeof(global::SHCDESE.EventAPI.R3EventHook<global::SHCDESE.EventAPI.Buildings.BuildingRepairEventArgs>))
            .Field(typeof(global::SHCDESE.Interop.GameBuilding), "r_AliveState", typeof(global::SHCDESE.Interop.Enums.AliveState))
            .Field(typeof(global::SHCDESE.Interop.GameBuilding), "r_BuildingType", typeof(global::SHCDESE.Interop.eStructs))
            .Field(typeof(global::SHCDESE.Interop.GameBuilding), "r_CurrentHealth", typeof(short))
            .Field(typeof(global::SHCDESE.Interop.GameBuilding), "r_GlobalId", typeof(uint))
            .Field(typeof(global::SHCDESE.Interop.GameBuilding), "r_MaxHealth", typeof(ushort))
            .Field(typeof(global::SHCDESE.Interop.GameBuilding), "r_PlayerIdOwner", typeof(ushort))
            .Method(typeof(global::CrusaderDE.DelegateCommand), "Execute", typeof(void), typeof(object))
            .Method(typeof(global::CrusaderDE.MainViewModel), "get_ButtonRepairMouseEnterCommand", typeof(global::CrusaderDE.DelegateCommand))
            .Method(typeof(global::CrusaderDE.MainViewModel), "get_ButtonRepairMouseLeaveCommand", typeof(global::CrusaderDE.DelegateCommand))
            .Method(typeof(global::CrusaderDE.MainViewModel), "get_Instance", typeof(global::CrusaderDE.MainViewModel))
            .Method(typeof(global::GameData), "get_Instance", typeof(global::GameData))
            .Method(typeof(global::GameData), "get_lastGameState", typeof(global::EngineInterface.PlayState))
            .Method(typeof(global::SHCDESE.API.GameBuildingManagerAPI), "get_Instance", typeof(global::SHCDESE.API.GameBuildingManagerAPI))
            .Method(typeof(global::SHCDESE.API.GameBuildingManagerAPI), "GetBuildingManager", typeof(global::RedBird.Core.Memory.NativePointer<global::SHCDESE.Interop.GameBuildingManager>))
            .Method(typeof(global::SHCDESE.API.GameBuildingManagerAPI), "GetGoldCost", typeof(int), typeof(global::SHCDESE.Interop.eStructs))
            .Method(typeof(global::SHCDESE.API.GameBuildingManagerAPI), "GetIronIngotCost", typeof(int), typeof(global::SHCDESE.Interop.eStructs))
            .Method(typeof(global::SHCDESE.API.GameBuildingManagerAPI), "GetRawPitchCost", typeof(int), typeof(global::SHCDESE.Interop.eStructs))
            .Method(typeof(global::SHCDESE.API.GameBuildingManagerAPI), "IsValidId", typeof(bool), typeof(int))
            .Method(typeof(global::SHCDESE.API.GameBuildingManagerAPI), "TryGetBuildingById", typeof(bool), typeof(int), typeof(global::SHCDESE.Interop.GameBuilding*).MakeByRefType())
            .Method(typeof(global::SHCDESE.API.GamePlayerManagerAPI), "AddPlayerGold", typeof(bool), typeof(int), typeof(int))
            .Method(typeof(global::SHCDESE.API.GamePlayerManagerAPI), "get_Instance", typeof(global::SHCDESE.API.GamePlayerManagerAPI))
            .Method(typeof(global::SHCDESE.API.GamePlayerManagerAPI), "GetGoodAmount", typeof(int), typeof(int), typeof(global::SHCDESE.Interop.eGoods))
            .Method(typeof(global::SHCDESE.API.GamePlayerManagerAPI), "GetLocalPlayerId", typeof(int))
            .Method(typeof(global::SHCDESE.API.GamePlayerManagerAPI), "HasGoodsAmount", typeof(bool), typeof(int), typeof(global::SHCDESE.Interop.eGoods), typeof(int))
            .Method(typeof(global::SHCDESE.API.GamePlayerManagerAPI), "RemoveGood", typeof(void), typeof(int), typeof(global::SHCDESE.Interop.eGoods), typeof(int), typeof(bool))
            .Method(typeof(global::SHCDESE.API.GamePlayerManagerAPI), "TryGetPlayerResourcesById", typeof(bool), typeof(int), typeof(global::SHCDESE.Interop.GamePlayerResources*).MakeByRefType())
            .Method(typeof(global::SHCDESE.EventAPI.Buildings.BuildingRepairEventArgs), "get_BuildingGlobalId", typeof(int))
            .Method(typeof(global::SHCDESE.EventAPI.Buildings.BuildingRepairEventArgs), "get_BuildingId", typeof(int))
            .Method(typeof(global::SHCDESE.EventAPI.Buildings.BuildingRepairEventArgs), "get_PlayerId", typeof(int))
            .Method(typeof(global::SHCDESE.EventAPI.EventHookBase), "get_Phase", typeof(global::SHCDESE.EventAPI.EventHookPhase))
            .Method(typeof(global::SHCDESE.EventAPI.EventHookBase), "get_SkipOriginalFunction", typeof(bool))
            .Method(typeof(global::SHCDESE.EventAPI.EventHookBase), "set_SkipOriginalFunction", typeof(void), typeof(bool))
            .Method(typeof(global::SHCDESE.EventAPI.R3EventHook<global::SHCDESE.EventAPI.Buildings.BuildingRepairEventArgs>), "get_Observable", typeof(global::R3.Observable<global::SHCDESE.EventAPI.Buildings.BuildingRepairEventArgs>));

        // ownership receives the validated, called entry (null: not registered).
        internal static bool TryCreate(string hash, long moduleBase, ManualLogSource log,
            RepairTooltipViewModel tooltipViewModel, NativeOwnershipRegistry ownership,
            out BuildingRepairService service, out NativeCapabilityDiagnostic diagnostic)
        {
            service = null;
            if (!string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase) ||
                moduleBase == 0)
            {
                diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.BuildingRepair,
                    NativeCapabilityState.UnsupportedBuild, hash, "The installed native repair build is not verified.");
                return false;
            }
            if (!ManagedMemberContracts.TryValidate(NativeCapabilityIds.BuildingRepair, log, out diagnostic))
                return false;

            Hook candidateCopy = null;
            IDisposable candidateSubscription = null;
            try
            {
                byte[] actual = new byte[CanRepairEntry.Length];
                Marshal.Copy(new IntPtr(checked(moduleBase + CanRepairRva)), actual, 0, actual.Length);
                for (int index = 0; index < actual.Length; index++)
                    if (actual[index] != CanRepairEntry[index])
                        throw new InvalidOperationException("Native repair entry does not match the audited function.");
                ApiSharedNativeSites.Claim(ownership, ApiSharedNativeSites.BuildingRepair, ApiSharedNativeSites.BuildingRepairSpans(),
                    reason => new InvalidOperationException(reason));

                BuildingRepairService candidate = new BuildingRepairService(moduleBase, log, tooltipViewModel);
                MethodInfo copyTarget = FindMethod(typeof(EngineInterface), "CopyPlayStateStruct",
                    BindingFlags.Public | BindingFlags.Static,
                    typeof(EngineInterface.PlayStateReturnData), typeof(int[]));
                candidateCopy = new Hook(copyTarget, (CopyStateDelegate)candidate.CopyStateHook);
                candidate.originalCopy = candidateCopy.GenerateTrampoline<CopyStateDelegate>();
                candidateSubscription = BuildingR3EventHooks.OnBuildingRepair.Observable.Subscribe(candidate.OnBuildingRepair);
                candidate.copyHook = candidateCopy;
                candidate.repairSubscription = candidateSubscription;
                if (!PresentationEvents.TryRegister(PresentationOperation.GuiChecks, APISharedPlugin.PluginGuid,
                    "BuildingRepair", null, args => { if (candidate.published && args.OriginalCompleted) candidate.HudUpdateHook(args.Controller); }, out string reason))
                    throw new InvalidOperationException(reason);
                candidate.published = true;
                service = candidate;
                diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.BuildingRepair,
                    NativeCapabilityState.Available, hash, "Verified Vanilla repair path and shared repair handlers installed.");
                return true;
            }
            catch (Exception ex)
            {
                // These candidates have not been published to the process-wide API.
                try { candidateSubscription?.Dispose(); } catch { }
                try { candidateCopy?.Undo(); } catch { }
                try { candidateCopy?.Dispose(); } catch { }
                ApiSharedNativeSites.Release(ownership, ApiSharedNativeSites.BuildingRepair);
                diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.BuildingRepair,
                    NativeCapabilityState.ValidationFailed, hash, ex.Message);
                NativeApiLog.Error(log, $"Building repair capability unavailable: {ex}");
                return false;
            }
        }

        private static MethodInfo FindMethod(Type owner, string name, BindingFlags flags, params Type[] parameters)
        {
            MethodInfo method = owner.GetMethod(name, flags, null, parameters, null);
            if (method == null) throw new MissingMethodException(owner.FullName, name);
            return method;
        }
    }
}
