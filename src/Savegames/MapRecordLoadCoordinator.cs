using APIShared.Networking;
using APIShared.Savegames;
using BepInEx.Logging;
using CrusaderDE;
using MessagePack;
using MessagePack.Formatters;
using MonoMod.RuntimeDetour;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace APIShared
{
    // A distinct Extender packet identity is necessary: one packet type can have only
    // one message channel. Its wire body is exactly the existing readiness protocol.
    [MessagePackFormatter(typeof(MapRecordReadinessPacketFormatter))]
    internal sealed class MapRecordReadinessPacket
    {
        internal SavegameLoadPacket Value;
    }
    internal sealed class MapRecordReadinessPacketFormatter : IMessagePackFormatter<MapRecordReadinessPacket>
    {
        private readonly SavegameLoadPacketFormatter formatter = new SavegameLoadPacketFormatter();
        public MapRecordReadinessPacketFormatter() { }
        public void Serialize(ref MessagePackWriter writer, MapRecordReadinessPacket value, MessagePackSerializerOptions options)
            => formatter.Serialize(ref writer, value?.Value, options);
        public MapRecordReadinessPacket Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        { var value = formatter.Deserialize(ref reader, options); return value == null ? null : new MapRecordReadinessPacket { Value = value }; }
    }

    // HostStartGame only publishes GO! in the audited managed assembly. This single
    // permanent gate delays that publication, not native post-load success. All mutable
    // transaction work runs on Unity: RegisterMessage supplies copied FIFO delivery.
    internal static class MapRecordLoadCoordinator
    {
        private delegate void HostStart(Platform_Multiplayer self);
        private const int PreparationSeconds = 60, StartWindowSeconds = 30;
        private static Hook hook;
        private static HostStart original;
        private static MessageChannel<MapRecordReadinessPacket> channel;
        private static Presentation.IUiFrameWorkRegistration frameWork;
        private static ManualLogSource log;
        private static bool attempted, initialized, acknowledged, released, loading;
        private static Platform_Multiplayer owner;
        private static SavegameLoadPacket pending;
        private static SavegameLoadAgreement agreement;
        private static IReadOnlyList<SavegameRecordDigest> localRecords;
        private static string localPath, localHash;
        private static ulong hostId;
        private static DateTime deadline;
        private static long fingerprintCheckedAt;
        private static bool fingerprintMatched;
        private static readonly HashSet<string> retired = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<string> retiredOrder = new Queue<string>();
        private static NativeAdmissionTicket admission;
        private sealed class NativeAdmissionTicket
        {
            internal readonly string Path, Hash;
            internal NativeAdmissionTicket(string path, string hash) { Path = path; Hash = hash; }
        }

        internal static void Initialize(ManualLogSource logger)
        {
            if (attempted) return;
            attempted = true; log = logger;
            if (!Internal.UnityMainThreadDispatch.IsMainThread)
                throw new InvalidOperationException("Map readiness initialization requires Unity's main thread.");
            channel = ApiShared.ForMod(APISharedPlugin.PluginGuid).Network.RegisterMessage<MapRecordReadinessPacket>(
                "MapRecordReadiness", Receive, new MessageChannelOptions<MapRecordReadinessPacket> { MaximumBodyBytes = 200000 });
            if (!Presentation.UiWorkRuntime.TryRegister(APISharedPlugin.PluginGuid, "internal/21-map-record-readiness",
                Tick, 0, out frameWork, out string reason)) throw new InvalidOperationException(reason);
            Hook candidate = null;
            try
            {
                var method = typeof(Platform_Multiplayer).GetMethod(nameof(Platform_Multiplayer.HostStartGame), Type.EmptyTypes);
                if (method == null || !method.IsPublic || method.IsStatic || method.ReturnType != typeof(void))
                    throw new MissingMethodException("Expected public instance void Platform_Multiplayer.HostStartGame().");
                candidate = new Hook(method, (HostStart)Request, new HookConfig { ManualApply = true });
                original = candidate.GenerateTrampoline<HostStart>();
                if (original == null) throw new InvalidOperationException("Map readiness original trampoline is unavailable.");
                // Retain before publication: a partially failing Apply must never be unpatched.
                hook = candidate; candidate.Apply(); initialized = true;
                NativeApiLog.Debug(log, "MAP_RECORD_PREPARATION_READY: permanent pre-GO gate installed.");
            }
            catch { if (hook == null) candidate?.Dispose(); throw; }
        }

        private static ulong[] Humans(Platform_Multiplayer.MPLobby lobby)
        {
            if (lobby?.members == null || lobby.members.Any(member => member == null || member.dummyToBeKicked))
                throw new InvalidOperationException("Lobby roster is unavailable or changing.");
            var humans = lobby.members.Where(member => !member.SkirmishMember && member.id.m_SteamID > 1000)
                .Select(member => member.id.m_SteamID).OrderBy(id => id).ToArray();
            if (humans.Length == 0 || humans.Length != humans.Distinct().Count())
                throw new InvalidOperationException("Invalid human lobby roster.");
            return humans;
        }
        private static string Roster(Platform_Multiplayer.MPLobby lobby) => string.Join(",", Humans(lobby));
        private static int Checksum(Platform_Multiplayer.MPLobby lobby)
        {
            if (!int.TryParse(lobby.crc, NumberStyles.Integer, CultureInfo.InvariantCulture, out int crc))
                throw new InvalidDataException("Selected map checksum is unavailable.");
            return crc;
        }
        private static FileHeader Header(Platform_Multiplayer.MPLobby lobby)
        {
            var header = MapFileManager.Instance.GetHeaderFromFileNameMP(lobby.mapFileName, Checksum(lobby));
            if (header == null || !SameMap(header.fileName, lobby.mapFileName) ||
                unchecked((int)header.crc) != Checksum(lobby) || string.IsNullOrEmpty(header.filePath))
                throw new InvalidDataException("Local map does not match the current lobby selection.");
            return header;
        }
        private static bool SameMap(string a, string b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(Path.GetFileNameWithoutExtension(a), Path.GetFileNameWithoutExtension(b), StringComparison.OrdinalIgnoreCase);
        private static string Hash(string path)
        {
            using (var input = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
        }
        private static IReadOnlyList<SavegameRecordDigest> Capture(string path, out string hash)
        {
            hash = Hash(path);
            var records = VersionedSaveDataRuntime.CaptureMapSimulationDigests(path);
            if (Hash(path) != hash) throw new InvalidDataException("Local map changed while its simulation records were read.");
            if (!SavegameRecordComparison.IsValid(records) || records.Any(record => record.State == SavegameRecordState.Unreadable))
                throw new InvalidDataException("Local map has unreadable or invalid declared simulation records.");
            return records;
        }
        private static string Fingerprint(Platform_Multiplayer.MPLobby lobby)
        {
            using (var memory = new MemoryStream()) using (var writer = new BinaryWriter(memory))
            {
                writer.Write(lobby.id.m_SteamID); writer.Write(lobby.mapFileName ?? ""); writer.Write(Checksum(lobby));
                writer.Write(lobby.settings ?? ""); writer.Write(lobby.AIVDataChecksum() ?? "");
                writer.Write(lobby.maxPlayers ?? ""); writer.Write(lobby.AIPlayers ?? ""); writer.Write(lobby.gameTypeCoop ?? "");
                writer.Write(lobby.coopTrailGame); writer.Write(lobby.coopTrailID); writer.Write(lobby.coopSelectedMission); writer.Write(lobby.coopOrderSwapped);
                var humans = Humans(lobby); writer.Write(humans.Length);
                foreach (ulong human in humans) { writer.Write(human); writer.Write(lobby.getThisPlayerFromSteamID(human)); }
                writer.Write(SavegameModSettings.CaptureHostFingerprint()); writer.Flush();
                using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(memory.ToArray())).Replace("-", "");
            }
        }
        private static string Name(ulong id)
        {
            try { string value = SteamFriends.GetFriendPersonaName(new CSteamID(id)); if (!string.IsNullOrWhiteSpace(value)) return value; } catch { }
            return id.ToString(CultureInfo.InvariantCulture);
        }
        private static void Request(Platform_Multiplayer self)
        {
            if (!Internal.UnityMainThreadDispatch.IsMainThread)
            { NativeApiLog.Error(log, "Map start publication requires Unity's main thread."); return; }
            if (self.activeLobby?.isHost != true) { original(self); return; }
            if (!SavegameLoadCoordinator.CancelSelection())
            { ShowBlocked("A confirmed multiplayer save is already starting."); return; }
            if (pending != null)
            {
                if (released || loading) return;
                try { if (Fingerprint(self.activeLobby) == pending.Fingerprint) return; } catch { }
                Abort("Map selection or lobby settings changed.", true, false);
            }
            try
            {
                owner = self;
                var lobby = self.activeLobby; var humans = Humans(lobby); ulong local = SteamUser.GetSteamID().m_SteamID;
                hostId = SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID;
                if (hostId != local || !humans.Contains(local)) throw new InvalidOperationException("The local host is not the current lobby owner and member.");
                localPath = Path.GetFullPath(Header(lobby).filePath);
                localRecords = Capture(localPath, out localHash);
                string fingerprint = Fingerprint(lobby);
                pending = new SavegameLoadPacket { Operation = 0, Lobby = lobby.id.m_SteamID, Token = Guid.NewGuid().ToString("N"),
                    SaveName = lobby.mapFileName, Checksum = Checksum(lobby), Fingerprint = fingerprint, Roster = Roster(lobby), Records = localRecords };
                agreement = new SavegameLoadAgreement(pending.Token, fingerprint, humans.Where(id => id != local), localRecords);
                acknowledged = released = loading = false; fingerprintCheckedAt = 0;
                deadline = DateTime.UtcNow.AddSeconds(PreparationSeconds);
                if (!agreement.Ready) Send(pending);
                Tick();
            }
            catch (Exception error) { Abort(error.Message); }
            finally { Schedule(); }
        }
        private static void Send(SavegameLoadPacket packet)
        {
            var result = channel.SendToLobby(new MapRecordReadinessPacket { Value = packet });
            if (!result.Submitted) throw new InvalidOperationException("Map readiness send failed: " + result.Reason);
        }
        private static SavegameLoadPacket Reply(int operation, string reason = null) => new SavegameLoadPacket {
            Operation = operation, Lobby = pending.Lobby, Token = pending.Token, SaveName = pending.SaveName,
            Checksum = pending.Checksum, Fingerprint = pending.Fingerprint, Roster = pending.Roster,
            Records = operation == 1 ? localRecords : Array.Empty<SavegameRecordDigest>(),
            Reason = reason == null || reason.Length <= SavegameLoadPacket.MaximumReasonLength ? reason : reason.Substring(0, SavegameLoadPacket.MaximumReasonLength)
        };
        private static void Receive(NetworkMessage<MapRecordReadinessPacket> message)
        {
            try { ReceiveCore(message); } catch (Exception error) { Abort(error.Message); }
        }
        private static void ReceiveCore(NetworkMessage<MapRecordReadinessPacket> message)
        {
            var packet = message.Packet?.Value; var mp = Platform_Multiplayer.Instance; var lobby = mp?.activeLobby;
            if (packet == null || lobby == null || !Humans(lobby).Contains(message.SenderSteamId)) return;
            ulong host = SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID;
            if (packet.Version != SavegameLoadPacket.CurrentVersion)
            { if (lobby.isHost || message.SenderSteamId == host) Abort(Name(message.SenderSteamId) + " uses a different map readiness protocol. Every player needs the same APIShared version."); return; }
            if (packet.Lobby != lobby.id.m_SteamID || packet.Roster != Roster(lobby) || packet.Operation < 0 || packet.Operation > 3 ||
                packet.Token?.Length != 32 || !Guid.TryParseExact(packet.Token, "N", out _) || retired.Contains(packet.Token) ||
                packet.SaveName?.Length > 1024 || !SameMap(packet.SaveName, lobby.mapFileName) || packet.Checksum != Checksum(lobby) ||
                packet.Time != 0 || packet.Metadata != null || packet.Fingerprint?.Length != 64) return;
            if (lobby.isHost)
            {
                if (pending == null || packet.Token != pending.Token || packet.Fingerprint != pending.Fingerprint) return;
                if (packet.Operation == 1 && !agreement.Accept(message.SenderSteamId, packet.Token, packet.Fingerprint,
                    packet.Records, Name(message.SenderSteamId), out string mismatch) && mismatch != null) { Abort(mismatch); return; }
                if (packet.Operation == 3) { Abort("A participant cancelled map preparation: " + packet.Reason); return; }
                Tick(); return;
            }
            if (message.SenderSteamId != host) return;
            if (packet.Operation == 0)
            {
                if (pending?.Token == packet.Token) { Tick(); return; }
                if (pending != null) Abort("Host replaced map preparation.", false, false);
                if (!SavegameRecordComparison.IsValid(packet.Records)) throw new InvalidDataException("Invalid host map record list.");
                owner = mp; hostId = host; pending = packet; agreement = null;
                acknowledged = released = loading = false; fingerprintCheckedAt = 0;
                localPath = Path.GetFullPath(Header(lobby).filePath); localRecords = Capture(localPath, out localHash);
                string mismatch = SavegameRecordComparison.Describe(packet.Records, localRecords, Name(SteamUser.GetSteamID().m_SteamID));
                if (mismatch != null) throw new InvalidDataException(mismatch);
                deadline = DateTime.UtcNow.AddSeconds(PreparationSeconds); Tick();
            }
            else if (pending?.Token == packet.Token && packet.Fingerprint == pending.Fingerprint)
            {
                if (packet.Operation == 3) Abort("Host cancelled map preparation: " + packet.Reason, false);
                else if (packet.Operation == 2)
                { if (!released) deadline = DateTime.UtcNow.AddSeconds(StartWindowSeconds); released = true; }
            }
        }
        private static bool FingerprintMatches(bool decisive)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (decisive || fingerprintCheckedAt == 0 || now - fingerprintCheckedAt >= System.Diagnostics.Stopwatch.Frequency / 2)
            { fingerprintMatched = Fingerprint(owner.activeLobby) == pending.Fingerprint; fingerprintCheckedAt = now; }
            return fingerprintMatched;
        }
        private static void CheckContext()
        {
            var lobby = owner?.activeLobby;
            if (lobby == null || lobby.id.m_SteamID != pending.Lobby || Roster(lobby) != pending.Roster ||
                SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID != hostId)
                throw new InvalidOperationException("Lobby owner or human roster changed during map preparation.");
            var header = Header(lobby);
            if (!SameMap(pending.SaveName, lobby.mapFileName) || pending.Checksum != Checksum(lobby) ||
                !string.Equals(Path.GetFullPath(header.filePath), localPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Local map selection changed.");
        }
        private static void CheckFile()
        { if (Hash(localPath) != localHash) throw new InvalidDataException("Local map changed after its simulation records were confirmed."); }
        private static void Tick()
        {
            if (pending == null || loading) return;
            try
            {
                CheckContext();
                if (DateTime.UtcNow > deadline) throw new TimeoutException(TimeoutReason());
                if (owner.activeLobby.isHost)
                {
                    if (!FingerprintMatches(false)) throw new InvalidOperationException("Prepared map selection or host settings changed.");
                    if (released || !agreement.Ready || !ModSettings.ModSettingsApplication.PrepareLaunch()) return;
                    if (!FingerprintMatches(true)) throw new InvalidOperationException("Prepared map settings changed before GO.");
                    CheckFile(); released = true; deadline = DateTime.UtcNow.AddSeconds(StartWindowSeconds);
                    if (Humans(owner.activeLobby).Length > 1) Send(Reply(2));
                    NativeApiLog.Debug(log, "MAP_RECORDS_CONFIRMED: all human peers ready; publishing Vanilla GO once.");
                    original(owner);
                }
                else if (acknowledged)
                { if (!FingerprintMatches(false)) throw new InvalidOperationException("Confirmed client map settings changed."); }
                else if (FingerprintMatches(false) && ModSettings.ModSettingsApplication.PrepareLaunch() && FingerprintMatches(true))
                {
                    CheckFile();
                    var sent = channel.SendToSteamPeer(new MapRecordReadinessPacket { Value = Reply(1) }, hostId);
                    if (!sent.Submitted) throw new InvalidOperationException(sent.Reason);
                    acknowledged = true;
                }
            }
            catch (Exception error) { Abort(error.Message); }
            finally { Schedule(); }
        }
        private static void Schedule() => frameWork?.SetContinuous(pending != null && !loading);
        internal static bool ValidateStart(string path, bool multiplayer)
        {
            if (!multiplayer) return true;
            if (!Internal.UnityMainThreadDispatch.IsMainThread)
            { NativeApiLog.Error(log, "Map readiness validation requires Unity's main thread."); return false; }
            try
            {
                if (!initialized || pending == null || (owner.activeLobby.isHost ? !released : !acknowledged))
                    throw new InvalidOperationException("Multiplayer map has no confirmed record preparation.");
                CheckContext();
                if (!string.Equals(Path.GetFullPath(path), localPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Native map load differs from the confirmed local file.");
                CheckFile();
                if (!FingerprintMatches(true) || !ModSettings.ModSettingsApplication.PrepareLaunch())
                    throw new InvalidOperationException("Multiplayer map settings changed before native load.");
                System.Threading.Volatile.Write(ref admission, new NativeAdmissionTicket(localPath, localHash));
                loading = true; Schedule(); return true;
            }
            catch (Exception error) { Abort(error.Message); return false; }
        }
        // Native load may run on another thread. This path reads only an immutable
        // main-thread ticket and this peer's file; no Steam, lobby, settings or UI.
        internal static bool ValidateNativeAdmission(string path, bool multiplayer)
        {
            if (!multiplayer) return true;
            var ticket = System.Threading.Volatile.Read(ref admission);
            if (ticket == null) return false;
            try
            {
                return string.Equals(Path.GetFullPath(path), ticket.Path, StringComparison.OrdinalIgnoreCase) &&
                    Hash(ticket.Path) == ticket.Hash && ReferenceEquals(ticket, System.Threading.Volatile.Read(ref admission));
            }
            catch { return false; }
        }
        internal static bool CancelSelection()
        {
            if (pending == null) return true;
            if (released || loading) return false;
            Abort("Map selection cancelled.", true, false); return true;
        }
        private static string TimeoutReason() => released ? "Confirmed multiplayer map did not start within 30 seconds." :
            "Map record preparation timed out. Waiting for: " + (agreement == null ? "the lobby host" : string.Join(", ", agreement.Unconfirmed.Select(Name))) +
            ". Every player needs the same APIShared/mod versions and the selected map.";
        private static void Retire(string token)
        { if (token == null || !retired.Add(token)) return; retiredOrder.Enqueue(token); if (retiredOrder.Count > 64) retired.Remove(retiredOrder.Dequeue()); }
        internal static void Abort(string reason)
        {
            if (Internal.UnityMainThreadDispatch.IsMainThread) { Abort(reason, true, true); return; }
            // Revoke native admission immediately, then retire only this exact
            // transaction on Unity; delayed failure reporting must not cancel a new map.
            var transaction = System.Threading.Volatile.Read(ref pending);
            var ticket = System.Threading.Volatile.Read(ref admission);
            System.Threading.Interlocked.CompareExchange(ref admission, null, ticket);
            if (!Internal.UnityMainThreadDispatch.TryEnqueue(() => {
                if (ReferenceEquals(pending, transaction)) Abort(reason, true, true);
            })) NativeApiLog.Error(log, "MAP_RECORD_ABORT_DEFERRED_UNAVAILABLE: " + reason);
        }
        private static void Abort(string reason, bool notify, bool showError = true)
        {
            if (pending != null)
            {
                if (notify) try
                {
                    if (owner?.activeLobby?.isHost == true) Send(Reply(3, reason));
                    else channel.SendToSteamPeer(new MapRecordReadinessPacket { Value = Reply(3, reason) }, hostId);
                }
                catch (Exception error) { NativeApiLog.Error(log, "Map preparation cancellation send failed: " + error.Message); }
                Retire(pending.Token);
            }
            Clear();
            if (!showError) { NativeApiLog.Debug(log, "MAP_RECORD_PREPARATION_CANCELLED: " + reason); return; }
            ShowBlocked(reason);
        }
        private static void ShowBlocked(string reason)
        {
            NativeApiLog.Error(log, "MAP_RECORD_LOAD_BLOCKED: " + reason);
            Action show = () => {
                try { HUD_ConfirmationPopup.ShowConfirmationOKMessage(Localization.UiText.Get("Savegames.LoadTitle"), () => { }, reason); }
                catch (Exception error) { NativeApiLog.Error(log, "Map preparation failure popup unavailable: " + error.Message); }
            };
            if (Internal.UnityMainThreadDispatch.IsMainThread) show(); else Internal.UnityMainThreadDispatch.TryEnqueue(show);
        }
        private static void Clear()
        {
            System.Threading.Volatile.Write(ref admission, null);
            pending = null; agreement = null; owner = null; localRecords = null; localPath = localHash = null;
            acknowledged = released = loading = false; fingerprintCheckedAt = 0;
            frameWork?.CancelPending(); frameWork?.SetContinuous(false);
        }
        internal static void FinishLoad()
        {
            if (Internal.UnityMainThreadDispatch.IsMainThread)
            { if (pending != null) Retire(pending.Token); Clear(); return; }
            var transaction = System.Threading.Volatile.Read(ref pending);
            var ticket = System.Threading.Volatile.Read(ref admission);
            System.Threading.Interlocked.CompareExchange(ref admission, null, ticket);
            if (!Internal.UnityMainThreadDispatch.TryEnqueue(() => {
                if (ReferenceEquals(pending, transaction)) FinishLoad();
            })) NativeApiLog.Error(log, "MAP_RECORD_FINISH_DEFERRED_UNAVAILABLE");
        }
    }
}
