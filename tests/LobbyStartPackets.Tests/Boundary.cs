// Only external boundaries are doubled. Production staging/binding/replay and queue execute unchanged.
// Permanent hook IL is independently compiled/generated against the installed SE in APISharedTests.
using System;
using System.Collections.Generic;
using System.Reflection;
namespace Steamworks
{
    public struct CSteamID { public ulong m_SteamID; public CSteamID(ulong value) { m_SteamID=value; } }
    public static class SteamUser { public static CSteamID GetSteamID()=>new CSteamID(Boundary.Local); }
    public static class SteamMatchmaking { public static CSteamID GetLobbyOwner(CSteamID id)=>new CSteamID(Boundary.Host); }
}
public static class Boundary
{
    public static ulong Local=2002, Host=2001;
    public static bool Main=true;
    public static Action DuringProcess;
    public static readonly List<short> Processed=new List<short>();
    public static readonly List<string> Logs=new List<string>();
}
public class Platform_Multiplayer
{
    public static Platform_Multiplayer Instance;
    public static bool MPGameActive;
    public MPLobby activeLobby;
    public List<MPGameMember> gameMembers;
    public bool mapLoaded=true;
    public class MPLobby { public Steamworks.CSteamID id=new Steamworks.CSteamID(99); public bool isHost; public string startGame; public ulong[] Humans={2001,2002}; }
    public class MPGameMember { public ulong steamID; public bool skirmishAI,isSelf,kicked,stillWithSteamConnection=true; public int packetsReceived; }
    public class MPData { public short packetType; public byte[] data; public int dataOffset,dataLength; }
    private bool processMessage(MPData data,MPGameMember member,bool fromThread)
    { Boundary.Processed.Add(data.packetType); Boundary.DuringProcess?.Invoke(); return true; }
}
namespace APIShared
{
    public static class APISharedPlugin { public const string PluginGuid="fixture"; }
    public static class NativeApiLog
    { public static void Info(object log,string text)=>Boundary.Logs.Add(text); public static void Error(object log,string text)=>Boundary.Logs.Add(text); }
    public sealed class MissionContext { public long SessionId; }
    public static class MissionLifecycleService { public static MissionContext ActiveContext; }
    public static class LobbyReadinessSupport { public static ulong[] ObservedHumans(Platform_Multiplayer.MPLobby lobby)=>(ulong[])lobby.Humans.Clone(); }
    public static class MapRecordLoadCoordinator
    {
        public static string Token;
        public static bool TryGetLobbyPacketAdmission(out string token,out ulong lobbyId,out ulong hostId,out IReadOnlyList<ulong> peers)
        { token=Token; lobbyId=99; hostId=Boundary.Host; peers=new ulong[]{2001,2002}; return token!=null; }
    }
}
namespace APIShared.Savegames
{
    public enum MultiplayerSavePreparationPhase { Preparing,Ended }
    public sealed class MultiplayerSavePreparationSnapshot
    { public string OperationId,SaveName="test.msv"; public ulong LobbyId=99,HostId=2001; public IReadOnlyList<ulong> HumanPeers=new ulong[]{2001,2002}; public MultiplayerSavePreparationPhase Phase; }
    public static class MultiplayerSavePreparation { public static MultiplayerSavePreparationSnapshot Current; }
}
namespace APIShared.Internal
{
    public static class UnityMainThreadDispatch { public static bool IsMainThread=>Boundary.Main; }
    public static class MissionEvents { public static EventStream Ended=new EventStream(); }
    public sealed class Notification { public APIShared.MissionContext Context; }
    public sealed class EventStream
    { public Action<Notification> Action; public IDisposable Subscribe(Action<Notification> action) { Action=action; return new Empty(); } }
    public sealed class Empty:IDisposable { public void Dispose(){} }
}
namespace R3 {}
namespace BepInEx.Logging { public sealed class ManualLogSource {} }
namespace SHCDESE.API { public sealed class GameNetworkAPI {} }
namespace MonoMod.Cil
{
    public sealed class ILContext:IDisposable
    { public delegate void Manipulator(ILContext il); public ILContext(object body){} public object ReferenceBag; public void Invoke(Manipulator m)=>m(this); public void Dispose(){} }
}
namespace MonoMod.Utils
{
    public sealed class DynamicMethodDefinition:IDisposable
    { public DynamicMethodDefinition(MethodInfo method){} public object Definition; public void Generate(){} public void Dispose(){} }
    public static class RuntimeILReferenceBag { public static object Instance; }
}
namespace APIShared.Patching
{
    public sealed class SafeIlHook
    {
        public bool IsApplied=true;
        public static bool TryInstall(string owner,string id,MethodInfo method,MonoMod.Cil.ILContext.Manipulator edit,object log,out SafeIlHook hook,out string failure)
        { hook=new SafeIlHook(); failure=null; return true; }
    }
}
namespace APIShared.ScriptExtenderFixes
{
    internal static class LobbyStartPacketIl
    { internal static void Stage(MonoMod.Cil.ILContext il){} internal static void Replay(MonoMod.Cil.ILContext il){} internal static void Exit(MonoMod.Cil.ILContext il){} }
}
