using System;

namespace APIShared
{
    internal sealed partial class BuildingPanelExtensionsService
    {
        // Script Extender and game members this service's own code binds; checked once at creation (issue #7).
        internal static unsafe ManagedMemberContract MemberContract() => new ManagedMemberContract()
            .Field(typeof(global::CrusaderDE.HUD_Buildings), "RefBarracksPanel", typeof(global::Noesis.Grid))
            .Field(typeof(global::CrusaderDE.HUD_Buildings), "RefBedouinStockadePanel", typeof(global::Noesis.Grid))
            .Field(typeof(global::CrusaderDE.HUD_Buildings), "RefHUDBuildingFullClickMask", typeof(global::Noesis.Grid))
            .Field(typeof(global::CrusaderDE.HUD_Buildings), "RefMercPostPanel", typeof(global::Noesis.Grid))
            .Field(typeof(global::CrusaderDE.HUD_Buildings), "RefShowCathedralPanel", typeof(global::Noesis.Grid))
            .Field(typeof(global::CrusaderDE.HUD_Buildings), "RefShowEngineersGuildPanel", typeof(global::Noesis.Grid))
            .Field(typeof(global::CrusaderDE.HUD_Buildings), "RefShowTunellersGuildPanel", typeof(global::Noesis.Grid))
            .Field(typeof(global::CrusaderDE.MainViewModel), "HUDBuildingPanel", typeof(global::CrusaderDE.HUD_Buildings))
            .Field(typeof(global::CrusaderDE.MainViewModel), "IsMapEditorMode", typeof(bool))
            .Field(typeof(global::CrusaderDE.MainViewModel), "viewModelLoaded", typeof(bool))
            .Field(typeof(global::EngineInterface.PlayState), "app_mode", typeof(int))
            .Field(typeof(global::EngineInterface.PlayState), "app_sub_mode", typeof(int))
            .Field(typeof(global::EngineInterface.PlayState), "in_structure", typeof(int))
            .Field(typeof(global::SHCDESE.Interop.GameBuilding), "r_AliveState", typeof(global::SHCDESE.Interop.Enums.AliveState))
            .Field(typeof(global::SHCDESE.Interop.GameBuilding), "r_BuildingType", typeof(global::SHCDESE.Interop.eStructs))
            .Field(typeof(global::SHCDESE.Interop.GameBuilding), "r_GlobalId", typeof(uint))
            .Field(typeof(global::SHCDESE.Interop.GameBuilding), "r_PlayerIdOwner", typeof(ushort))
            .Method(typeof(global::CrusaderDE.MainViewModel), "get_Instance", typeof(global::CrusaderDE.MainViewModel))
            .Method(typeof(global::CrusaderDE.MainViewModel), "get_Show_HUD_Building", typeof(bool))
            .Method(typeof(global::GameData), "get_Instance", typeof(global::GameData))
            .Method(typeof(global::GameData), "get_lastGameState", typeof(global::EngineInterface.PlayState))
            .Method(typeof(global::SHCDESE.API.GameBuildingManagerAPI), "get_Instance", typeof(global::SHCDESE.API.GameBuildingManagerAPI))
            .Method(typeof(global::SHCDESE.API.GameBuildingManagerAPI), "TryGetBuildingById", typeof(bool), typeof(int), typeof(global::SHCDESE.Interop.GameBuilding*).MakeByRefType())
            .Method(typeof(global::SHCDESE.API.GamePlayerManagerAPI), "get_Instance", typeof(global::SHCDESE.API.GamePlayerManagerAPI))
            .Method(typeof(global::SHCDESE.API.GamePlayerManagerAPI), "GetLocalPlayerId", typeof(int));
    }
}
