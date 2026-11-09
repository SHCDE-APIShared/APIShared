using APIShared;
using APIShared.Savegames;
using MessagePack;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    private static Platform_Multiplayer Mp => Platform_Multiplayer.Instance;
    private static SavegameLoadPacket RequestPacket => Boundary.Sent.OfType<MapRecordReadinessPacket>().Last(packet => packet.Value.Operation == 0).Value;
    private static SavegameRecordDigest Present(byte value) => new SavegameRecordDigest("foreign.mod", "foreign-record", SavegameRecordState.Present, Enumerable.Repeat(value, 32).ToArray());
    private static void Reset(bool solo = false, bool client = false)
    {
        MapRecordLoadCoordinator.FinishLoad(); SavegameLoadCoordinator.FinishLoad();
        APIShared.Economy.WorkerProductionAgreement.Waiting=APIShared.Economy.WorkerProductionAgreement.Blocked=false;
        APIShared.Economy.WorkerProductionAgreement.Checks=0;
        Boundary.MainThread = Boundary.Prepare = Boundary.SendOk = true; Boundary.SaveVeto = false; Boundary.Local = client ? 2002UL : 2001UL; Boundary.Host = 2001;
        Boundary.MapStarts = Boundary.SaveStarts = Boundary.Captures = Boundary.Popups = 0;Boundary.LookupHeader=null;
        Boundary.Settings = "host-settings"; Boundary.Records = Array.Empty<SavegameRecordDigest>(); Boundary.DuringCapture = Boundary.DuringPrepare = null;
        Boundary.Logs.Clear(); Boundary.Sent.Clear(); Boundary.Broadcast.Clear(); Boundary.LocalChat.Clear();
        Boundary.WaitingBroadcast.Clear(); Boundary.WaitingLocal.Clear();
        Boundary.Presented.Clear();
        Boundary.Path = Path.Combine(AppContext.BaseDirectory, "readiness.map"); File.WriteAllText(Boundary.Path, "native map and versioned records");
        var lobby = new Platform_Multiplayer.MPLobby { id = new Steamworks.CSteamID(99), isHost = !client };
        lobby.members.Add(new Platform_Multiplayer.MPLobbyMember { id = new Steamworks.CSteamID(2001) });
        if (!solo) lobby.members.Add(new Platform_Multiplayer.MPLobbyMember { id = new Steamworks.CSteamID(2002) });
        Platform_Multiplayer.Instance = new Platform_Multiplayer { activeLobby = lobby };
    }
    private static void Start() => Boundary.MapHook.DynamicInvoke(Mp);
    private static void Save() => Boundary.SaveHook.DynamicInvoke(Mp, "save-name");
    private static void Tick() => APIShared.Presentation.UiWorkRuntime.Ticks["internal/21-map-record-readiness"]();
    private static SavegameLoadPacket Reply(SavegameLoadPacket packet, int operation = 1) => new SavegameLoadPacket {
        Version = packet.Version, Operation = operation, Lobby = packet.Lobby, Token = packet.Token, SaveName = packet.SaveName, Checksum = packet.Checksum,
        Fingerprint = packet.Fingerprint, Roster = packet.Roster, Records = packet.Records,
        Metadata = operation == 0 ? packet.Metadata : null
    };
    private static void Receive(SavegameLoadPacket packet, ulong sender = 2002) => Boundary.Receive(new MapRecordReadinessPacket { Value = packet }, sender);
    public static void Main()
    {
        SavegameLoadCoordinator.Initialize(null); MapRecordLoadCoordinator.Initialize(null);
        Reset(solo: true); Start();
        Check(Boundary.MapStarts == 1 && Boundary.Captures == 1 && Boundary.Sent.Count == 0, "solo MP captures own map but needs no network echo");
        Check(!MapRecordLoadCoordinator.ValidateNativeAdmission(Boundary.Path, true), "GO publication alone never grants a native ticket");
        Check(MapRecordLoadCoordinator.ValidateStart(Boundary.Path, true), "solo platform start creates a verified admission ticket");
        Boundary.MainThread = false;
        Check(MapRecordLoadCoordinator.ValidateNativeAdmission(Boundary.Path, true), "native admission is independent of Unity/Steam access");
        Boundary.MainThread = true;
        File.AppendAllText(Boundary.Path, "changed");
        Check(!MapRecordLoadCoordinator.ValidateNativeAdmission(Boundary.Path, true), "native admission rejects a file changed after platform start");
        MapRecordLoadCoordinator.FinishLoad();
        Check(!MapRecordLoadCoordinator.ValidateNativeAdmission(Boundary.Path, true) && MapRecordLoadCoordinator.ValidateNativeAdmission("unused", false), "finished ticket cannot be replayed; SP needs none");
        Reset(solo: true); Start(); Check(MapRecordLoadCoordinator.ValidateStart(Boundary.Path, true), "worker failure fixture has a native ticket");
        Boundary.MainThread = false; MapRecordLoadCoordinator.Abort("native file failure");
        Check(Boundary.Popups == 0 && !MapRecordLoadCoordinator.ValidateNativeAdmission(Boundary.Path, true), "worker failure revokes the ticket without UI or lobby processing");
        Boundary.MainThread = true; while (Boundary.Presented.Count != 0) Boundary.Presented.Dequeue()();
        Check(Boundary.Popups == 1, "worker failure retires its transaction on the presentation thread");
        Reset(solo: true); Start(); Check(MapRecordLoadCoordinator.ValidateStart(Boundary.Path, true), "worker cleanup fixture has a ticket");
        Boundary.MainThread = false; MapRecordLoadCoordinator.FinishLoad();
        Check(!MapRecordLoadCoordinator.ValidateNativeAdmission(Boundary.Path, true), "worker cleanup immediately revokes native admission");
        var oldCleanup = Boundary.Presented.Dequeue();
        Boundary.MainThread = true; Reset(solo: true); Start(); oldCleanup();
        Check(MapRecordLoadCoordinator.ValidateStart(Boundary.Path, true), "a later cleanup cannot revoke a replacement transaction");

        Reset(); Start(); var request = RequestPacket;
        Check(Boundary.MapStarts == 0 && Boundary.Captures == 1, "even all-empty MP record sets wait for every human peer");
        Receive(Reply(request), 3001); Check(Boundary.MapStarts == 0, "foreign transport sender cannot acknowledge");
        var stale = Reply(request); stale.Token = Guid.NewGuid().ToString("N"); Receive(stale);
        Check(Boundary.MapStarts == 0, "a stale token cannot acknowledge");
        Receive(Reply(request)); Receive(Reply(request));
        Check(Boundary.MapStarts == 1, "matching peers release the actual original exactly once");
        Check(MapRecordLoadCoordinator.ValidateStart(Boundary.Path, true), "host native start requires current context and same file");
        Mp.activeLobby.Aiv = "changed AIV";
        Check(!MapRecordLoadCoordinator.ValidateStart(Boundary.Path, true), "AIV selection changing after release revokes start");

        Reset(); Start(); request = RequestPacket;
        var different = Reply(request); different.Records = new[] { Present(1) }; Receive(different);
        Check(Boundary.MapStarts == 0 && Boundary.Logs.Any(text => text.Contains("declared by peer-2002")), "client-only record declaration blocks an empty host list");
        Reset(); Boundary.Records = new[] { Present(1) }; Start(); request = RequestPacket;
        different = Reply(request); different.Records = new[] { Present(2) }; Receive(different);
        Check(Boundary.MapStarts == 0 && Boundary.Popups > 0, "different stored record bytes block GO with explanation");
        Reset(); Start(); request = RequestPacket; File.AppendAllText(Boundary.Path, "changed"); Receive(Reply(request));
        Check(Boundary.MapStarts == 0, "host rehashes its own complete file immediately before GO");
        Reset(); Boundary.DuringCapture = () => File.AppendAllText(Boundary.Path, "changed while reading"); Start();
        Check(Boundary.MapStarts == 0 && Boundary.Logs.Any(text => text.Contains("changed while")), "hash brackets archive record reads");
        Reset(); Start(); request = RequestPacket; Mp.activeLobby.settings = "different native settings"; Receive(Reply(request));
        Check(Boundary.MapStarts == 0 && Boundary.Logs.Any(text => text.Contains("MapCategory.Vanilla")), "changed native setup invalidates waiting preparation");
        Reset(); Start(); Boundary.Host = 2002; Tick();
        Check(Boundary.MapStarts == 0 && Boundary.Logs.Any(text => text.Contains("owner")), "changed transport lobby owner invalidates preparation");
        Reset(); Start(); Mp.activeLobby.members[1].dummyToBeKicked = true; Tick();
        Check(Boundary.MapStarts == 0 && Boundary.Popups > 0, "pending-kick roster is never accepted");

        Reset(); Start(); request = RequestPacket;
        typeof(MapRecordLoadCoordinator).GetField("deadline", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, DateTime.UtcNow.AddSeconds(-1)); Tick();
        Check(Boundary.MapStarts == 0 && Boundary.Broadcast.Single().Contains("peer-2002") &&
            Boundary.Logs.Any(text => text.Contains("phase=timeout") && text.Contains("last observed:") && text.Contains(request.Token)),
            "timeout names missing current participants and records the last comparison");
        Reset(); Start(); request = RequestPacket; Boundary.SendOk = false; Receive(Reply(request));
        Check(Boundary.MapStarts == 0, "release send failure never publishes GO");

        Reset(); Start(); request = Reply(RequestPacket, 0); request.Token = Guid.NewGuid().ToString("N");
        Reset(client: true); Receive(request, 2002);
        Check(!Boundary.Sent.OfType<MapRecordReadinessPacket>().Any(), "client ignores a preparation from a non-host transport sender");
        Receive(request, 2001);
        Check(Boundary.Sent.OfType<MapRecordReadinessPacket>().Count(packet => packet.Value.Operation == 1) == 1, "client acknowledges its own current file and matching fingerprint");
        Check(MapRecordLoadCoordinator.ValidateStart(Boundary.Path, true), "client may see Vanilla GO before the auxiliary release packet");
        MapRecordLoadCoordinator.FinishLoad(); Receive(request, 2001);
        Check(!MapRecordLoadCoordinator.ValidateStart(Boundary.Path, true), "retired preparation cannot be replayed");

        Reset(); Start(); request = Reply(RequestPacket, 0); request.Token = Guid.NewGuid().ToString("N");
        Reset(client: true); Boundary.Records = new[] { Present(1) }; Receive(request, 2001);
        Check(!Boundary.Sent.OfType<MapRecordReadinessPacket>().Any(packet => packet.Value.Operation == 1), "client checks declaration mismatch before acknowledging");

        Reset(); Start(); Save();
        Check(Boundary.MapStarts == 0 && Boundary.Sent.OfType<MapRecordReadinessPacket>().Any(packet => packet.Value.Operation == 3) &&
            Boundary.Sent.OfType<SavegameLoadPacket>().Any(packet => packet.Operation == 0), "save selection cancels an unpublished map preparation");
        Reset(solo: true); Start(); Save(); Check(Boundary.MapStarts == 1 && Boundary.SaveStarts == 0, "published map start cannot be replaced by a save selection");
        Reset(solo: true); Save(); Start(); Check(Boundary.SaveStarts == 1 && Boundary.MapStarts == 0, "published save start cannot be replaced by a map selection");
        Reset(); Save(); Start();
        Check(Boundary.Sent.OfType<SavegameLoadPacket>().Any(packet => packet.Operation == 3) && Boundary.Sent.OfType<MapRecordReadinessPacket>().Any(packet => packet.Value.Operation == 0), "map selection cancels an unpublished save preparation");

        Reset(); Mp.activeLobby.AIPlayers = null;
        Mp.activeLobby.coopTrailID = -1;
        Mp.activeLobby.coopSelectedMission = 3;
        Mp.activeLobby.coopOrderSwapped = true;
        Mp.activeLobby.members.Add(new Platform_Multiplayer.MPLobbyMember { id = new Steamworks.CSteamID(3), SkirmishMember = true });
        Start(); request = Reply(RequestPacket, 0); request.Token = Guid.NewGuid().ToString("N");
        Reset(client: true); Mp.activeLobby.AIPlayers = "1";
        Mp.activeLobby.members.Add(new Platform_Multiplayer.MPLobbyMember { id = new Steamworks.CSteamID(3), SkirmishMember = true });
        Receive(request, 2001);
        Check(Boundary.Sent.OfType<MapRecordReadinessPacket>().Any(x => x.Value.Operation == 1),
            "actual AI occupancy and inactive trail match across host and client despite Vanilla's different caches");

        Reset(); Mp.activeLobby.coopTrailGame = true; Mp.activeLobby.coopTrailID = 1;
        Start(); request = Reply(RequestPacket, 0); request.Token = Guid.NewGuid().ToString("N");
        Reset(client: true); Mp.activeLobby.coopTrailGame = true; Mp.activeLobby.coopTrailID = 2;
        Receive(request, 2001);
        var status = Boundary.Sent.OfType<MapRecordReadinessPacket>().Single().Value;
        Check(status.Operation == MapRecordReadinessPacket.ObservationOperation &&
            Boundary.Logs.Any(x => x.Contains("changed=Coop")) && Boundary.Captures == 0,
            "an active Trail mismatch reports Coop without acknowledging or reading the file");

        Reset(); Start();
        typeof(MapRecordLoadCoordinator).GetField("waitNoticeAt", BindingFlags.Static | BindingFlags.NonPublic)
            .SetValue(null, DateTime.UtcNow.AddSeconds(-1));
        Tick(); Tick();
        Check(Boundary.WaitingBroadcast.Count == 1 && Boundary.WaitingBroadcast[0].Contains("peer-2002"),
            "a prolonged wait names the unconfirmed client once");
        var observation = Reply(RequestPacket, MapRecordReadinessPacket.ObservationOperation);
        observation.Records = Array.Empty<SavegameRecordDigest>();
        observation.Metadata = new byte[RequestPacket.Metadata.Length + 1];
        Array.Copy(RequestPacket.Metadata, 0, observation.Metadata, 1, RequestPacket.Metadata.Length);
        observation.Metadata[observation.Metadata.Length - 1] ^= 1;
        Receive(observation, 3001);
        var staleLegacy = Reply(RequestPacket, 3);
        staleLegacy.Version = MapRecordReadinessPacket.PreviousVersion;
        staleLegacy.Token = Guid.NewGuid().ToString("N");
        Receive(staleLegacy);
        Receive(new SavegameLoadPacket { Version = 99, Operation = -1 });
        Check(Boundary.Popups == 0, "stale legacy packets and unscoped version notices cannot abort a current host attempt");
        var staleObservation = Reply(RequestPacket, MapRecordReadinessPacket.ObservationOperation);
        staleObservation.Metadata = observation.Metadata; staleObservation.Token = Guid.NewGuid().ToString("N");
        Receive(staleObservation);
        Check(Boundary.WaitingBroadcast.Count == 1, "foreign and stale status cannot change chat");
        Receive(observation); Receive(observation);
        Check(Boundary.WaitingBroadcast.Count == 2 && Boundary.WaitingBroadcast.Last().Contains("MapCategory.Mods") &&
            Boundary.Logs.Any(x => x.Contains("fixture.mod") && x.Contains("HostSetting") && x.Contains(RequestPacket.Token)) &&
            Boundary.MapStarts == 0, "authenticated mod mismatch updates chat and logs host property hashes but cannot admit a start");
        Receive(Reply(RequestPacket));
        Check(Boundary.MapStarts == 1, "an actual acknowledgement resolves an observed mismatch");

        Reset(); Start(); request = Reply(RequestPacket, 0); request.Token = Guid.NewGuid().ToString("N");
        Reset(client: true); Mp.activeLobby.mapFileName = "previous.map"; Mp.activeLobby.crc = "";
        Mp.activeLobby.members.RemoveAt(0); Boundary.Settings = "not synchronized";
        Receive(request, 2001);
        Check(Boundary.Captures == 0 && Boundary.Popups == 0 &&
            Boundary.Logs.Any(x => x.Contains("changed=Map,Slots,Mods")) &&
            Boundary.Logs.Any(x => x.Contains("property=HostSetting")),
            "a proposal arriving before map, roster and mod settings logs all differences and waits");
        Mp.activeLobby.mapFileName = "readiness.map"; Mp.activeLobby.crc = "17";
        Mp.activeLobby.members.Insert(0, new Platform_Multiplayer.MPLobbyMember { id = new Steamworks.CSteamID(2001) });
        Boundary.Settings = "host-settings";
        typeof(MapRecordLoadCoordinator).GetField("fingerprintCheckedAt", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 0L);
        Tick();
        Check(Boundary.Sent.OfType<MapRecordReadinessPacket>().Last().Value.Operation == 1 && Boundary.Captures == 1,
            "delayed map, roster and host settings can converge and then acknowledge");

        Reset(); Mp.activeLobby.mapFileName = "Arena.v1"; Mp.activeLobby.crc = "0"; Start();
        request = Reply(RequestPacket, 0); request.Token = Guid.NewGuid().ToString("N");
        Reset(client: true); Mp.activeLobby.mapFileName = "Arena.v2"; Mp.activeLobby.crc = "0"; Receive(request, 2001);
        Check(Boundary.Captures == 0 && Boundary.Sent.OfType<MapRecordReadinessPacket>().Single().Value.Operation ==
            MapRecordReadinessPacket.ObservationOperation && Boundary.Logs.Any(x => x.Contains("changed=Map")),
            "distinct published map names with a dot cannot alias at CRC zero");

        Reset(); Start(); request = Reply(RequestPacket, 0); request.Token = Guid.NewGuid().ToString("N");
        Reset(client: true); Boundary.Settings = "mismatch"; Boundary.SendOk = false;
        Receive(request, 2001);
        typeof(MapRecordLoadCoordinator).GetField("waitNoticeAt", BindingFlags.Static | BindingFlags.NonPublic)
            .SetValue(null, DateTime.UtcNow.AddSeconds(-1));
        Tick();
        Check(Boundary.WaitingLocal.Single().Contains("MapCategory.Mods"), "client explains the wait locally if the status send fails");

        Reset(); Start(); request = RequestPacket;
        Mp.activeLobby.members.Add(new Platform_Multiplayer.MPLobbyMember { id = new Steamworks.CSteamID(3), SkirmishMember = true });
        Receive(Reply(request));
        Check(Boundary.MapStarts == 0 && Boundary.Broadcast.Single().Contains("MapCategory.Ai") &&
            Boundary.Logs.Any(text => text.Contains(request.Token) && text.Contains("ai=0") && text.Contains("ai=1")),
            "actual AI changes block GO and log both states under the shared attempt id");

        Reset(); Start(); request = RequestPacket; Boundary.Settings = "changed"; Receive(Reply(request));
        Check(Boundary.Broadcast.Single().Contains("MapCategory.Mods") &&
            Boundary.Logs.Any(text => text.Contains("fixture.mod.HostSetting: ") && text.Contains(" -> ") && text.Contains(request.Token)),
            "mod drift identifies the owner and property in the correlated error");
        Boundary.Settings = "host-settings"; Start(); request = RequestPacket; Boundary.Settings = "changed"; Receive(Reply(request));
        Check(Boundary.Broadcast.Count == 2 && Boundary.Logs.Count(text => text.Contains("MAP_RECORD_LOAD_BLOCKED")) == 2,
            "two equal rejected attempts each reach error logging and chat");

        Reset(); Start(); request = RequestPacket;
        var clientCancel = Reply(request, 3); clientCancel.Reason = "client rejected a record";
        Receive(clientCancel, 2001);
        Check(Boundary.Broadcast.Count == 0, "a host packet echo cannot masquerade as a client rejection");
        Receive(clientCancel); Receive(clientCancel);
        Check(Boundary.Broadcast.Single().Contains("peer-2002") && Boundary.Logs.Any(text => text.Contains(request.Token)),
            "host reports the rejecting client once in lobby chat");
        request.Token = Guid.NewGuid().ToString("N");
        Reset(client: true); Receive(request, 2001);
        var hostCancel = Reply(request, 3); hostCancel.Reason = "host rejected start";
        Mp.activeLobby.crc = "18"; Receive(hostCancel, 2001);
        Check(Boundary.Broadcast.Count == 0 && Boundary.LocalChat.Count == 0 &&
            Boundary.Logs.Any(text => text.Contains(request.Token) && text.Contains("host cancellation")),
            "authenticated host cancellation still logs after map metadata changed and does not echo chat");
        request.Token = Guid.NewGuid().ToString("N");
        Reset(client: true); Boundary.SendOk = false; Boundary.Records = new[] { Present(1) }; Receive(request, 2001);
        Check(Boundary.LocalChat.Count == 1, "client displays its rejection locally if forwarding fails");
        request.Token = Guid.NewGuid().ToString("N");
        Reset(client: true); Boundary.Records = new[] { Present(1) }; Receive(request, 2001);
        Check(Boundary.LocalChat.Count == 0 && Boundary.Broadcast.Count == 0 &&
            Boundary.Sent.OfType<MapRecordReadinessPacket>().Single().Value.Operation == 3,
            "successful client rejection forwards only to the host");

        Reset(client: true); var legacy = Reply(request, 0); legacy.Token = Guid.NewGuid().ToString("N"); legacy.Version = MapRecordReadinessPacket.PreviousVersion; legacy.Metadata = null;
        Receive(legacy, 2001);
        Check(Boundary.LocalChat.Single().Contains("protocol " + MapRecordReadinessPacket.PreviousVersion) &&
            Boundary.Sent.OfType<MapRecordReadinessPacket>().Single().Value.Version == MapRecordReadinessPacket.PreviousVersion,
            "client rejects a v2 host and returns a scoped v2 cancellation");

        Reset(solo: true); Start(); Boundary.DuringPrepare = () => Boundary.Settings = "changed at final preparation";
        Check(!MapRecordLoadCoordinator.ValidateStart(Boundary.Path, true) && Boundary.Broadcast.Single().Contains("MapCategory.Mods"),
            "final admission checks the fingerprint after applying configuration");
        Reset(solo: true); Start(); Boundary.Prepare = false;
        Check(!MapRecordLoadCoordinator.ValidateStart(Boundary.Path, true) && Boundary.Broadcast.Single().Contains("MapConfigurationWaiting"),
            "pending configuration gets its own start-blocking reason");

        Reset(solo: true); Boundary.SaveVeto = true; Save();
        Check(Boundary.SaveStarts == 0 && Boundary.Broadcast.Single().Contains("fixture save veto") &&
            Boundary.Logs.Any(text => text.Contains("SAVE_SETTINGS_LOAD_BLOCKED")),
            "prepared save veto logs and announces through the same lobby status boundary");

        Reset(solo: true); Save(); Boundary.SaveVeto = true;
        for (int retry = 0; retry < 2; retry++)
        {
            bool retryRejected = false;
            try { SavegameLoadCoordinator.ValidateStart(Boundary.Path, true); }
            catch (InvalidOperationException) { retryRejected = true; }
            Check(retryRejected, "an automatic save retry still fails closed");
        }
        Check(Boundary.Broadcast.Count == 1 && Boundary.Logs.Count(text => text.Contains("SAVE_SETTINGS_LOAD_BLOCKED")) == 1,
            "automatic retries of a rejected save admission produce one announcement");

        Reset(); Start(); request = RequestPacket; Receive(Reply(request));
        var releasedCancel = Reply(request, 3); releasedCancel.Reason = "peer rejection after GO";
        Receive(releasedCancel);
        Check(!MapRecordLoadCoordinator.ValidateStart(Boundary.Path, true) && Boundary.Broadcast.Count == 1,
            "a GO already published for a cancelled map does not log or announce a second rejection");

        Reset(solo: true); Save();
        Boundary.Settings = "changed after save publication";
        typeof(SavegameLoadCoordinator).GetField("fingerprintCheckedAt", BindingFlags.Static | BindingFlags.NonPublic)
            .SetValue(null, 0L);
        APIShared.Presentation.UiWorkRuntime.Ticks["internal/20-save-load"]();
        bool rejectedPublishedSave = false;
        try { SavegameLoadCoordinator.ValidateStart(Boundary.Path, true); }
        catch (InvalidOperationException) { rejectedPublishedSave = true; }
        Check(rejectedPublishedSave && Boundary.Broadcast.Count == 1,
            "a save start value already published for a cancelled attempt does not announce a second rejection");

        Reset(); Start();
        var envelope = new MapRecordReadinessPacket { Value = RequestPacket };
        var wire = MessagePackSerializer.Serialize(envelope);
        Check(wire.SequenceEqual(MessagePackSerializer.Serialize(envelope.Value)), "separate packet identity reuses the common readiness wire layout");
        var roundTrip = MessagePackSerializer.Deserialize<MapRecordReadinessPacket>(wire);
        Check(roundTrip.Value.Token == envelope.Value.Token && roundTrip.Value.Fingerprint == envelope.Value.Fingerprint, "actual explicit formatter round trip");
        Reset(solo:true);
        APIShared.Economy.WorkerProductionAgreement.SetPreparedSavePath("previous-save.sav");
        MapRecordLoadCoordinator.PrepareSelectedProductionData(null,true);
        Check(APIShared.Economy.WorkerProductionAgreement.PreparedDataPath==Path.GetFullPath(Boundary.Path),
            "The actual selected map replaces a previous save before Ready/Play");
        var selectedView=new CrusaderDE.FRONT_Multiplayer{currentLobby=Mp.activeLobby};
        selectedView.Select(new FileHeader{fileName="local-selected",filePath=Boundary.Path,crc=456});
        Mp.activeLobby.crc=null;Mp.activeLobby.mapFileName="stale-network-name";
        Check(MapRecordLoadCoordinator.PrepareSelectedProductionData(selectedView,false) &&
            APIShared.Economy.WorkerProductionAgreement.PreparedDataPath==Path.GetFullPath(Boundary.Path),
            "Solo Play and solo Coop use the exact receiver selection even though their dummy lobby never publishes CRC");
        Check(!MapRecordLoadCoordinator.PrepareSelectedProductionData(selectedView,true) &&
            APIShared.Economy.WorkerProductionAgreement.PreparedDataPath==null,
            "Early network Ready without CRC waits and never substitutes the solo receiver header");
        Mp.activeLobby.mapFileName="readiness.map";Mp.activeLobby.crc="17";
        Check(MapRecordLoadCoordinator.PrepareSelectedProductionData(selectedView,true),
            "A subsequently published network name/CRC resumes canonical selection preparation");
        Boundary.LookupHeader=new FileHeader{fileName="readiness.map",filePath=Boundary.Path,crc=18};
        bool rejected=false;try{MapRecordLoadCoordinator.PrepareSelectedProductionData(selectedView,true);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"A valid network CRC is never relaxed to a different cached header or solo selection");
        Boundary.LookupHeader=null;
        selectedView.Select(new FileHeader{fileName="",filePath=Boundary.Path});
        rejected=false;try{MapRecordLoadCoordinator.PrepareSelectedProductionData(selectedView,false);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"A local start needs a valid current selected header rather than old lobby data");
        APIShared.Economy.WorkerProductionAgreement.Waiting=true;Start();
        Check(Boundary.MapStarts==0,"Pending map production requirements delay GO even after all map-record peers match");
        APIShared.Economy.WorkerProductionAgreement.Waiting=false;Tick();
        Check(Boundary.MapStarts==1,"The existing map tick releases GO after production requirements agree");
        APIShared.Economy.WorkerProductionAgreement.Blocked=true;
        int mapProductionChecks=APIShared.Economy.WorkerProductionAgreement.Checks;
        Check(!MapRecordLoadCoordinator.ValidateStart(Boundary.Path,true) &&
            APIShared.Economy.WorkerProductionAgreement.Checks>mapProductionChecks &&
            !MapRecordLoadCoordinator.ValidateNativeAdmission(Boundary.Path,true),
            "Final map production failure prevents creating a native admission ticket");
        Reset(solo:true);APIShared.Economy.WorkerProductionAgreement.Blocked=true;Start();
        Check(Boundary.MapStarts==0 && Boundary.Popups>0,"Invalid map production records visibly block GO");

        Reset();Start();request=Reply(RequestPacket,0);request.Token=Guid.NewGuid().ToString("N");
        Reset(client:true);APIShared.Economy.WorkerProductionAgreement.Waiting=true;Receive(request,2001);
        Check(!Boundary.Sent.OfType<MapRecordReadinessPacket>().Any(packet=>packet.Value.Operation==1),
            "A client cannot ACK matching map bytes while production requirements are pending");
        APIShared.Economy.WorkerProductionAgreement.Waiting=false;Tick();
        Check(Boundary.Sent.OfType<MapRecordReadinessPacket>().Count(packet=>packet.Value.Operation==1)==1,
            "The existing client map tick acknowledges once production requirements match");

        Reset(solo:true);APIShared.Economy.WorkerProductionAgreement.Waiting=true;Save();
        Check(Boundary.SaveStarts==0,"Pending production agreement waits without releasing Vanilla or cancelling preparation");
        APIShared.Economy.WorkerProductionAgreement.Waiting=false;
        APIShared.Presentation.UiWorkRuntime.Ticks["internal/20-save-load"]();
        Check(Boundary.SaveStarts==1,"The existing save tick releases exactly once after production agreement completes");
        Reset(solo:true);APIShared.Economy.WorkerProductionAgreement.Blocked=true;Save();
        Check(Boundary.SaveStarts==0 && Boundary.Popups>0,"Blocking production requirements visibly reject before Vanilla save loading");
        Reset(solo:true);Boundary.Path=Path.Combine(AppContext.BaseDirectory,"save-name.sav");File.WriteAllText(Boundary.Path,"valid saved world");
        Save();APIShared.Economy.WorkerProductionAgreement.Blocked=true;
        int admittedProductionChecks=APIShared.Economy.WorkerProductionAgreement.Checks;
        bool productionRejected=false;
        try{SavegameLoadCoordinator.ValidateStart(Boundary.Path,true);}catch(InvalidOperationException){productionRejected=true;}
        Check(productionRejected && APIShared.Economy.WorkerProductionAgreement.Checks>admittedProductionChecks &&
            !SavegameLoadCoordinator.IsLoadingMultiplayerSave,"Final production rejection revokes admission before native loading");
        MapRecordLoadCoordinator.FinishLoad(); SavegameLoadCoordinator.FinishLoad();
        Console.WriteLine("PASS: " + checks + " production map-readiness checks: all-peer records, authority, immutable native tickets, file changes, selection changes and mutual save/map exclusion.");
    }
}
