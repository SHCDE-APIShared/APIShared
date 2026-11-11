using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Health
{
    // Process root. No MonoBehaviour, timer, per-frame observer, owner watcher or repeating world scan.
    internal static unsafe class HealthRuntime
    {
        internal const string ArchiveId = "APIShared.Health.v1";
        private static readonly object Sync = new object();
        private static readonly Dictionary<long,HealthRecord> Records = new Dictionary<long,HealthRecord>();
        private static readonly Dictionary<int,Pending> Units = new Dictionary<int,Pending>();
        private static readonly Dictionary<int,Pending> Buildings = new Dictionary<int,Pending>();
        private static readonly HashSet<long> Applied = new HashSet<long>();
        private static readonly List<int> Completed = new List<int>();
        private static IMissionLifecycleCapability missions;
        private static IDisposable buildingCreated;
        private static ManualLogSource logger;
        private static MissionContext mission;
        private static byte[] archiveBytes;
        private static object archiveSource;
        private static bool prepared, scanned, fullBasis, legacyLords, malformed;
        private static uint generation;
        private static int available;
        private static string unavailable = "APIShared health initialization has not completed.";
        private static int reportedFailures;
        internal static bool IsAvailable => Volatile.Read(ref available) != 0;
        internal static string UnavailableReason => IsAvailable ? string.Empty : Volatile.Read(ref unavailable);
        internal static bool IsObserving => IsAvailable && mission != null && HealthModifiers.HasDemand;

        internal static void Initialize(CrusaderLibraryLoadContext context,string hash,ManualLogSource log)
        {
            if (IsAvailable) return;
            logger=log;
            try
            {
                if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out missions,out var diagnostic))
                    throw new InvalidOperationException("HP service needs mission lifecycle: " + diagnostic);
                HealthNativeObserver.Install(context,hash,OnTypeInitialized,log);
                if (!ModSaveDataAPI.Instance.RegisterModDataHandler(ArchiveId,Save,BufferArchive,ClearArchive))
                    throw new InvalidOperationException("HP archive identity is already registered.");
                buildingCreated=BuildingR3EventHooks.OnBuildingSpawn.Observable.Subscribe(OnBuildingCreated);
                if (!missions.TryRegisterObserver("health/lifecycle",OnStart,OnEnd,OnInitialization,out diagnostic))
                    throw new InvalidOperationException("HP lifecycle unavailable: " + diagnostic);
                if (!Simulation.SimulationTickEvents.TryRegister(APISharedPlugin.PluginGuid,"health/initialize",Tick,out string reason,500))
                    throw new InvalidOperationException(reason);
                Volatile.Write(ref available,1);
                NativeApiLog.Info(log,"Shared HP service ready; initialization-only, native save-preserved simulation ticks; no ongoing HP scans.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref unavailable,error.Message); Report("startup",error);
                // Published native code and any already registered observers remain rooted and dormant.
            }
        }

        private static void OnStart(MissionLifecycleNotification notification)
        {
            if (notification.IsReplay) return;
            lock (Sync) { Reset(); mission=notification.Context; }
        }
        private static void OnEnd(MissionLifecycleNotification notification) { lock (Sync) Reset(); }
        private static void OnInitialization(MissionLifecycleNotification notification)
        {
            if (notification.Phase == MissionInitializationPhase.BeforeLoad) lock (Sync) Reset();
        }
        private static void Reset()
        {
            mission=null; prepared=false; scanned=false; fullBasis=false; legacyLords=false; malformed=false;
            generation=0; Records.Clear(); Units.Clear(); Buildings.Clear(); Applied.Clear(); Completed.Clear();
        }
        private static void BufferArchive(byte[] bytes,LoadContext context)
        { lock (Sync) { archiveBytes=bytes; archiveSource=GameMapArchiveManagerAPI.Instance.GetMapArchive(); } }
        private static void ClearArchive() { lock (Sync) { archiveBytes=null; archiveSource=null; } }

        private static void Prepare()
        {
            if (prepared || mission == null) return;
            prepared=true; fullBasis=!mission.IsSave;
            if (!mission.IsSave) { archiveBytes=null; archiveSource=null; return; }
            byte[] bytes=ReferenceEquals(archiveSource,GameMapArchiveManagerAPI.Instance.GetMapArchive()) ? archiveBytes : null;
            archiveBytes=null; archiveSource=null;
            if (bytes != null)
            {
                try
                {
                    HealthSaveState state=HealthSaveState.Decode(bytes);
                    fullBasis=state.FullBasis; legacyLords=state.LegacyLords;
                    foreach (var record in state.Records)
                    { Records.Add(record.Key,record); generation=Math.Max(generation,record.Generation); }
                }
                catch (Exception error)
                { malformed=true; fullBasis=false; legacyLords=false; Records.Clear(); Report("archive",error); }
                return;
            }
            // Only explicit original maxima are accepted from older consumer archives.
            foreach (var provider in HealthModifiers.LegacyProviders)
            {
                HealthLegacyBasis[] values;
                try { values=provider.Read(); }
                catch (Exception error) { Report("migration " + provider.Owner + "/" + provider.Id,error); continue; }
                if (values == null) continue;
                legacyLords=true;
                foreach (var value in values)
                {
                    if (value.Maximum == 0 || !UnitAccess.TryGetById(value.UnitId,out GameUnit* unit,out _) ||
                        unit->r_GlobalId != value.GlobalId || value.GlobalId == 0 || unit->r_UnitChimp != eChimps.CHIMP_TYPE_LORD) continue;
                    var record=NewRecord(HealthObjectKind.Unit,value.UnitId,value.GlobalId,(int)unit->r_UnitChimp,value.Maximum);
                    Records[record.Key]=record;
                }
            }
            if (!legacyLords) NativeApiLog.Info(logger,"Shared HP inactive for this legacy save: no original HP archive; new categories require a new game.");
        }

        private static void ScanOnce()
        {
            if (scanned) return; scanned=true;
            var units=GameUnitManagerAPI.Instance.GetUnitsAsSpan();
            for (int spanIndex=0;spanIndex<units.Length;spanIndex++)
            {
                ref GameUnit unit=ref units[spanIndex];
                if ((unit.r_AliveState == AliveState.IsAlive || unit.r_AliveState == AliveState.NeedsInit) && unit.r_GlobalId != 0)
                    Queue(Units,spanIndex+1,unit.r_GlobalId,(int)unit.r_UnitChimp,HealthInitializationReason.MissionStart,0);
            }
            var buildings=GameBuildingManagerAPI.Instance.GetBuildingsAsSpan();
            for (int spanIndex=0;spanIndex<buildings.Length;spanIndex++)
            {
                ref GameBuilding building=ref buildings[spanIndex];
                if ((building.r_AliveState == AliveState.IsAlive || building.r_AliveState == AliveState.NeedsInit) && building.r_GlobalId != 0)
                    Queue(Buildings,spanIndex+1,building.r_GlobalId,(int)building.r_BuildingType,HealthInitializationReason.MissionStart,0);
            }
        }
        private static void OnTypeInitialized(int id)
        {
            lock (Sync)
            {
                if (mission == null || !HealthModifiers.HasDemand || !UnitAccess.TryGetById(id,out GameUnit* unit,out _) || unit->r_GlobalId == 0) return;
                // Import saved generations before assigning a fresh one. Otherwise an early
                // same-type initialization could collide with a still-buffered saved record.
                Prepare(); if (malformed) return;
                long key=HealthRecord.KeyFor(HealthObjectKind.Unit,id);
                Records.Remove(key); Applied.Remove(key);
                Queue(Units,id,unit->r_GlobalId,(int)unit->r_UnitChimp,HealthInitializationReason.TypeInitialized,NextGeneration());
            }
        }
        private static void OnBuildingCreated(BuildingSpawnEventArgs args)
        {
            if (args.Phase != EventHookPhase.Post || args.ReturnValue <= 0 || args.ReturnValue > int.MaxValue || !IsObserving) return;
            lock (Sync)
            {
                int id=(int)args.ReturnValue;
                if (!TryBuilding(id,out GameBuilding* building) || building->r_GlobalId == 0) return;
                Prepare(); if (malformed) return;
                long key=HealthRecord.KeyFor(HealthObjectKind.Building,id); Records.Remove(key); Applied.Remove(key);
                Queue(Buildings,id,building->r_GlobalId,(int)building->r_BuildingType,HealthInitializationReason.Created,NextGeneration());
            }
        }
        private static void Queue(Dictionary<int,Pending> queue,int id,uint global,int type,HealthInitializationReason reason,uint serial)
        {
            // A more precise native-initialization trigger must survive the once-only initial scan.
            if (serial == 0 && queue.ContainsKey(id)) return;
            queue[id]=new Pending { Id=id,Global=global,Type=type,Reason=reason,Generation=serial };
        }
        private static uint NextGeneration()
        {
            if (generation == uint.MaxValue) throw new InvalidOperationException("HP initialization generation exhausted.");
            return ++generation;
        }
        private static HealthRecord NewRecord(HealthObjectKind kind,int id,uint global,int type,uint basis) =>
            new HealthRecord { Kind=kind,Id=id,GlobalId=global,Type=type,Basis=basis,AppliedMaximum=basis,Generation=NextGeneration() };

        private static void Tick(int tick)
        {
            if (!IsAvailable || mission == null || !HealthModifiers.HasDemand) return;
            lock (Sync)
            {
                if (mission == null) return;
                Prepare(); if (malformed) return; ScanOnce();
                Process(Units,ApplyUnit); Process(Buildings,ApplyBuilding);
            }
        }
        private static void Process(Dictionary<int,Pending> queue,Func<Pending,bool> apply)
        {
            Completed.Clear();
            foreach (var item in queue)
            {
                try { if (apply(item.Value)) Completed.Add(item.Key); }
                catch (Exception error) { Completed.Add(item.Key); Report("object initialization",error); }
            }
            foreach (int id in Completed) queue.Remove(id);
        }

        private static HealthRecord Capture(HealthObjectKind kind,Pending pending,uint actualMaximum)
        {
            long key=HealthRecord.KeyFor(kind,pending.Id);
            if (Records.TryGetValue(key,out var record) && record.GlobalId == pending.Global && record.Type == pending.Type &&
                (pending.Generation == 0 || record.Generation == pending.Generation))
                return record;
            // A valid legacy archive does not prove a basis for every existing Lord.
            // Only a positively observed new initialization may supply a fresh missing basis.
            if (!fullBasis && !(legacyLords && pending.Generation != 0 && kind == HealthObjectKind.Unit &&
                (eChimps)pending.Type == eChimps.CHIMP_TYPE_LORD)) return null;
            if (actualMaximum == 0) return null;
            record=NewRecord(kind,pending.Id,pending.Global,pending.Type,actualMaximum);
            if (pending.Generation != 0) record.Generation=pending.Generation;
            if (kind == HealthObjectKind.Unit && (eChimps)pending.Type == eChimps.CHIMP_TYPE_SIEGE_TOWER)
            {
                // Capture even when saving before the first tick: a later type-table change
                // must not replace the second original basis after loading this generation.
                int proxy=GameBuildingManagerAPI.Instance.GetDefaultHealth(eStructs.STRUCT_SIEGE_TOWER);
                if (proxy <= 0 || proxy > short.MaxValue) throw new InvalidOperationException("Unsafe prospective siege-tower building basis.");
                record.ProxyBasis=(uint)proxy;
            }
            Records[key]=record; return record;
        }
        private static HealthModifierContext Context(HealthRecord record,int player,HealthInitializationReason reason) =>
            new HealthModifierContext(record.Kind,record.Id,record.GlobalId,player,
                player >= 1 && player <= GamePlayerManagerAPI.MAX_PLAYERS && GamePlayerManagerAPI.Instance.IsAIPlayer(player),
                record.Kind == HealthObjectKind.Unit ? (eChimps)record.Type : default(eChimps),
                record.Kind == HealthObjectKind.Building ? (eStructs)record.Type : default(eStructs),record.Basis,
                mission.IsSave && reason == HealthInitializationReason.MissionStart ? HealthInitializationReason.SaveLoaded : reason);
        private static bool ApplyUnit(Pending pending)
        {
            long key=HealthRecord.KeyFor(HealthObjectKind.Unit,pending.Id);
            if (!UnitAccess.TryGetById(pending.Id,out GameUnit* unit,out _) || unit->r_GlobalId != pending.Global || (int)unit->r_UnitChimp != pending.Type) return true;
            if (unit->r_AliveState == AliveState.NeedsInit) return false;
            if (!UnitAccess.IsReallyAlive(unit) || unit->r_CurrentHealth == 0 || unit->r_MaxHealth == 0 || Applied.Contains(key)) return true;
            HealthRecord record=Capture(HealthObjectKind.Unit,pending,unit->r_MaxHealth); if (record == null) return true;
            uint maximum=HealthModifiers.Evaluate(Context(record,unit->r_ControllableForPlayerId,pending.Reason),HealthArithmetic.UnitMaximum,out bool contributed);
            if (!contributed)
            {
                // A neutral/excluded subject is captured for save-load compatibility, never rewritten.
                record.AppliedMaximum=unit->r_MaxHealth; Applied.Add(key); return true;
            }
            if (unit->r_UnitChimp == eChimps.CHIMP_TYPE_SIEGE_TOWER)
            {
                if (record.ProxyBasis == 0)
                {
                    int basis=GameBuildingManagerAPI.Instance.GetDefaultHealth(eStructs.STRUCT_SIEGE_TOWER);
                    if (basis <= 0 || basis > short.MaxValue) throw new InvalidOperationException("Unsafe prospective siege-tower building basis.");
                    record.ProxyBasis=(uint)basis;
                }
                maximum=HealthArithmetic.SiegeTowerMaximum(maximum,record.Basis,record.ProxyBasis,
                    BuildingLimit(eStructs.STRUCT_SIEGE_TOWER),out uint proxy);
                record.ProxyMaximum=proxy;
            }
            uint current=HealthArithmetic.ScaleCurrent(unit->r_CurrentHealth,unit->r_MaxHealth,maximum);
            ushort percent=HealthArithmetic.Percentage(current,maximum);
            unit->r_MaxHealth=maximum; unit->r_CurrentHealth=current;
            unit->r_CurrentHealthPercentage=percent; unit->r_HealthBarBlocks=(uint)(percent/10);
            record.AppliedMaximum=maximum; Applied.Add(key); return true;
        }
        private static bool ApplyBuilding(Pending pending)
        {
            long key=HealthRecord.KeyFor(HealthObjectKind.Building,pending.Id);
            if (!TryBuilding(pending.Id,out GameBuilding* building) || building->r_GlobalId != pending.Global || (int)building->r_BuildingType != pending.Type) return true;
            if (building->r_AliveState == AliveState.NeedsInit) return false;
            if (building->r_AliveState != AliveState.IsAlive || building->r_CurrentHealth <= 0 || building->r_MaxHealth == 0 || Applied.Contains(key)) return true;
            if (building->r_BuildingType == eStructs.STRUCT_SIEGE_TOWER)
            {
                // Audited reciprocal links: building +0x110/+0x114, unit +0x334/+0x364.
                int unitId=building->N00001757;
                if (unitId == 0) return false;
                if (!UnitAccess.TryGetById(unitId,out GameUnit* tower,out _) || tower->r_GlobalId != building->N000017CB ||
                    tower->r_UnitChimp != eChimps.CHIMP_TYPE_SIEGE_TOWER) return true;
                if (tower->r_LinkedProductionBuildingId != pending.Id || tower->r_LinkedProductionBuildingGlobalId != building->r_GlobalId) return false;
                long unitKey=HealthRecord.KeyFor(HealthObjectKind.Unit,unitId);
                if (!Records.TryGetValue(unitKey,out var paired) || paired.GlobalId != tower->r_GlobalId || !Applied.Contains(unitKey))
                    return Units.ContainsKey(unitId) ? false : true;
                if (paired.ProxyMaximum == 0) return true;
                uint value=HealthArithmetic.ScaleCurrent((uint)building->r_CurrentHealth,building->r_MaxHealth,paired.ProxyMaximum);
                building->r_MaxHealth=(ushort)paired.ProxyMaximum; building->r_CurrentHealth=(short)value; Applied.Add(key); return true;
            }
            HealthRecord record=Capture(HealthObjectKind.Building,pending,building->r_MaxHealth); if (record == null) return true;
            uint maximum=HealthModifiers.Evaluate(Context(record,building->r_PlayerIdOwner,pending.Reason),BuildingLimit(building->r_BuildingType),out bool contributed);
            if (!contributed) { record.AppliedMaximum=building->r_MaxHealth; Applied.Add(key); return true; }
            uint current=HealthArithmetic.ScaleCurrent((uint)building->r_CurrentHealth,building->r_MaxHealth,maximum);
            building->r_MaxHealth=(ushort)maximum; building->r_CurrentHealth=(short)current;
            record.AppliedMaximum=maximum; Applied.Add(key); return true;
        }
        private static uint BuildingLimit(eStructs type) => HealthArithmetic.BuildingLimit(
            GameBuildingManagerAPI.Instance.GetWoodCost(type),GameBuildingManagerAPI.Instance.GetStoneCost(type));
        private static bool TryBuilding(int id,out GameBuilding* building)
        {
            building=null; var manager=GameBuildingManagerAPI.Instance;
            return id > 0 && id <= manager.GetBuildingsAsSpan().Length && manager.TryGetBuildingById(id,out building) && building != null;
        }

        private static byte[] Save(SaveContext context)
        {
            if (!IsAvailable || !context.IsSaveFile || context.IsMapEditorSave) return null;
            lock (Sync)
            {
                if (mission == null || !HealthModifiers.HasDemand) return null;
                Prepare(); if (malformed) return null; ScanOnce();
                // Saving captures pending completed bases but NEVER mutates native HP.
                foreach (var item in Units)
                    if (UnitAccess.TryGetById(item.Key,out GameUnit* unit,out _) && unit->r_GlobalId == item.Value.Global &&
                        (int)unit->r_UnitChimp == item.Value.Type && unit->r_CurrentHealth > 0 &&
                        (UnitAccess.IsReallyAlive(unit) || unit->r_AliveState == AliveState.NeedsInit))
                        Capture(HealthObjectKind.Unit,item.Value,unit->r_MaxHealth);
                foreach (var item in Buildings)
                    if (TryBuilding(item.Key,out GameBuilding* building) && building->r_GlobalId == item.Value.Global &&
                        (int)building->r_BuildingType == item.Value.Type && building->r_CurrentHealth > 0 && building->r_BuildingType != eStructs.STRUCT_SIEGE_TOWER)
                        Capture(HealthObjectKind.Building,item.Value,building->r_MaxHealth);
                var result=new HealthSaveState { FullBasis=fullBasis,LegacyLords=legacyLords };
                foreach (var record in Records.Values)
                {
                    bool valid=record.Kind == HealthObjectKind.Unit ?
                        UnitAccess.TryGetById(record.Id,out GameUnit* unit,out _) && unit->r_GlobalId == record.GlobalId && (int)unit->r_UnitChimp == record.Type && unit->r_CurrentHealth > 0 :
                        TryBuilding(record.Id,out GameBuilding* building) && building->r_GlobalId == record.GlobalId && (int)building->r_BuildingType == record.Type && building->r_CurrentHealth > 0;
                    if (valid) result.Records.Add(record);
                }
                result.Records.Sort((a,b) => a.Key.CompareTo(b.Key)); return result.Encode();
            }
        }
        internal static void Report(string where,Exception error)
        {
            // Failure-only logarithmic throttle, shared across rare service errors.
            int count=Interlocked.Increment(ref reportedFailures);
            if ((count & (count-1)) == 0) NativeApiLog.Error(logger,"Shared HP " + where + ": " + error);
        }
        private struct Pending
        {
            internal int Id,Type;
            internal uint Global,Generation;
            internal HealthInitializationReason Reason;
        }
    }
}
