using System;
using System.Collections.Generic;
using System.Reflection;
using APIShared;
using APIShared.Savegames;
using APIShared.ScriptExtenderFixes;
using Steamworks;

internal static class Program
{
    private static int checks;
    private const BindingFlags Fields=BindingFlags.Static|BindingFlags.NonPublic;
    private static void Check(bool value,string reason) { checks++; if(!value) throw new Exception(reason); }
    private static void Set(string name,object value)=>typeof(LobbyStartPacketRuntime).GetField(name,Fields).SetValue(null,value);
    private static Platform_Multiplayer Mp=>Platform_Multiplayer.Instance;
    private static LobbyStartPacketQueue Queue=>(LobbyStartPacketQueue)typeof(LobbyStartPacketRuntime).GetField("queue",Fields).GetValue(null);
    private static void Proposal(string token,string name="test.msv")=>MultiplayerSavePreparation.Current=new MultiplayerSavePreparationSnapshot { OperationId=token,SaveName=name };
    private static void Reset()
    {
        Set("scope",null); Queue.Reset(); Set("ready",true); Set("replaying",false);
        Set("mapLoaded",typeof(Platform_Multiplayer).GetField("mapLoaded"));
        Set("process",typeof(Platform_Multiplayer).GetMethod("processMessage",BindingFlags.Instance|BindingFlags.NonPublic));
        Boundary.Local=2002; Boundary.Host=2001; Boundary.Main=true; Boundary.DuringProcess=null;
        Boundary.Processed.Clear(); Boundary.Logs.Clear(); Platform_Multiplayer.MPGameActive=false;
        Platform_Multiplayer.Instance=new Platform_Multiplayer { activeLobby=new Platform_Multiplayer.MPLobby(),gameMembers=Members() };
        MissionLifecycleService.ActiveContext=new MissionContext { SessionId=1 };
        MapRecordLoadCoordinator.Token=null; Proposal("A");
    }
    private static List<Platform_Multiplayer.MPGameMember> Members()=>new List<Platform_Multiplayer.MPGameMember> {
        new Platform_Multiplayer.MPGameMember { steamID=2001 },new Platform_Multiplayer.MPGameMember {steamID=2002,isSelf=true} };
    private static void Stage(short type=2,byte[] bytes=null,ulong sender=2001)
    {
        bytes=bytes??BitConverter.GetBytes(123);
        LobbyStartPacketRuntime.Stage(Mp,new Platform_Multiplayer.MPData {packetType=type,data=bytes,dataLength=bytes.Length},new CSteamID(sender));
    }
    private static LobbyStartPacketRuntime.StartReceipt Begin(long session=2)
    {
        MissionLifecycleService.ActiveContext=new MissionContext {SessionId=session};
        return LobbyStartPacketRuntime.BeginNativeStart(Mp,session);
    }
    private static void Arm(long session=2,bool success=true,bool newMembers=true)
    {
        Mp.activeLobby.startGame=MultiplayerSavePreparation.Current?.SaveName??"GO!map";
        var receipt=Begin(session); Check(receipt!=null,"authenticated actual start must bind");
        if(newMembers) Mp.gameMembers=Members();
        LobbyStartPacketRuntime.EndNativeStart(receipt,success);
    }
    private static void Replay(bool worker=false)=>LobbyStartPacketRuntime.Replay(Mp,worker);
    private static void Main()
    {
        foreach(string staleAnnouncement in new[]{null,"older.msv"})
        {
            Reset(); Mp.activeLobby.startGame=staleAnnouncement; Stage();
            Check(Queue.Count==1,"early seed survives absent/stale Steam announcement");
            Replay(); Check(Boundary.Processed.Count==0,"no replay before actual start");
            Arm(); MultiplayerSavePreparation.Current=null; Replay();
            Check(Boundary.Processed.Count==1&&Queue.Count==0,"native success replays early seed once after scope publication ends");
            Replay(); Check(Boundary.Processed.Count==1,"no duplicate replay");
            Check(Mp.gameMembers[0].packetsReceived==1,"current native member receives Vanilla counter");
        }
        Reset(); Stage(); Arm(); Proposal("B"); Stage();
        Check(Queue.Count==1,"new token replaces old armed buffer, retains new seed");
        Arm(3); MultiplayerSavePreparation.Current=null; Replay(); Check(Boundary.Processed.Count==1,"only B seed replayed");

        Reset(); Stage(); Arm(); Proposal("B"); Replay();
        Check(Queue.Count==0&&Boundary.Processed.Count==0,"new proposal supersedes old armed queue even without new packet");

        Reset(); Stage(); Arm(success:false); Replay(); Check(Queue.Count==0&&Boundary.Processed.Count==0,"failed native result discards despite mapLoaded true");
        Reset(); Stage(); Arm(newMembers:false); Replay(); Check(Queue.Count==0&&Boundary.Processed.Count==0,"old gameMembers cannot arm new world");
        Reset(); Stage(); Mp.activeLobby.startGame="test.msv"; var notLoaded=Begin(); Mp.gameMembers=Members(); Mp.mapLoaded=false;
        LobbyStartPacketRuntime.EndNativeStart(notLoaded,true); Replay(); Check(Queue.Count==0&&Boundary.Processed.Count==0,"native mapLoaded milestone is required");
        Reset(); Stage(); Mp.activeLobby.startGame="test.msv"; var oldReceipt=Begin();
        Proposal("B"); Stage(); Arm(3); LobbyStartPacketRuntime.EndNativeStart(oldReceipt,false);
        Check(Queue.Count==1,"late failed receipt cannot discard newer armed epoch");
        MultiplayerSavePreparation.Current=null; Replay(); Check(Boundary.Processed.Count==1,"newer epoch survives failed old native receipt");
        Reset(); Stage(); Arm(); Replay(true); Check(Queue.Count==1,"worker prolog cannot replay");
        Boundary.Main=false; Replay(); Check(Queue.Count==1,"foreign thread cannot replay"); Boundary.Main=true; Replay(); Check(Boundary.Processed.Count==1,"main thread replay preserved");

        Reset(); Stage(); Arm(); Mp.activeLobby.Humans=new ulong[]{2001,2002,2003}; Replay();
        Check(Queue.Count==0&&Boundary.Processed.Count==0,"same lobby altered roster is rejected");
        Reset(); Stage(); Arm(); Boundary.Host=2003; Replay(); Check(Queue.Count==0&&Boundary.Processed.Count==0,"changed authenticated owner rejects buffered data");

        foreach(bool throwAfterReplace in new[]{false,true})
        {
            Reset(); Stage(); Stage(4,Array.Empty<byte>()); Arm();
            Boundary.DuringProcess=()=>{
                Boundary.DuringProcess=null; Proposal("B","next.msv"); Stage();
                if(throwAfterReplace) throw new InvalidOperationException("old consumer callback failed after replacement");
            };
            Replay(); Check(Boundary.Processed.Count==1,"old replay stops immediately after callback replaces epoch");
            Check(Queue.Count==1,"new epoch survives old callback return/exception");
            Arm(3); MultiplayerSavePreparation.Current=null; Replay();
            Check(Boundary.Processed.Count==2&&Queue.Count==0,"new epoch remains independently usable");
        }
        Reset(); for(int i=0;i<65;i++) Stage();
        Check(Queue.Poisoned&&Queue.Count==0,"overflow discards and poisons entire epoch"); int logCount=Boundary.Logs.Count;
        Stage(); Stage(); Check(Queue.Poisoned&&Boundary.Logs.Count==logCount,"overflow summary emitted only once");
        Proposal("B"); Stage(); Check(!Queue.Poisoned&&Queue.Count==1,"only new epoch removes poison");

        Reset(); MapRecordLoadCoordinator.Token="M1"; MultiplayerSavePreparation.Current=null; Stage();
        Check(Queue.Count==1,"map admission also preserves early seed"); Arm(); Replay(); Check(Boundary.Processed.Count==1,"map seed replays after native start");
        Reset(); Stage(); LobbyStartPacketRuntime.Exit(Mp); Check(Queue.Count==0,"normal exit discards pending world packets");
        Console.WriteLine("LobbyStartRuntime: "+checks+" checks PASS (external boundaries doubled, production runtime/queue unchanged).");
    }
}
