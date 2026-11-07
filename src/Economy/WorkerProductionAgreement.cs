using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using APIShared.Internal;
using APIShared.Lobby;
using APIShared.Localization;
using APIShared.Networking;
using BepInEx.Logging;
using Steamworks;

namespace APIShared.Economy
{
    // Immutable boundary between native/profile/persistence owners and the lobby protocol.
    // CanExecute never conceals the actual installed native-history protection key.
    internal sealed class WorkerProductionAgreementDescriptor
    {
        internal WorkerProductionAgreementDescriptor(string serviceKey, string rulesDigest, string wireMode,
            string sidecarKey, int readableSidecarSchema, int requiredSidecarSchema, bool canExecute,
            bool hasDemand, bool requiredDataValid, string savedRecordDigest = "")
        {
            ServiceKey=serviceKey??""; RulesDigest=rulesDigest??""; WireMode=wireMode??"";
            SidecarKey=sidecarKey??""; ReadableSidecarSchema=readableSidecarSchema;
            RequiredSidecarSchema=requiredSidecarSchema; CanExecute=canExecute;
            HasDemand=hasDemand; RequiredDataValid=requiredDataValid; SavedRecordDigest=savedRecordDigest??"";
        }
        internal readonly string ServiceKey,RulesDigest,WireMode,SidecarKey,SavedRecordDigest;
        internal readonly int ReadableSidecarSchema,RequiredSidecarSchema;
        internal readonly bool CanExecute,HasDemand,RequiredDataValid;
        internal string LocalFailure => !RequiredDataValid ? "InvalidData" :
            RequiredSidecarSchema<0 || ReadableSidecarSchema<RequiredSidecarSchema ? "Sidecar" :
            RequiredSidecarSchema>0 && SidecarKey.Length==0 ? "Sidecar" :
            (HasDemand || RequiredSidecarSchema>0) && (!CanExecute || ServiceKey.Length==0 || RulesDigest.Length==0) ? "Unavailable" : null;
        internal string Digest()
        {
            using(var bytes=new MemoryStream()) using(var writer=new BinaryWriter(bytes,Encoding.UTF8))
            {
                writer.Write(ServiceKey);writer.Write(RulesDigest);writer.Write(WireMode);writer.Write(SidecarKey);
                writer.Write(ReadableSidecarSchema);writer.Write(RequiredSidecarSchema);writer.Write(CanExecute);
                writer.Write(HasDemand);writer.Write(RequiredDataValid);writer.Write(SavedRecordDigest);writer.Flush();
                using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-","");
            }
        }
        internal bool Matches(WorkerProductionAgreementDescriptor other) => other!=null &&
            LocalFailure==null && other.LocalFailure==null && ServiceKey==other.ServiceKey &&
            RulesDigest==other.RulesDigest && WireMode==other.WireMode && SidecarKey==other.SidecarKey &&
            CanExecute==other.CanExecute &&
            RequiredSidecarSchema==other.RequiredSidecarSchema && SavedRecordDigest==other.SavedRecordDigest;
    }

    internal sealed class WorkerProductionAgreementResult
    {
        internal WorkerProductionAgreementResult(string code,string diagnostic="")
        { Code=code??"Unavailable";Diagnostic=diagnostic??""; }
        internal string Code { get; }
        internal string Diagnostic { get; }
        internal bool Ready => Code.Length==0;
        internal bool Waiting => Code=="Pending" || Code=="Roster";
    }

    // Pure, epoch-bound host receipt set. A missing report never means unavailable.
    internal sealed class WorkerProductionAgreementRound
    {
        private readonly HashSet<ulong> expected=new HashSet<ulong>();
        private readonly Dictionary<ulong,WorkerProductionAgreementDescriptor> reports=new Dictionary<ulong,WorkerProductionAgreementDescriptor>();
        private string token="";
        internal void Reset(string nonce,IEnumerable<ulong> peers)
        {token=nonce??"";expected.Clear();reports.Clear();foreach(ulong peer in peers)expected.Add(peer);}
        internal bool Report(ulong sender,string nonce,WorkerProductionAgreementDescriptor value)
        {if(value==null || token.Length==0 || nonce!=token || !expected.Contains(sender))return false;reports[sender]=value;return true;}
        internal WorkerProductionAgreementResult Resolve(WorkerProductionAgreementDescriptor local)
        {
            if(local==null)return new WorkerProductionAgreementResult("Unavailable");
            if(local.LocalFailure!=null)return new WorkerProductionAgreementResult(local.LocalFailure);
            foreach(ulong peer in expected)
            {
                if(!reports.TryGetValue(peer,out var value))return new WorkerProductionAgreementResult("Pending");
                if(!local.Matches(value))return new WorkerProductionAgreementResult("Mismatch");
            }
            return new WorkerProductionAgreementResult("");
        }
    }

    // One neutral protocol for every production consumer, including none. It reuses the
    // shared sender-aware MessageChannel and lobby observer; no additional frame publisher.
    internal static class WorkerProductionAgreement
    {
        private static readonly WorkerProductionAgreementRound round=new WorkerProductionAgreementRound();
        private static Func<WorkerProductionAgreementDescriptor> capture;
        private static Action<string> selectedSaveChanged;
        private static MessageChannel<WorkerProductionAgreementPacket> channel;
        private static ManualLogSource log;
        private static LobbyStateSnapshot roster;
        private static WorkerProductionAgreementDescriptor local;
        private static WorkerProductionAgreementResult current=new WorkerProductionAgreementResult("Unavailable");
        private static string rosterKey="",token="",localDigest="",preparedPath;
        private static string approvedDescriptorDigest="";
        private static ulong host,localId;
        private static bool initialized,committed;
        private static int posted;
        internal static event Action Changed;
        internal static WorkerProductionAgreementResult Current => Volatile.Read(ref current);
        internal static string ApprovedDescriptorDigest => Volatile.Read(ref approvedDescriptorDigest);
        internal static string PreparedSavePath => preparedPath;
        internal static string PreparedDataPath => preparedPath;

        internal static void Initialize(ManualLogSource logger,Func<WorkerProductionAgreementDescriptor> captureDescriptor,
            Action<string> onSelectedSaveChanged=null)
        {
            if(initialized)return;
            if(!UnityMainThreadDispatch.IsMainThread)throw new InvalidOperationException("Production agreement initializes on Unity.");
            log=logger;capture=captureDescriptor??throw new ArgumentNullException(nameof(captureDescriptor));selectedSaveChanged=onSelectedSaveChanged;
            // This registration is unconditional, even when native initialization failed.
            channel=ApiShared.ForMod(APISharedPlugin.PluginGuid).Network.RegisterMessage<WorkerProductionAgreementPacket>(
                "worker-production-agreement",Receive,new MessageChannelOptions<WorkerProductionAgreementPacket>{MaximumBodyBytes=8192});
            if(!LobbyLaunchGuards.TryRegister(APISharedPlugin.PluginGuid,"worker-production-agreement",Guard,out string reason,int.MaxValue))
                throw new InvalidOperationException(reason);
            if(!LobbyLaunchStatus.TryRegisterProvider(APISharedPlugin.PluginGuid,"worker-production-agreement",_=>StatusItems(),out reason))
                throw new InvalidOperationException(reason);
            if(!ApiShared.Current.TryGetLobbyState(APISharedPlugin.PluginGuid,out var lobby,out var diagnostic) ||
                !lobby.TryRegisterObserver("worker-production-agreement",Observe,out diagnostic))
                throw new InvalidOperationException(diagnostic?.Reason??"Production lobby observation unavailable.");
            initialized=true;Refresh();
        }

        internal static void SetPreparedSavePath(string path) => SetPreparedDataPath(path);
        // The selected archive can be a save or a map. Both own simulation records.
        internal static void SetPreparedDataPath(string path)
        {
            if(string.Equals(path,preparedPath,StringComparison.OrdinalIgnoreCase))return;
            preparedPath=path;selectedSaveChanged?.Invoke(path);RequestRefresh();
        }
        internal static void RequestRefresh()
        {
            Volatile.Write(ref approvedDescriptorDigest,"");
            if(Interlocked.CompareExchange(ref posted,1,0)!=0)return;
            if(!UnityMainThreadDispatch.TryEnqueue(()=>{Interlocked.Exchange(ref posted,0);Refresh();}))
                Interlocked.Exchange(ref posted,0);
        }
        internal static bool TryPrepareSaveAdmission(string path,out bool waiting,out string diagnostic)
        {
            SetPreparedDataPath(path);Refresh();var result=Current;
            if(result.Ready && rosterKey.Length==0)
            {waiting=true;diagnostic=UiText.Get("WorkerProductionAgreement.Roster");return false;}
            waiting=result.Waiting;diagnostic=result.Ready?"":UiText.Get("WorkerProductionAgreement."+result.Code);
            return result.Ready;
        }
        internal static bool TryAdmitCurrent(out string diagnostic)
        {Refresh();diagnostic=Current.Diagnostic;return Current.Ready && ApprovedDescriptorDigest.Length!=0;}
        private static string Guard(LobbyLaunchGuardContext context)
        {
            if(context.Command!="HostLoadGame")
            {
                try
                {
                    if(!MapRecordLoadCoordinator.PrepareSelectedProductionData(context.Lobby,context.RequiresPeerAgreement))
                    {SetResult("Pending");return UiText.Get("WorkerProductionAgreement.Pending");}
                }
                catch(Exception error)
                {
                    NativeApiLog.Error(log,"Selected map production requirements: "+error);
                    return Reject("InvalidData",!context.RequiresPeerAgreement);
                }
            }
            Refresh();
            if(local==null)return Reject("Unavailable",!context.RequiresPeerAgreement);
            if(local.LocalFailure!=null)return Reject(local.LocalFailure,!context.RequiresPeerAgreement);
            if(!context.RequiresPeerAgreement)return null;
            if(rosterKey.Length==0)return UiText.Get("WorkerProductionAgreement.Roster");
            return Current.Ready?null:UiText.Get("WorkerProductionAgreement."+Current.Code);
        }
        private static string Reject(string code,bool localAttempt)
        {
            if(localAttempt)WorkerProductionModifiers.NotifyRejectedLocalLaunch(code);
            return UiText.Get("WorkerProductionAgreement."+code);
        }
        private static IEnumerable<LobbyLaunchStatusItem> StatusItems()
        {
            var value=Current;
            return value.Ready?Array.Empty<LobbyLaunchStatusItem>():new[]{new LobbyLaunchStatusItem(
                UiText.Get("WorkerProductionAgreement."+value.Code),value.Waiting?LobbyLaunchStatusKind.Loading:LobbyLaunchStatusKind.Blocking,value.Diagnostic)};
        }
        private static void Observe(LobbyStateSnapshot value)
        {
            if(value==null)return;
            if(!value.LobbyId.HasValue && value.PreserveForMapTransition)return;
            roster=value;
            string next=RosterKey(value);
            if(next!=rosterKey){rosterKey=next;token="";round.Reset("",Array.Empty<ulong>());committed=false;}
            Refresh();
        }
        private static string RosterKey(LobbyStateSnapshot value)
        {
            if(!value.LobbyId.HasValue || value.HasUnresolvedPlayers || value.LocalPlayerId<1 ||
                !value.Players.ContainsKey(value.LocalPlayerId) || value.Players.Count<1 || value.Error.Length!=0)return "";
            return value.LobbyId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+
                string.Join(",",value.Players.Values.OrderBy(id=>id).Select(id=>id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        private static void Refresh()
        {
            if(capture==null)return;
            try
            {
                var next=capture();if(next==null)throw new InvalidOperationException("Production descriptor missing.");
                string digest=next.Digest();bool changed=digest!=localDigest;local=next;localDigest=digest;
                if(rosterKey.Length==0)
                {SetResult(next.LocalFailure??(roster?.LobbyId.HasValue==true?"Roster":""));return;}
                var activeLobby=Platform_Multiplayer.Instance?.activeLobby;
                bool transition=activeLobby==null && MissionLifecycleService.ActiveContext?.Mode.IsRealMultiplayer==true;
                // The shared observer freezes its authenticated roster during map start.
                // Steam can already have retired that lobby; only the tracked MP attempt
                // may keep its last authenticated host. Other contexts require the live ID.
                if(!transition && (activeLobby==null || activeLobby.id.m_SteamID!=roster.LobbyId))
                {SetResult("Roster");return;}
                localId=SteamUser.GetSteamID().m_SteamID;
                ulong nextHost=transition?host:SteamMatchmaking.GetLobbyOwner(new CSteamID(roster.LobbyId.Value)).m_SteamID;
                if(nextHost!=host){host=nextHost;changed=true;token="";round.Reset("",Array.Empty<ulong>());committed=false;}
                if(!roster.Players.Values.Contains(localId) || !roster.Players.Values.Contains(host)){SetResult("Roster");return;}
                if(roster.Players.Count==1){SetResult(local.LocalFailure??"");return;}
                if(host==localId)
                {
                    if(changed || token.Length==0)BeginChallenge();
                    CompleteHost();
                }
                else
                {
                    if(changed){committed=false;Send(0,host,"");if(token.Length!=0)Send(2,host,token);}
                    SetResult(local.LocalFailure??(committed?"":"Pending"));
                }
            }
            catch(Exception error){SetResult("Unavailable",error.Message);}
        }
        private static void BeginChallenge()
        {
            token=Guid.NewGuid().ToString("N");round.Reset(token,roster.Players.Values.Where(peer=>peer!=localId));committed=false;SetResult("Pending");
            foreach(ulong peer in roster.Players.Values)if(peer!=localId)Send(1,peer,token);
        }
        private static void CompleteHost()
        {
            var resolution=round.Resolve(local);if(!resolution.Ready){SetResult(resolution.Code,resolution.Diagnostic);return;}
            if(!committed){foreach(ulong peer in roster.Players.Values)if(peer!=localId)Send(3,peer,token);committed=true;}
            SetResult("");
        }
        private static void Receive(NetworkMessage<WorkerProductionAgreementPacket> message)
        {
            var packet=message.Packet;
            if(packet==null || rosterKey.Length==0 || roster==null || !roster.Players.Values.Contains(message.SenderSteamId))return;
            if(packet.Version!=WorkerProductionAgreementPacket.CurrentVersion || packet.Operation<0){SetResult("Protocol");return;}
            if(packet.Lobby!=roster.LobbyId || packet.Roster!=rosterKey || packet.Descriptor==null ||
                packet.Operation>0 && (packet.Token==null || packet.Token.Length!=32))return;
            Refresh();
            if(packet.Operation==0 && host==localId){BeginChallenge();return;}
            if(packet.Operation==1 && message.SenderSteamId==host && host!=localId)
            {token=packet.Token;committed=false;Send(2,host,token);SetResult(local.LocalFailure??"Pending");return;}
            if(packet.Operation==2 && host==localId && packet.Token==token)
            {if(round.Report(message.SenderSteamId,packet.Token,packet.Descriptor))CompleteHost();return;}
            if(packet.Operation==3 && message.SenderSteamId==host && host!=localId && packet.Token==token)
            {committed=local.Matches(packet.Descriptor);SetResult(local.LocalFailure??(committed?"":"Mismatch"));}
        }
        private static void Send(int operation,ulong peer,string nonce)
        {
            var sent=channel.SendToSteamPeer(new WorkerProductionAgreementPacket{Operation=operation,
                Lobby=roster.LobbyId.Value,Roster=rosterKey,Token=nonce,Descriptor=local},peer);
            if(!sent.Submitted)
            {
                // Submission can fail before the remote channel is ready. Invalidate
                // this round so the next existing input/guard event retries a challenge
                // or hello instead of leaving a permanently pending, unsent receipt.
                localDigest="";committed=false;SetResult("Unavailable",sent.Reason);
                throw new InvalidOperationException(sent.Reason);
            }
        }
        private static void SetResult(string code,string diagnostic="")
        {
            Volatile.Write(ref approvedDescriptorDigest,string.IsNullOrEmpty(code)&&rosterKey.Length!=0?localDigest:"");
            var previous=Current;if(previous.Code==code && previous.Diagnostic==diagnostic)return;
            Volatile.Write(ref current,new WorkerProductionAgreementResult(code,diagnostic));
            WorkerProductionModifiers.UpdatePeerReadiness(code,diagnostic);
            foreach(Action callback in Changed?.GetInvocationList()??Array.Empty<Delegate>())
                try{callback();}catch(Exception error){try{NativeApiLog.Error(log,"Production agreement observer: "+error);}catch{}}
            LobbyLaunchStatus.RequestRefresh();
        }
    }
}
