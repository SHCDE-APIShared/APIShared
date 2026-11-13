using System;
using APIShared.Health;
using APIShared.Modifiers;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

internal static class Program
{
    private static int checks;
    private static decimal factor=1;
    private static bool exclude;
    private static int policyCalls;
    private static HealthLegacyBasis[] legacy;
    private static HealthModifierHandle handle;
    private static void Check(bool value,string message) { checks++;if(!value)throw new Exception(message); }
    private static GameUnit Unit(uint maximum,uint current=0,eChimps type=eChimps.CHIMP_TYPE_ARCHER,uint global=1)=>
        new GameUnit{r_AliveState=AliveState.IsAlive,r_GlobalId=global,r_UnitChimp=type,r_ControllableForPlayerId=2,r_MaxHealth=maximum,r_CurrentHealth=current==0?maximum:current};
    private static GameBuilding Building(int maximum,int current=0,eStructs type=eStructs.STRUCT_HOVEL,uint global=101)=>
        new GameBuilding{r_AliveState=AliveState.IsAlive,r_GlobalId=global,r_BuildingType=type,r_PlayerIdOwner=2,r_MaxHealth=(ushort)maximum,r_CurrentHealth=(short)(current==0?maximum:current)};
    private static void Reset() { World.Reset();factor=1;exclude=false;legacy=null;handle.SetEnabled(true); }
    private static byte[] Save()=>ModSaveDataAPI.Instance.Save(new SaveContext());
    private static void Load(byte[] bytes) { if(bytes!=null)ModSaveDataAPI.Instance.Load(bytes,new LoadContext());World.Start(true); }
    private static HealthRecord SavedUnit(byte[] bytes,int id=1)
    {
        foreach(var record in HealthSaveState.Decode(bytes).Records)
            if(record.Kind==HealthObjectKind.Unit && record.Id==id) return record;
        throw new Exception("Missing saved unit " + id);
    }
    private static void SpawnBuilding(int id) => BuildingR3EventHooks.OnBuildingSpawn.Observable.Raise(
        new BuildingSpawnEventArgs{Phase=EventHookPhase.Post,ReturnValue=id});
    private static void Tower(bool withProxy=true)
    {
        World.Units[0]=Unit(240000,120000,eChimps.CHIMP_TYPE_SIEGE_TOWER);
        World.Units[0].r_LinkedProductionBuildingId=1;World.Units[0].r_LinkedProductionBuildingGlobalId=101;
        if(!withProxy) return;
        DeployProxy();
    }
    private static void DeployProxy()
    {
        World.Buildings[0]=Building(1000,500,eStructs.STRUCT_SIEGE_TOWER);
        World.Buildings[0].N00001757=1;World.Buildings[0].N000017CB=1;
    }
    public static void Main()
    {
        World.Reset();HealthRuntime.Initialize(new SHCDESE.API.LowLevel.CrusaderLibraryLoadContext(),"fixture",null);
        Check(HealthModifiers.IsAvailable,"service available");
        Check(HealthModifiers.TryRegister("test","health",c=>{policyCalls++;return exclude?ValueModifierContribution.None:ValueModifierContribution.Factor(factor);},out handle,out _),"register");
        Check(!handle.IsEnabled,"initially disabled");
        Check(!HealthModifiers.TryRegister("test","health",c=>ValueModifierContribution.None,out _,out _),"duplicate rejected");
        Check(HealthModifiers.TryRegisterLegacyBasisProvider("test","legacy",()=>legacy,out _),"migration register");

        Reset();factor=2;World.Units[0]=Unit(100,50);World.Buildings[0]=Building(1000,250);World.Start();World.Tick();
        Check(World.Units[0].r_MaxHealth==200&&World.Units[0].r_CurrentHealth==100,"unit ratio");
        Check(World.Units[0].r_CurrentHealthPercentage==50&&World.Units[0].r_HealthBarBlocks==5,"bar caches");
        Check(World.Buildings[0].r_MaxHealth==2000&&World.Buildings[0].r_CurrentHealth==500,"building ratio");
        factor=5;World.Units[0].r_ControllableForPlayerId=1;for(int i=2;i<100;i++)World.Tick(i);
        Check(World.Units[0].r_MaxHealth==200&&World.Buildings[0].r_MaxHealth==2000,"frozen despite settings and owner change");
        World.Units[0]=Unit(80,80,eChimps.CHIMP_TYPE_ARCHER,1);HealthNativeObserver.Callback(1);World.Tick(100);
        Check(World.Units[0].r_MaxHealth==400,"same type reinitialization gets fresh base");
        World.Units[1]=Unit(30,30,eChimps.CHIMP_TYPE_PEASANT,2);HealthNativeObserver.Callback(2);World.Tick(101);
        Check(World.Units[1].r_MaxHealth==150,"future spawn");
        World.Buildings[1]=Building(400,400,eStructs.STRUCT_TOWER,102);
        BuildingR3EventHooks.OnBuildingSpawn.Observable.Raise(new BuildingSpawnEventArgs{Phase=EventHookPhase.Post,ReturnValue=2});World.Tick(102);
        Check(World.Buildings[1].r_MaxHealth==2000,"future building");

        Reset();factor=5;World.Units[0]=Unit(10_000_000);World.Buildings[0]=Building(10000);World.Start();World.Tick();
        Check(World.Units[0].r_MaxHealth==HealthArithmetic.UnitMaximum,"unit native percent limit");
        Check(World.Buildings[0].r_MaxHealth==short.MaxValue,"building native signed limit");
        Check(HealthArithmetic.BuildingLimit(int.MaxValue,0)==1,"repair overflow limit");
        uint tower=HealthArithmetic.SiegeTowerMaximum(1_200_000,240000,1000,32767,out uint proxy);
        Check((ulong)tower*proxy<=int.MaxValue&&tower<1_200_000&&proxy>1000,"coupled tower limit");
        Check((ulong)(tower+1)*HealthArithmetic.ProxyMaximum(tower+1,240000,1000)>int.MaxValue,"greatest safe tower integer pair");
        Check(HealthArithmetic.ProxyMaximum(HealthArithmetic.UnitMaximum,1,32767)==uint.MaxValue,"oversized prospective proxy saturates without unsigned wrap");
        Check(HealthArithmetic.SiegeTowerMaximum(HealthArithmetic.UnitMaximum,1,32767,32767,out uint tinyProxy)==1&&tinyProxy==32767,
            "tiny unit basis retains monotonic coupled safety search");

        Reset();factor=5;World.Units[0]=Unit(240000,120000,eChimps.CHIMP_TYPE_SIEGE_TOWER);World.Start();World.Tick();
        uint frozen=World.Units[0].r_MaxHealth;factor=2;World.Buildings[0]=Building(1000,500,eStructs.STRUCT_SIEGE_TOWER);
        BuildingR3EventHooks.OnBuildingSpawn.Observable.Raise(new BuildingSpawnEventArgs{Phase=EventHookPhase.Post,ReturnValue=1});World.Tick(2);
        Check(World.Buildings[0].r_MaxHealth==1000,"unlinked proxy waits");
        World.Buildings[0].N00001757=1;World.Buildings[0].N000017CB=1;World.Units[0].r_LinkedProductionBuildingId=1;World.Units[0].r_LinkedProductionBuildingGlobalId=101;World.Tick(3);
        Check(World.Units[0].r_MaxHealth==frozen&&World.Buildings[0].r_MaxHealth==proxy,"late proxy inherits same frozen factor");
        Check(World.Buildings[0].r_CurrentHealth==(short)HealthArithmetic.ScaleCurrent(500,1000,proxy),"late proxy injury preserved");

        Reset();factor=2;World.Units[0]=Unit(300000,300000,eChimps.CHIMP_TYPE_LORD);World.Start();World.Tick();byte[] bytes=Save();
        for(int i=0;i<20;i++)
        { World.Reset();factor=i%2==0?1:2;World.Units[0]=Unit(150000,150000,eChimps.CHIMP_TYPE_LORD);Load(bytes);World.Tick();Check(World.Units[0].r_MaxHealth==(uint)(300000*factor),"load original Lord profile basis");bytes=Save(); }
        World.Reset();factor=3;World.Units[0]=Unit(600000,300000,eChimps.CHIMP_TYPE_LORD);Load(bytes);World.Tick();
        Check(World.Units[0].r_MaxHealth==900000&&World.Units[0].r_CurrentHealth==450000,"wounded load ratio");

        Reset();factor=2;World.Units[0]=Unit(100);World.Start();bytes=Save();Check(World.Units[0].r_MaxHealth==100,"saving before tick never changes HP");
        World.Reset();World.Units[0]=Unit(100);Load(bytes);World.Tick();Check(World.Units[0].r_MaxHealth==200,"save before first tick retains unscaled basis");
        Reset();factor=2;World.Units[0]=Unit(100);World.Start();World.Tick();World.Units[0]=Unit(70,70,eChimps.CHIMP_TYPE_PEASANT);HealthNativeObserver.Callback(1);bytes=Save();
        World.Reset();World.Units[0]=Unit(70,70,eChimps.CHIMP_TYPE_PEASANT);Load(bytes);World.Tick();Check(World.Units[0].r_MaxHealth==140,"pending transition saved with new type basis");

        // A precise event may arrive after load completion but before the first service tick.
        // Saved and fresh identities deliberately share ID, global ID and type; only generation distinguishes them.
        Reset();factor=2;World.Units[0]=Unit(100);World.Start();World.Tick();bytes=Save();
        World.Reset();World.Units[0]=Unit(100);Load(bytes);World.Units[0]=Unit(80);HealthNativeObserver.Callback(1);World.Tick();
        Check(World.Units[0].r_MaxHealth==160,"same-identity reinit before first loaded tick supersedes buffered basis");
        World.Reset();World.Units[0]=Unit(100);Load(bytes);World.Units[0]=Unit(70);HealthNativeObserver.Callback(1);byte[] earlyResave=Save();
        Check(World.Units[0].r_MaxHealth==70,"early reinit resave never applies HP");
        Check(HealthSaveState.Decode(earlyResave).Records[0].Basis==70,"early reinit resave preserves fresh generation basis");
        World.Reset();World.Units[0]=Unit(70);Load(earlyResave);World.Tick();
        Check(World.Units[0].r_MaxHealth==140,"early reinit resave reloads fresh basis");

        Reset();factor=5;World.Units[0]=Unit(240000,120000,eChimps.CHIMP_TYPE_SIEGE_TOWER);World.Start();bytes=Save();
        Check(World.Units[0].r_MaxHealth==240000,"siege save before tick does not initialize HP");
        World.Reset();World.ProxyBasis=2000;World.Units[0]=Unit(240000,120000,eChimps.CHIMP_TYPE_SIEGE_TOWER);Load(bytes);World.Tick();
        frozen=World.Units[0].r_MaxHealth;factor=2;World.Buildings[0]=Building(1000,500,eStructs.STRUCT_SIEGE_TOWER);
        World.Buildings[0].N00001757=1;World.Buildings[0].N000017CB=1;World.Units[0].r_LinkedProductionBuildingId=1;World.Units[0].r_LinkedProductionBuildingGlobalId=101;
        BuildingR3EventHooks.OnBuildingSpawn.Observable.Raise(new BuildingSpawnEventArgs{Phase=EventHookPhase.Post,ReturnValue=1});World.Tick(2);
        Check(World.Units[0].r_MaxHealth==frozen&&World.Buildings[0].r_MaxHealth==proxy,"early siege save retains basis and late proxy uses frozen target");
        bytes=Save();World.Reset();World.Units[0]=Unit(240000,120000,eChimps.CHIMP_TYPE_SIEGE_TOWER);World.Buildings[0]=Building((int)proxy,(int)proxy/2,eStructs.STRUCT_SIEGE_TOWER);
        World.Buildings[0].N00001757=1;World.Buildings[0].N000017CB=1;World.Units[0].r_LinkedProductionBuildingId=1;World.Units[0].r_LinkedProductionBuildingGlobalId=101;Load(bytes);World.Tick();
        Check(World.Units[0].r_MaxHealth==480000&&World.Buildings[0].r_MaxHealth==2000,"siege reload applies new factor once to both linked records");

        Reset();factor=2;World.Units[0]=Unit(150000,150000,eChimps.CHIMP_TYPE_LORD);World.Units[1]=Unit(100,100,eChimps.CHIMP_TYPE_ARCHER,2);
        legacy=new[]{new HealthLegacyBasis(1,1,300000)};Load(null);World.Tick();
        Check(World.Units[0].r_MaxHealth==600000&&World.Units[1].r_MaxHealth==100,"legacy Lords only");bytes=Save();
        World.Reset();World.Units[0]=Unit(150000,150000,eChimps.CHIMP_TYPE_LORD);World.Units[1]=Unit(100,100,eChimps.CHIMP_TYPE_ARCHER,2);Load(bytes);World.Tick();
        Check(World.Units[0].r_MaxHealth==600000&&World.Units[1].r_MaxHealth==100,"legacy category exclusion survives resave");
        Reset();factor=2;World.Units[0]=Unit(100,100,eChimps.CHIMP_TYPE_LORD);legacy=Array.Empty<HealthLegacyBasis>();Load(null);World.Tick();
        Check(World.Units[0].r_MaxHealth==100,"valid empty legacy archive cannot invent an initial Lord basis");bytes=Save();
        Check(HealthSaveState.Decode(bytes).Records.Count==0,"resave cannot persist an invented legacy Lord basis");
        World.Reset();World.Units[0]=Unit(100,100,eChimps.CHIMP_TYPE_LORD);Load(bytes);World.Tick();
        Check(World.Units[0].r_MaxHealth==100,"missing legacy Lord basis remains excluded after resave");
        World.Units[0]=Unit(80,80,eChimps.CHIMP_TYPE_LORD);HealthNativeObserver.Callback(1);World.Tick(2);
        Check(World.Units[0].r_MaxHealth==160,"observed future Lord initialization supplies a fresh legacy-mission basis");
        Reset();factor=2;World.Units[0]=Unit(100,100,eChimps.CHIMP_TYPE_LORD);World.Units[1]=Unit(100,100,eChimps.CHIMP_TYPE_LORD,2);
        legacy=new[]{new HealthLegacyBasis(1,1,150),new HealthLegacyBasis(2,999,150)};Load(null);World.Tick();
        Check(World.Units[0].r_MaxHealth==300&&World.Units[1].r_MaxHealth==100,"one valid legacy identity never authorizes a missing or invalid Lord basis");
        Reset();World.Units[0]=Unit(100);Load(null);World.Tick();Check(World.Units[0].r_MaxHealth==100,"missing legacy archive inactive");
        Reset();World.Units[0]=Unit(100);Load(new byte[16]);World.Tick();Check(World.Units[0].r_MaxHealth==100&&World.Errors.Count>0,"corrupt archive fail closed");

        Reset();factor=2;exclude=true;World.Units[0]=Unit(100,50);World.Buildings[0]=Building(1000,500);World.Start();World.Tick();
        Check(World.Units[0].r_MaxHealth==100&&World.Units[0].r_CurrentHealth==50&&World.Buildings[0].r_MaxHealth==1000,"None does not touch excluded subjects");
        Reset();factor=2;World.Units[0]=Unit(100);World.Units[0].r_CurrentHealth=0;World.Buildings[0]=Building(100);World.Buildings[0].r_CurrentHealth=0;World.Start();World.Tick();
        Check(World.Units[0].r_CurrentHealth==0&&World.Buildings[0].r_CurrentHealth==0,"no resurrection");

        var state=new HealthSaveState{FullBasis=true};state.Records.Add(new HealthRecord{Kind=HealthObjectKind.Unit,Id=1,GlobalId=5,Type=2,Generation=1,Basis=100,AppliedMaximum=200});
        bytes=state.Encode();Check(HealthSaveState.Decode(bytes).Records[0].Basis==100,"archive roundtrip");
        var trailing=new byte[bytes.Length+1];Array.Copy(bytes,trailing,bytes.Length);bool rejected=false;try{HealthSaveState.Decode(trailing);}catch(InvalidOperationException){rejected=true;}Check(rejected,"archive trailing bytes rejected");
        state.Records.Add(state.Records[0]);rejected=false;try{state.Encode();}catch(InvalidOperationException){rejected=true;}Check(rejected,"duplicate archive IDs rejected");
        DormantHistory();
        DormantAndExcludedTowers();
        Console.WriteLine($"Health production-source fixture: {checks} checks passed.");
    }

    private static void DormantHistory()
    {
        Reset();factor=2;World.Units[0]=Unit(100);World.Start();World.Tick();byte[] original=Save();
        World.Reset();handle.SetEnabled(false);World.Units[0]=Unit(100);Load(original);
        Check(HealthRuntime.IsObserving,"loaded history keeps the native observer demanded while policies are disabled");
        int calls=policyCalls;
        World.Units[0]=Unit(80,40);HealthNativeObserver.Callback(1);byte[] dormant=Save();
        Check(World.Units[0].r_MaxHealth==80&&World.Units[0].r_CurrentHealth==40&&policyCalls==calls,"early dormant save never applies or evaluates policies");
        Check(SavedUnit(dormant).Basis==80&&SavedUnit(dormant).AppliedMaximum==80,"early dormant save retains the fresh same-identity generation");
        handle.SetEnabled(true);World.Tick();
        Check(World.Units[0].r_MaxHealth==80,"enabling does not retroactively apply a completed dormant generation");
        World.Units[0]=Unit(70);HealthNativeObserver.Callback(1);World.Tick(2);
        Check(World.Units[0].r_MaxHealth==140,"a subsequent active initialization still applies policies");
        World.Reset();World.Units[0]=Unit(80,40);Load(dormant);World.Tick();
        Check(World.Units[0].r_MaxHealth==160&&World.Units[0].r_CurrentHealth==80,"dormant resave reloads the fresh basis without compounding");

        // A load normalizer is not a positively observed new initialization.
        World.Reset();handle.SetEnabled(false);World.Units[0]=Unit(80,40);Load(original);World.Tick();dormant=Save();
        Check(SavedUnit(dormant).Basis==100&&SavedUnit(dormant).AppliedMaximum==80,"dormant normalization preserves the verified original basis");
        World.Buildings[0]=Building(350,175);SpawnBuilding(1);World.Tick(2);dormant=Save();
        Check(World.Buildings[0].r_MaxHealth==350&&World.Buildings[0].r_CurrentHealth==175,"dormant spawned building HP stays unchanged");
        World.Reset();handle.SetEnabled(true);World.Units[0]=Unit(80,40);World.Buildings[0]=Building(350,175);Load(dormant);World.Tick();
        Check(World.Units[0].r_MaxHealth==200&&World.Buildings[0].r_MaxHealth==700,"dormant building and retained unit bases both survive resaving");

        World.Reset();handle.SetEnabled(false);World.Units[0]=Unit(100);Load(original);World.Tick();
        World.Units[0]=Unit(60,30,eChimps.CHIMP_TYPE_PEASANT,2);HealthNativeObserver.Callback(1);World.Tick(2);dormant=Save();
        Check(SavedUnit(dormant).GlobalId==2&&SavedUnit(dormant).Basis==60,"dormant slot reuse replaces old global/type provenance");
        World.Units[0].r_CurrentHealth=0;World.Tick(3);dormant=Save();
        Check(World.Units[0].r_CurrentHealth==0&&HealthSaveState.Decode(dormant).Records.Count==0,"dormant dead units are neither revived nor archived as live");

        Reset();handle.SetEnabled(false);World.Units[0]=Unit(100);World.Start();World.Tick();
        Check(!HealthRuntime.IsObserving&&Save()==null,"new mission without demand or history remains dormant after a previous tracked mission");
        World.Reset();World.Units[0]=Unit(100);Load(null);World.Tick();
        Check(!HealthRuntime.IsObserving&&Save()==null,"legacy save without demand does not gain new eligibility");
        handle.SetEnabled(true);factor=2;World.Tick(2);
        Check(World.Units[0].r_MaxHealth==100&&!HealthSaveState.Decode(Save()).FullBasis,"later activation cannot invent a missing legacy basis");

        Reset();factor=2;legacy=Array.Empty<HealthLegacyBasis>();World.Units[0]=Unit(100,100,eChimps.CHIMP_TYPE_LORD);Load(null);World.Tick();byte[] legacySave=Save();
        World.Reset();handle.SetEnabled(false);World.Units[0]=Unit(100,100,eChimps.CHIMP_TYPE_LORD);Load(legacySave);World.Tick();
        Check(HealthSaveState.Decode(Save()).Records.Count==0,"dormant legacy history does not invent an existing Lord basis");
        World.Units[0]=Unit(80,80,eChimps.CHIMP_TYPE_LORD);HealthNativeObserver.Callback(1);
        World.Units[1]=Unit(100,100,eChimps.CHIMP_TYPE_ARCHER,2);HealthNativeObserver.Callback(2);World.Tick(2);dormant=Save();
        var legacyState=HealthSaveState.Decode(dormant);
        Check(!legacyState.FullBasis&&legacyState.LegacyLords&&legacyState.Records.Count==1&&SavedUnit(dormant).Basis==80,"dormant legacy history captures only a positively observed new Lord generation");
        World.Reset();handle.SetEnabled(true);World.Units[0]=Unit(80,80,eChimps.CHIMP_TYPE_LORD);World.Units[1]=Unit(100,100,eChimps.CHIMP_TYPE_ARCHER,2);Load(dormant);World.Tick();
        Check(World.Units[0].r_MaxHealth==160&&World.Units[1].r_MaxHealth==100,"legacy restrictions survive a dormant resave");

        Reset();handle.SetEnabled(false);World.Units[0]=Unit(100);Load(new byte[16]);World.Tick();
        Check(World.Units[0].r_MaxHealth==100&&Save()==null&&World.Errors.Count>0,"malformed history stays fail closed with no active policy");
        handle.SetEnabled(true);World.Tick(2);
        Check(World.Units[0].r_MaxHealth==100&&Save()==null,"activation cannot reinterpret malformed history as fresh bases");
    }

    private static void DormantAndExcludedTowers()
    {
        Reset();factor=2;Tower(false);World.Start();World.Tick();byte[] original=Save();
        Check(SavedUnit(original).ProxyBasis==1000&&SavedUnit(original).ProxyMaximum==2000,"tower history contains a frozen prospective proxy target");
        Reset();exclude=true;Tower();Load(original);World.Tick();
        Check(World.Units[0].r_MaxHealth==240000&&World.Buildings[0].r_MaxHealth==1000,"None does not reapply a saved target to an existing proxy");
        Check(SavedUnit(Save()).ProxyBasis==1000&&SavedUnit(Save()).ProxyMaximum==0,"None preserves the proxy basis but retires the old applied target");
        Reset();exclude=true;Tower(false);Load(original);World.Tick();DeployProxy();SpawnBuilding(1);World.Tick(2);
        Check(World.Buildings[0].r_MaxHealth==1000,"None does not reapply a saved target to a later deployed proxy");

        Reset();handle.SetEnabled(false);Tower();World.ProxyBasis=2000;Load(original);int calls=policyCalls;byte[] dormant=Save();
        Check(World.Units[0].r_MaxHealth==240000&&World.Units[0].r_CurrentHealth==120000&&World.Buildings[0].r_MaxHealth==1000&&World.Buildings[0].r_CurrentHealth==500&&policyCalls==calls,"early dormant tower save leaves both linked objects untouched");
        Check(SavedUnit(dormant).Basis==240000&&SavedUnit(dormant).ProxyBasis==1000&&SavedUnit(dormant).ProxyMaximum==0,"dormant tower keeps both original bases despite a changed type table");
        handle.SetEnabled(true);World.Tick();
        Check(World.Units[0].r_MaxHealth==240000&&World.Buildings[0].r_MaxHealth==1000,"enabling after a dormant tower save does not retroactively apply HP");
        Reset();factor=2;Tower();Load(dormant);World.Tick();
        Check(World.Units[0].r_MaxHealth==480000&&World.Buildings[0].r_MaxHealth==2000,"active reload computes a fresh paired target from dormant original bases");

        Reset();factor=2;handle.SetEnabled(false);Tower();World.Units[0].r_AliveState=AliveState.NeedsInit;Load(original);World.Tick();Save();
        Check(World.Buildings[0].r_MaxHealth==1000,"dormant proxy waits for its unfinished unit generation");
        handle.SetEnabled(true);World.Units[0].r_AliveState=AliveState.IsAlive;World.Tick(2);
        Check(World.Units[0].r_MaxHealth==480000&&World.Buildings[0].r_MaxHealth==2000,"activation before native initialization finishes still applies to both linked objects");

        Reset();factor=2;Tower(false);World.Start();World.Tick();handle.SetEnabled(false);DeployProxy();SpawnBuilding(1);World.Tick(2);dormant=Save();
        Check(World.Buildings[0].r_MaxHealth==1000&&SavedUnit(dormant).ProxyMaximum==2000,"dormant late proxy stays untouched without discarding its previously active unit target");
        handle.SetEnabled(true);World.Tick(3);
        Check(World.Buildings[0].r_MaxHealth==1000,"a completed dormant late proxy is not applied retroactively");
    }
}
