// External game/event boundaries only. The service, registry, archive and arithmetic are production
// source links; this fixture does not assert native struct layout or native-hook execution.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Health;
namespace BepInEx.Logging { public class ManualLogSource { } }
namespace R3 { public static class ObservableExtensions { public static IDisposable Subscribe<T>(this Observable<T> value,Action<T> callback) => value.Subscribe(callback); } public sealed class Observable<T> { private event Action<T> callback; public IDisposable Subscribe(Action<T> value) { callback+=value; return new Token(); } public void Raise(T args) => callback?.Invoke(args); private sealed class Token:IDisposable { public void Dispose() {} } } }
namespace SHCDESE.Interop.Enums { public enum AliveState { None,NeedsInit,IsAlive,MarkedForDeletion } }
namespace SHCDESE.Interop
{
    public enum eChimps { CHIMP_TYPE_NULL,CHIMP_TYPE_PEASANT,CHIMP_TYPE_ARCHER,CHIMP_TYPE_LORD,CHIMP_TYPE_SIEGE_TOWER }
    public enum eStructs { STRUCT_NULL,STRUCT_HOVEL,STRUCT_TOWER,STRUCT_SIEGE_TOWER,STRUCT_DRAWBRIDGE }
    public struct GameUnit { public SHCDESE.Interop.Enums.AliveState r_AliveState; public uint r_GlobalId,r_CurrentHealth,r_MaxHealth,r_HealthBarBlocks; public eChimps r_UnitChimp; public ushort r_ControllableForPlayerId,r_CurrentHealthPercentage,r_IsKilledByProjectile,r_LinkedProductionBuildingId; public uint r_LinkedProductionBuildingGlobalId; }
    public struct GameBuilding { public SHCDESE.Interop.Enums.AliveState r_AliveState; public uint r_GlobalId; public ushort r_PlayerIdOwner,r_MaxHealth,N00001757; public short r_CurrentHealth; public eStructs r_BuildingType; public uint N000017CB; }
}
namespace SHCDESE.API.LowLevel { public sealed class CrusaderLibraryLoadContext { } }
namespace SHCDESE.EventAPI
{
    public enum EventHookPhase { Pre,Post }
    public static class BuildingR3EventHooks { public static readonly Event<Buildings.BuildingSpawnEventArgs> OnBuildingSpawn=new Event<Buildings.BuildingSpawnEventArgs>(); }
    public sealed class Event<T> { public readonly R3.Observable<T> Observable=new R3.Observable<T>(); }
}
namespace SHCDESE.EventAPI.Buildings { public class BuildingSpawnEventArgs { public SHCDESE.EventAPI.EventHookPhase Phase; public long ReturnValue; } }
namespace SHCDESE.API.Components.SaveData { public class SaveContext { public bool IsSaveFile=true,IsMapEditorSave; } public class LoadContext {} }
namespace SHCDESE.API
{
    using SHCDESE.Interop;
    using SHCDESE.API.Components.SaveData;
    public sealed class GameUnitManagerAPI { public static readonly GameUnitManagerAPI Instance=new GameUnitManagerAPI(); public Span<GameUnit> GetUnitsAsSpan() => World.Units; }
    public sealed unsafe class GameBuildingManagerAPI
    {
        public static readonly GameBuildingManagerAPI Instance=new GameBuildingManagerAPI();
        public Span<GameBuilding> GetBuildingsAsSpan()=>World.Buildings;
        public bool IsValidId(int id) => id > 0 && id <= World.Buildings.Length;
        public bool TryGetBuildingById(int id,out GameBuilding* result) { result=id>0&&id<=World.Buildings.Length ? World.BuildingPointer+id-1:null;return result!=null; }
        public int GetDefaultHealth(eStructs type)=>type==eStructs.STRUCT_SIEGE_TOWER ? World.ProxyBasis:1000;
        public int GetWoodCost(eStructs type)=>World.Wood;
        public int GetStoneCost(eStructs type)=>World.Stone;
    }
    public sealed class GamePlayerManagerAPI { public const int MAX_PLAYERS=8; public static readonly GamePlayerManagerAPI Instance=new GamePlayerManagerAPI(); public bool IsAIPlayer(int player)=>player>1; }
    public sealed class GameMapArchiveManagerAPI { public static readonly GameMapArchiveManagerAPI Instance=new GameMapArchiveManagerAPI(); public object GetMapArchive()=>World.Archive; }
    public sealed class ModSaveDataAPI
    {
        public static readonly ModSaveDataAPI Instance=new ModSaveDataAPI();
        public Func<SaveContext,byte[]> Save; public Action<byte[],LoadContext> Load;
        public bool RegisterModDataHandler(string id,Func<SaveContext,byte[]> save,Action<byte[],LoadContext> load,Action unload) { Save=save;Load=load;return true; }
    }
}
namespace APIShared
{
    public static class APISharedPlugin { public const string PluginGuid="APIShared_Serp"; }
    public sealed class NativeCapabilityDiagnostic { public override string ToString()=>"fixture"; }
    public enum MissionInitializationPhase { BeforeLoad,BeforeNativeStart,NativeLoaded }
    public sealed class MissionContext { public bool IsSave; public long SessionId; }
    public sealed class MissionLifecycleNotification { public bool IsReplay;public MissionContext Context;public MissionInitializationPhase Phase; }
    public interface IMissionLifecycleCapability { bool TryRegisterObserver(string id,Action<MissionLifecycleNotification> start,Action<MissionLifecycleNotification> end,Action<MissionLifecycleNotification> initialization,out NativeCapabilityDiagnostic reason); }
    public sealed class MissionBoundary:IMissionLifecycleCapability
    {
        public Action<MissionLifecycleNotification> Start,End,Initialize;
        public bool TryRegisterObserver(string id,Action<MissionLifecycleNotification> start,Action<MissionLifecycleNotification> end,Action<MissionLifecycleNotification> initialization,out NativeCapabilityDiagnostic reason) { Start=start;End=end;Initialize=initialization;reason=new NativeCapabilityDiagnostic();return true; }
    }
    public static class ApiShared { public static ApiBoundary ForMod(string guid)=>new ApiBoundary(); }
    public sealed class ApiBoundary { public bool TryGetMissionLifecycle(out IMissionLifecycleCapability lifecycle,out NativeCapabilityDiagnostic diagnostic) { lifecycle=World.Missions;diagnostic=new NativeCapabilityDiagnostic();return true; } }
    public static class NativeApiLog { public static void Info(BepInEx.Logging.ManualLogSource log,string text) {} public static void Error(BepInEx.Logging.ManualLogSource log,string text) { World.Errors.Add(text); } }
    public static unsafe class UnitAccess { public static bool TryGetById(int id,out SHCDESE.Interop.GameUnit* unit,out int failure) { failure=0;unit=id>0&&id<=World.Units.Length ? World.UnitPointer+id-1:null;return unit!=null; } public static bool IsReallyAlive(SHCDESE.Interop.GameUnit* unit)=>unit!=null&&unit->r_AliveState==SHCDESE.Interop.Enums.AliveState.IsAlive&&unit->r_IsKilledByProjectile==0; }
}
namespace APIShared.Simulation { public static class SimulationTickEvents { public static Action<int> Callback;public static bool TryRegister(string owner,string id,Action<int> callback,out string reason,int order=0) { Callback=callback;reason="";return true; } } }
namespace APIShared.Health { internal static class HealthNativeObserver { internal static Action<int> Callback; internal static void Install(SHCDESE.API.LowLevel.CrusaderLibraryLoadContext context,string hash,Action<int> callback,BepInEx.Logging.ManualLogSource log) { Callback=callback; } } }
internal static unsafe class World
{
    internal static readonly APIShared.MissionBoundary Missions=new APIShared.MissionBoundary();
    internal static readonly List<string> Errors=new List<string>();
    internal static SHCDESE.Interop.GameUnit[] Units=new SHCDESE.Interop.GameUnit[20];
    internal static SHCDESE.Interop.GameBuilding[] Buildings=new SHCDESE.Interop.GameBuilding[20];
    private static GCHandle unitsPin,buildingsPin;
    internal static SHCDESE.Interop.GameUnit* UnitPointer;
    internal static SHCDESE.Interop.GameBuilding* BuildingPointer;
    internal static object Archive=new object();
    internal static int ProxyBasis=1000,Wood=10,Stone=10;
    internal static void Reset()
    {
        Missions.End?.Invoke(new APIShared.MissionLifecycleNotification());
        if (unitsPin.IsAllocated) unitsPin.Free(); if (buildingsPin.IsAllocated) buildingsPin.Free();
        Units=new SHCDESE.Interop.GameUnit[20];Buildings=new SHCDESE.Interop.GameBuilding[20];
        unitsPin=GCHandle.Alloc(Units,GCHandleType.Pinned); buildingsPin=GCHandle.Alloc(Buildings,GCHandleType.Pinned);
        UnitPointer=(SHCDESE.Interop.GameUnit*)unitsPin.AddrOfPinnedObject();BuildingPointer=(SHCDESE.Interop.GameBuilding*)buildingsPin.AddrOfPinnedObject();
        Archive=new object();ProxyBasis=1000;Wood=10;Stone=10;Errors.Clear();
    }
    internal static void Start(bool save=false)=>Missions.Start(new APIShared.MissionLifecycleNotification{Context=new APIShared.MissionContext{IsSave=save,SessionId=1}});
    internal static void Tick(int tick=1)=>APIShared.Simulation.SimulationTickEvents.Callback(tick);
}
