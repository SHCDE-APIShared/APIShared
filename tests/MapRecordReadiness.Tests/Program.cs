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
        Boundary.MainThread = Boundary.Prepare = Boundary.SendOk = true; Boundary.Local = client ? 2002UL : 2001UL; Boundary.Host = 2001;
        Boundary.MapStarts = Boundary.SaveStarts = Boundary.Captures = Boundary.Popups = 0;
        Boundary.Settings = "host-settings"; Boundary.Records = Array.Empty<SavegameRecordDigest>(); Boundary.DuringCapture = null;
        Boundary.Logs.Clear(); Boundary.Sent.Clear();
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
        Operation = operation, Lobby = packet.Lobby, Token = packet.Token, SaveName = packet.SaveName, Checksum = packet.Checksum,
        Fingerprint = packet.Fingerprint, Roster = packet.Roster, Records = packet.Records
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
        Check(Boundary.MapStarts == 0 && Boundary.Logs.Any(text => text.Contains("settings changed")), "changed native setup invalidates waiting preparation");
        Reset(); Start(); Boundary.Host = 2002; Tick();
        Check(Boundary.MapStarts == 0 && Boundary.Logs.Any(text => text.Contains("owner")), "changed transport lobby owner invalidates preparation");
        Reset(); Start(); Mp.activeLobby.members[1].dummyToBeKicked = true; Tick();
        Check(Boundary.MapStarts == 0 && Boundary.Popups > 0, "pending-kick roster is never accepted");

        Reset(); Start(); request = RequestPacket;
        typeof(MapRecordLoadCoordinator).GetField("deadline", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, DateTime.UtcNow.AddSeconds(-1)); Tick();
        Check(Boundary.MapStarts == 0 && Boundary.Logs.Any(text => text.Contains("peer-2002")), "timeout names missing current participants");
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

        var envelope = new MapRecordReadinessPacket { Value = RequestPacket };
        var wire = MessagePackSerializer.Serialize(envelope);
        Check(wire.SequenceEqual(MessagePackSerializer.Serialize(envelope.Value)), "separate packet identity reuses the exact existing readiness wire body");
        var roundTrip = MessagePackSerializer.Deserialize<MapRecordReadinessPacket>(wire);
        Check(roundTrip.Value.Token == envelope.Value.Token && roundTrip.Value.Fingerprint == envelope.Value.Fingerprint, "actual explicit formatter round trip");
        MapRecordLoadCoordinator.FinishLoad(); SavegameLoadCoordinator.FinishLoad();
        Console.WriteLine("PASS: " + checks + " production map-readiness checks: all-peer records, authority, immutable native tickets, file changes, selection changes and mutual save/map exclusion.");
    }
}
