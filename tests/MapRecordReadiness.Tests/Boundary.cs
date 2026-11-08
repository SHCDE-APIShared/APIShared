using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Text;
using APIShared;
using APIShared.Savegames;

// Controlled managed/Steam/archive boundaries. Both production coordinators and the
// actual packet formatter/agreement are linked unchanged; no game or hook is loaded.
internal static class Boundary
{
    internal static Delegate MapHook, SaveHook;
    internal static int MapStarts, SaveStarts, Captures, Popups;
    internal static bool MainThread = true, Prepare = true, SendOk = true, SaveVeto;
    internal static ulong Local = 2001, Host = 2001;
    internal static string Settings = "host-settings", Path;
    internal static FileHeader LookupHeader;
    internal static Action DuringCapture, DuringPrepare;
    internal static IReadOnlyList<SavegameRecordDigest> Records = Array.Empty<SavegameRecordDigest>();
    internal static readonly Dictionary<Type, Action<object, ulong>> Receivers = new Dictionary<Type, Action<object, ulong>>();
    internal static readonly List<object> Sent = new List<object>();
    internal static readonly List<string> Logs = new List<string>();
    internal static readonly List<string> Broadcast = new List<string>(), LocalChat = new List<string>(),
        WaitingBroadcast = new List<string>(), WaitingLocal = new List<string>();
    internal static readonly Queue<Action> Presented = new Queue<Action>();
    internal static void MapOriginal(Platform_Multiplayer self) { MapStarts++; }
    internal static void SaveOriginal(Platform_Multiplayer self, string name) { SaveStarts++; }
    internal static void Receive<T>(T packet, ulong sender) where T : class => Receivers[typeof(T)](packet, sender);
}
namespace BepInEx.Logging { public sealed class ManualLogSource { } }
namespace SHCDESE.API { internal sealed class Unused { } }
namespace UnityEngine { internal sealed class Unused { } }
namespace Steamworks
{
    public struct CSteamID { public ulong m_SteamID; public CSteamID(ulong value) { m_SteamID = value; } }
    internal static class SteamUser { internal static CSteamID GetSteamID() => new CSteamID(Boundary.Local); }
    internal static class SteamMatchmaking { internal static CSteamID GetLobbyOwner(CSteamID lobby) => new CSteamID(Boundary.Host); }
    internal static class SteamFriends { internal static string GetFriendPersonaName(CSteamID id) => "peer-" + id.m_SteamID; }
}
public sealed class Platform_Multiplayer
{
    public static Platform_Multiplayer Instance;
    public MPLobby activeLobby;
    public void HostStartGame() { }
    public void HostLoadGame(string name) { }
    public sealed class MPLobby
    {
        public Steamworks.CSteamID id;
        public bool isHost, coopTrailGame, coopOrderSwapped;
        public int coopTrailID, coopSelectedMission;
        public string mapFileName = "readiness.map", crc = "17", settings = "native-settings", maxPlayers = "8", AIPlayers = "0", gameTypeCoop = "0", Aiv = "aiv";
        public readonly List<MPLobbyMember> members = new List<MPLobbyMember>();
        public string AIVDataChecksum() => Aiv;
        public int CountAIPlayers() => members.Count(member => member.id.m_SteamID < 1000);
        public int getThisPlayerFromSteamID(ulong id) => members.FindIndex(member => member.id.m_SteamID == id) + 1;
    }
    public sealed class MPLobbyMember { public Steamworks.CSteamID id; public bool SkirmishMember, dummyToBeKicked; }
}
public sealed class FileHeader { public string fileName, filePath; public uint crc; public int xPlaySaveChecksum, xPlaySaveTime; }
public sealed class MapFileManager
{
    public static readonly MapFileManager Instance = new MapFileManager();
    public FileHeader GetHeaderFromFileNameMP(string name, int crc) => Boundary.LookupHeader ?? new FileHeader { fileName = name, filePath = Boundary.Path, crc = (uint)crc };
    public FileHeader GetHeaderFromMpSaveFileName(string name) => new FileHeader { fileName = name, filePath = Boundary.Path, xPlaySaveChecksum = 1, xPlaySaveTime = 2 };
}
namespace CrusaderDE
{
    internal static class HUD_ConfirmationPopup { internal static void ShowConfirmationOKMessage(string title, Action ok, string reason) { Boundary.Popups++; } }
    internal sealed class FRONT_Multiplayer
    {
        private FileHeader selectedMPHeader;
        public Platform_Multiplayer.MPLobby currentLobby;
        internal void Select(FileHeader header){selectedMPHeader=header;}
    }
}
namespace MonoMod.RuntimeDetour
{
    internal sealed class HookConfig { internal bool ManualApply; }
    internal sealed class Hook : IDisposable
    {
        private readonly MethodInfo target;
        internal Hook(MethodInfo target, Delegate callback, HookConfig config)
        { this.target = target; if (target.Name == "HostStartGame") Boundary.MapHook = callback; else Boundary.SaveHook = callback; }
        internal T GenerateTrampoline<T>() => (T)(object)Delegate.CreateDelegate(typeof(T), typeof(Boundary).GetMethod(
            target.Name == "HostStartGame" ? nameof(Boundary.MapOriginal) : nameof(Boundary.SaveOriginal), BindingFlags.Static | BindingFlags.NonPublic));
        internal void Apply() { }
        public void Dispose() { }
    }
}
namespace APIShared.Internal
{
    internal static class UnityMainThreadDispatch
    { internal static bool IsMainThread => Boundary.MainThread; internal static bool TryEnqueue(Action action) { Boundary.Presented.Enqueue(action); return true; } }
}
namespace APIShared.Presentation
{
    internal interface IUiFrameWorkRegistration { void SetContinuous(bool value); void CancelPending(); }
    internal sealed class Work : IUiFrameWorkRegistration { internal bool Continuous; public void SetContinuous(bool value) { Continuous = value; } public void CancelPending() { } }
    internal static class UiWorkRuntime
    {
        internal static readonly Dictionary<string, Action> Ticks = new Dictionary<string, Action>();
        internal static bool TryRegister(string owner, string id, Action action, int priority, out IUiFrameWorkRegistration work, out string reason)
        { Ticks.Add(id, action); work = new Work(); reason = ""; return true; }
    }
}
namespace APIShared.Networking
{
    internal sealed class NetworkMessage<T> { internal T Packet; internal ulong SenderSteamId; }
    internal sealed class MessageChannelOptions<T> { internal int MaximumBodyBytes; }
    internal sealed class SendResult { internal bool Submitted => Boundary.SendOk; internal string Reason => "send-failed"; }
    internal sealed class MessageChannel<T> where T : class
    {
        internal SendResult SendToLobby(T packet) { Boundary.Sent.Add(packet); return new SendResult(); }
        internal SendResult SendToSteamPeer(T packet, ulong id) { Boundary.Sent.Add(packet); return new SendResult(); }
    }
    internal sealed class NetworkClient
    {
        internal MessageChannel<T> RegisterMessage<T>(string id, Action<NetworkMessage<T>> receive, MessageChannelOptions<T> options) where T : class
        { Boundary.Receivers.Add(typeof(T), (packet, sender) => receive(new NetworkMessage<T> { Packet = (T)packet, SenderSteamId = sender })); return new MessageChannel<T>(); }
    }
}
namespace APIShared.Localization
{
    internal static class UiText
    {
        internal static string Get(string key, params object[] arguments) =>
            arguments.Length == 0 ? key : key + " " + string.Join(", ", arguments);
    }
}
namespace APIShared.Economy
{
    // External production agreement only; the real save coordinator below remains under test.
    internal static class WorkerProductionAgreement
    {
        internal static bool Waiting,Blocked;
        internal static int Checks;
        internal static string SavePath;
        internal static string PreparedDataPath=>SavePath;
        internal static void SetPreparedDataPath(string path){SavePath=path;}
        internal static void SetPreparedSavePath(string path){SavePath=path;}
        internal static bool TryPrepareSaveAdmission(string path,out bool waiting,out string diagnostic)
        {Checks++;SavePath=path;waiting=Waiting;diagnostic=Blocked?"production rejected":"production pending";return !Waiting&&!Blocked;}
    }
}
namespace APIShared.Lobby
{
    internal readonly struct LobbyLaunchGuardDecision { internal bool Blocked => Boundary.SaveVeto; internal string Reason => "fixture save veto"; }
    internal static class LobbyLaunchStatus
    {
        internal static void ReportPreparationWaiting(BepInEx.Logging.ManualLogSource fallback, string text, string detail,
            ulong lobbyId, bool broadcast, bool showLocal)
        {
            Boundary.Logs.Add(detail + ": " + text);
            if (broadcast) Boundary.WaitingBroadcast.Add(text);
            if (showLocal) Boundary.WaitingLocal.Add(text);
        }
        internal static void ReportPreparationBlocked(BepInEx.Logging.ManualLogSource fallback, string text, string detail,
            ulong lobbyId, bool broadcast, bool showLocal)
        {
            Boundary.Logs.Add(detail + ": " + text);
            if (broadcast) Boundary.Broadcast.Add(text);
            if (showLocal) Boundary.LocalChat.Add(text);
        }
    }
    internal static class LobbyLaunchGuards
    {internal static LobbyLaunchGuardDecision EvaluatePreparedMultiplayerLoad(int humans)=>default;}
}
namespace APIShared.ModSettings { internal static class ModSettingsApplication { internal static bool PrepareLaunch() { Boundary.DuringPrepare?.Invoke(); return Boundary.Prepare; } } }
namespace APIShared.Savegames
{
    internal static class VersionedSaveDataRuntime
    {
        internal static IReadOnlyList<SavegameRecordDigest> CaptureMapSimulationDigests(string path)
        { Boundary.Captures++; Boundary.DuringCapture?.Invoke(); return Boundary.Records; }
        internal static IReadOnlyList<SavegameRecordDigest> CaptureSimulationDigests(string path) => Array.Empty<SavegameRecordDigest>();
    }
}
namespace APIShared
{
    internal static class ApiShared { internal static Client ForMod(string owner) => new Client(); }
    internal sealed class Client { internal readonly Networking.NetworkClient Network = new Networking.NetworkClient(); }
    internal static class APISharedPlugin { internal const string PluginGuid = "APIShared-Test"; }
    internal static class NativeApiLog
    { internal static void Info(object log, string text) { Boundary.Logs.Add(text); } internal static void Debug(object log, string text) { Boundary.Logs.Add(text); } internal static void Error(object log, string text) { Boundary.Logs.Add(text); } internal static void Warning(object log, string text) { Boundary.Logs.Add(text); } }
    internal sealed class SavegameModSettingsRecord { internal readonly Dictionary<string, int> Mods = new Dictionary<string, int>(); }
    internal static class SavegameModSettings
    {
        internal static string CaptureHostFingerprint() => SavegameLoadAgreement.Fingerprint(CaptureHostSettingsForReadiness());
        internal static Dictionary<string, Dictionary<string, byte[]>> CaptureHostSettingsForReadiness() =>
            new Dictionary<string, Dictionary<string, byte[]>> {
                ["fixture.mod"] = new Dictionary<string, byte[]> { ["HostSetting"] = Encoding.UTF8.GetBytes(Boundary.Settings) }
            };
        internal static SavegameModSettingsRecord PrepareHostSave(string path) => null;
        internal static void PublishPreparedHostValues() { }
        internal static void CancelLoadChoice() { }
        internal static void AcceptPreparedClient(string path, SavegameModSettingsRecord record) { }
        internal static bool TryDeserialize(byte[] bytes, out SavegameModSettingsRecord record) { record = null; return false; }
    }
}
