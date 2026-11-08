using APIShared.Lobby;
using APIShared.Localization;
using APIShared.Networking;
using APIShared.Savegames;
using BepInEx.Logging;
using CrusaderDE;
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
    // HostStartGame only publishes GO! in the audited managed assembly. This single
    // permanent gate delays that publication, not native post-load success. All mutable
    // transaction work runs on Unity: RegisterMessage supplies copied FIFO delivery.
    internal static partial class MapRecordLoadCoordinator
    {
        private delegate void HostStart(Platform_Multiplayer self);
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
        private static LobbySnapshot preparedSnapshot, lastSnapshot;
        private static string attemptId;
        private static ulong attemptLobby;
        private static bool attemptHost;
        private static string configurationWaiting, announcedWaiting;
        private static DateTime waitNoticeAt, nextObservationRetry;
        private static byte[] lastSentObservation;
        private static LobbySnapshot lastSentSnapshot;
        private static string lastLoggedModFingerprint;
        private static bool waitingReasonDirty;
        private static bool observationSendFailed;
        private static readonly Dictionary<ulong, byte[]> PeerObservations = new Dictionary<ulong, byte[]>();
        private static readonly string[] Categories = { "Map", "Vanilla", "Ai", "Coop", "Slots", "Mods" };
        private const int DigestLength = 32;
        private const byte DiffObservation = 0, ConfigurationObservation = 1, ProductionObservation = 2;
        private static readonly BoundedTokenSet retired = new BoundedTokenSet();
        private static NativeAdmissionTicket admission;
        private static Platform_Multiplayer.MPLobby rejectedNativeLobby;
        private static string rejectedNativePath;
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
                "MapRecordReadiness", Receive, new MessageChannelOptions<MapRecordReadinessPacket> { MaximumBodyBytes = LobbyReadinessSupport.MaximumBodyBytes });
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
            var humans = LobbyReadinessSupport.ObservedHumans(lobby);
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
                throw new InvalidDataException(UiText.Get("LobbyLaunch.MapFileUnavailable", lobby.mapFileName));
            return header;
        }
        // Local Play/solo Coop load the receiver's selected header directly; their
        // dummy lobby has no authenticated CRC. Network admission still requires the
        // canonical name/CRC lookup used by the actual map coordinator. Early Ready
        // may precede CRC publication, which is pending rather than a legacy selection.
        internal static bool PrepareSelectedProductionData(FRONT_Multiplayer receiver,bool requiresPeerAgreement)
        {
            FileHeader header;
            if (!requiresPeerAgreement) header = LobbySelectedMapHeader.Read(receiver);
            else
            {
                var lobby = Platform_Multiplayer.Instance?.activeLobby;
                if (!ReferenceEquals(receiver,null) && !ReferenceEquals(receiver.currentLobby,lobby))
                    throw new InvalidDataException("The lobby receiver no longer owns the current map selection.");
                if (lobby == null || string.IsNullOrWhiteSpace(lobby.mapFileName) ||
                    !int.TryParse(lobby.crc,NumberStyles.Integer,CultureInfo.InvariantCulture,out _))
                {
                    Economy.WorkerProductionAgreement.SetPreparedDataPath(null);
                    return false;
                }
                header = Header(lobby);
            }
            if (header == null || string.IsNullOrWhiteSpace(header.fileName) || string.IsNullOrWhiteSpace(header.filePath))
                throw new InvalidDataException("The selected local map header is unavailable.");
            string path = Path.GetFullPath(header.filePath);
            if (!File.Exists(path)) throw new FileNotFoundException("The selected local map is unavailable.",path);
            Economy.WorkerProductionAgreement.SetPreparedDataPath(path);
            return true;
        }
        private static bool SameMap(string a, string b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static string Hash(string path)
        {
            using (var input = File.OpenRead(path))
                return Sha256Hex.Of(input);
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

        private static string CategoryLabels(IEnumerable<string> categories) =>
            string.Join(", ", categories.Select(category => UiText.Get("LobbyLaunch.MapCategory." + category)));

        private static string FingerprintMismatch()
        {
            if (preparedSnapshot == null || lastSnapshot == null) return UiText.Get("LobbyLaunch.MapFingerprintMismatch");
            var changes = preparedSnapshot.DescribeChanges(lastSnapshot);
            return changes.Count == 0 ? UiText.Get("LobbyLaunch.MapFingerprintMismatch") :
                UiText.Get("LobbyLaunch.MapChanged", CategoryLabels(changes));
        }

        private static void Request(Platform_Multiplayer self)
        {
            if (!Internal.UnityMainThreadDispatch.IsMainThread)
            {
                LobbyLaunchStatus.ReportPreparationBlocked(log, "Map start publication requires Unity's main thread.",
                    "MAP_RECORD_LOAD_BLOCKED role=unknown phase=request attempt=" + LobbyReadinessSupport.NewAttemptId(), 0, false, false);
                return;
            }
            if (self.activeLobby?.isHost != true) { original(self); return; }
            if (!SavegameLoadCoordinator.CancelSelection())
            { ShowRejectedRequest(self.activeLobby, "A confirmed multiplayer save is already starting."); return; }
            if (pending != null)
            {
                if (released || loading)
                { ShowRejectedRequest(self.activeLobby, "A confirmed multiplayer map is already starting."); return; }
                try
                {
                    CheckContext();
                    if (FingerprintMatches(true)) return;
                    Abort(FingerprintMismatch(), true, phase: "replacement request");
                }
                catch (Exception error) { Abort(error.Message, true, phase: "replacement request"); }
            }
            try
            {
                owner = self;
                var lobby = self.activeLobby;
                BeginAttempt(lobby, LobbyReadinessSupport.NewAttemptId());
                var humans = Humans(lobby); ulong local = SteamUser.GetSteamID().m_SteamID;
                hostId = SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID;
                if (hostId != local || !humans.Contains(local)) throw new InvalidOperationException("The local host is not the current lobby owner and member.");
                localPath = Path.GetFullPath(Header(lobby).filePath);
                if (!File.Exists(localPath))
                    throw new FileNotFoundException(UiText.Get("LobbyLaunch.MapFileUnavailable", lobby.mapFileName));
                localRecords = Capture(localPath, out localHash);
                Economy.WorkerProductionAgreement.SetPreparedDataPath(localPath);
                preparedSnapshot = lastSnapshot = new LobbySnapshot(lobby);
                string fingerprint = preparedSnapshot.Fingerprint;
                pending = new SavegameLoadPacket { Version = MapRecordReadinessPacket.CurrentVersion,
                    Operation = 0, Lobby = attemptLobby, Token = attemptId,
                    SaveName = lobby.mapFileName, Checksum = Checksum(lobby), Fingerprint = fingerprint, Roster = Roster(lobby),
                    Records = localRecords, Metadata = preparedSnapshot.Digests };
                agreement = new SavegameLoadAgreement(pending.Token, fingerprint, humans.Where(id => id != local), localRecords);
                acknowledged = released = loading = false; fingerprintCheckedAt = 0;
                deadline = DateTime.UtcNow.AddSeconds(LobbyReadinessSupport.PreparationSeconds);
                if (!agreement.Ready) Send(pending);
                Tick();
            }
            catch (Exception error) { Abort(error.Message, true, phase: "request"); }
            finally { Schedule(); }
        }
        private static void BeginAttempt(Platform_Multiplayer.MPLobby lobby, string token)
        {
            rejectedNativeLobby = null;
            rejectedNativePath = null;
            attemptId = token;
            attemptLobby = lobby.id.m_SteamID;
            attemptHost = lobby.isHost;
            waitNoticeAt = DateTime.UtcNow.AddSeconds(LobbyReadinessSupport.WaitingNoticeDelaySeconds);
            waitingReasonDirty = true;
        }
        private static void Send(SavegameLoadPacket packet)
        {
            var result = channel.SendToLobby(new MapRecordReadinessPacket { Value = packet });
            if (!result.Submitted) throw new InvalidOperationException("Map readiness send failed: " + result.Reason);
        }
        private static SavegameLoadPacket Reply(int operation, string reason = null) => new SavegameLoadPacket {
            Version = MapRecordReadinessPacket.CurrentVersion,
            Operation = operation, Lobby = pending.Lobby, Token = pending.Token, SaveName = pending.SaveName,
            Checksum = pending.Checksum, Fingerprint = pending.Fingerprint, Roster = pending.Roster,
            Records = operation == 1 ? localRecords : Array.Empty<SavegameRecordDigest>(),
            Reason = LobbyReadinessSupport.TruncateReason(reason)
        };
        private static void Receive(NetworkMessage<MapRecordReadinessPacket> message)
        {
            try { ReceiveCore(message); } catch (Exception error) { Abort(error.Message, true, phase: "readiness message"); }
        }
        private static void ReceiveCore(NetworkMessage<MapRecordReadinessPacket> message)
        {
            var packet = message.Packet?.Value; var mp = Platform_Multiplayer.Instance; var lobby = mp?.activeLobby;
            if (packet == null || lobby == null) return;
            ulong host = SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID;
            if (host == 0 || (lobby.isHost
                ? lobby.members?.Any(member => LobbyReadinessSupport.IsHuman(member) && member.id.m_SteamID == message.SenderSteamId) != true
                : message.SenderSteamId != host)) return;
            if (packet.Version != MapRecordReadinessPacket.CurrentVersion)
            {
                // A stale legacy response or a version-only notice has no authority to cancel a current attempt.
                if (lobby.isHost)
                {
                    if (!attemptHost || host != hostId || message.SenderSteamId == host ||
                        packet.Operation != 3 || packet.Metadata != null || !IsCurrentEnvelope(packet, lobby) ||
                        !agreement.Unconfirmed.Contains(message.SenderSteamId)) return;
                }
                else if (pending != null) return;
                if (packet.Token != null && retired.Contains(packet.Token)) return;
                string reason = LobbyReadinessSupport.PlayerName(message.SenderSteamId) + " uses map readiness protocol " + packet.Version +
                    ", but this APIShared uses " + MapRecordReadinessPacket.CurrentVersion + ". Install the same APIShared build on every computer.";
                if (!lobby.isHost && (packet.Version == MapRecordReadinessPacket.PreviousVersion ||
                    packet.Version == MapRecordReadinessPacket.LegacyMapVersion) && packet.Operation == 0 &&
                    HasProposalIdentity(packet, lobby) && packet.Metadata == null)
                {
                    // An older host can understand a rejection on its own transaction.
                    retired.Add(packet.Token);
                    packet.Operation = 3;
                    packet.Reason = reason;
                    packet.Records = Array.Empty<SavegameRecordDigest>();
                    try { ForwardCancel(packet, host); }
                    catch (Exception error) { NativeApiLog.Error(log, "Map preparation cancellation send failed: " + error.Message); }
                    ShowBlocked(reason, "MAP_RECORD_LOAD_BLOCKED role=client phase=protocol negotiation attempt=" + packet.Token,
                        lobby.id.m_SteamID, false, true);
                    return;
                }
                if (pending == null) BeginAttempt(lobby, LobbyReadinessSupport.NewAttemptId());
                Abort(reason, pending != null, phase: "protocol negotiation", localChat: !lobby.isHost);
                return;
            }
            if (packet.Operation == 3 && packet.Metadata == null && host == hostId && IsCurrentEnvelope(packet, lobby))
            {
                if (lobby.isHost && message.SenderSteamId != host)
                    Abort(UiText.Get("LobbyLaunch.MapPeerCancelled", LobbyReadinessSupport.PlayerName(message.SenderSteamId), packet.Reason), true, phase: "peer rejection");
                else if (!lobby.isHost && message.SenderSteamId == host)
                    Abort("Host cancelled map preparation: " + packet.Reason, false, phase: "host cancellation");
                return;
            }
            if (lobby.isHost)
            {
                if (!attemptHost || host != hostId || message.SenderSteamId == host || !IsCurrentEnvelope(packet, lobby) ||
                    !agreement.Unconfirmed.Contains(message.SenderSteamId)) return;
                if (packet.Operation == MapRecordReadinessPacket.ObservationOperation)
                {
                    if (AcceptObservation(message.SenderSteamId, packet)) Tick();
                    return;
                }
                if (packet.Operation != 1 || packet.Metadata != null) return;
                if (agreement.Accept(message.SenderSteamId, packet.Token, packet.Fingerprint,
                    packet.Records, LobbyReadinessSupport.PlayerName(message.SenderSteamId), out string mismatch))
                {
                    PeerObservations.Remove(message.SenderSteamId);
                    waitingReasonDirty = true;
                }
                else if (mismatch != null) { Abort(mismatch); return; }
                Tick();
                return;
            }

            if (message.SenderSteamId != host) return;
            if (packet.Operation == 0)
            {
                if (!HasProposalIdentity(packet, lobby) || packet.Metadata?.Length != Categories.Length * DigestLength ||
                    LobbySnapshot.Digest(packet.Metadata) != packet.Fingerprint) return;
                if (pending?.Token == packet.Token) { Tick(); return; }
                if (released || loading) return;
                if (pending != null) Abort("Host replaced map preparation.", false, false);
                owner = mp; hostId = host; pending = packet;
                BeginAttempt(lobby, packet.Token);
                if (!SavegameRecordComparison.IsValid(packet.Records)) throw new InvalidDataException("Invalid host map record list.");
                deadline = DateTime.UtcNow.AddSeconds(LobbyReadinessSupport.PreparationSeconds);
                Tick();
            }
            else if (packet.Operation == 2 && packet.Metadata == null && host == hostId &&
                acknowledged && IsCurrentEnvelope(packet, lobby) && !released)
            {
                deadline = DateTime.UtcNow.AddSeconds(LobbyReadinessSupport.StartWindowSeconds);
                released = true;
            }
        }

        // Control messages stay bound to the authenticated proposal even while live metadata is still arriving.
        private static bool IsCurrentEnvelope(SavegameLoadPacket packet, Platform_Multiplayer.MPLobby lobby) =>
            pending != null && packet.Lobby == lobby.id.m_SteamID && packet.Lobby == pending.Lobby &&
            packet.Token == pending.Token && packet.Fingerprint == pending.Fingerprint && packet.Roster == pending.Roster &&
            packet.SaveName == pending.SaveName && packet.Checksum == pending.Checksum && packet.Time == 0;

        private static bool HasProposalIdentity(SavegameLoadPacket packet, Platform_Multiplayer.MPLobby lobby) =>
            packet.Lobby == lobby.id.m_SteamID && !string.IsNullOrEmpty(packet.Roster) && packet.Roster.Length <= 256 &&
            packet.Token?.Length == 32 && Guid.TryParseExact(packet.Token, "N", out _) && !retired.Contains(packet.Token) &&
            !string.IsNullOrWhiteSpace(packet.SaveName) && packet.SaveName.Length <= 1024 &&
            packet.Time == 0 && packet.Fingerprint?.Length == 64;

        private static bool FingerprintMatches(bool decisive)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (decisive || fingerprintCheckedAt == 0 || LobbyReadinessSupport.FingerprintRecheckDue(fingerprintCheckedAt, now))
            {
                lastSnapshot = new LobbySnapshot(owner.activeLobby);
                fingerprintMatched = lastSnapshot.Fingerprint == pending.Fingerprint;
                if (fingerprintMatched && preparedSnapshot == null) preparedSnapshot = lastSnapshot;
                fingerprintCheckedAt = now;
            }
            return fingerprintMatched;
        }
        private static void CheckContext()
        {
            var lobby = CheckLobby();
            if (Roster(lobby) != pending.Roster)
                throw new InvalidOperationException("Human roster changed during map preparation.");
            if (!SameMap(pending.SaveName, lobby.mapFileName) || pending.Checksum != Checksum(lobby))
                throw new InvalidDataException("Map selection or CRC changed during preparation.");
            if (!string.Equals(Path.GetFullPath(Header(lobby).filePath), localPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Local map file selection changed.");
        }
        private static Platform_Multiplayer.MPLobby CheckLobby()
        {
            var lobby = owner?.activeLobby;
            if (lobby == null || lobby.id.m_SteamID != pending.Lobby ||
                SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID != hostId || lobby.isHost != attemptHost)
                throw new InvalidOperationException("Lobby owner changed or the lobby closed during map preparation.");
            return lobby;
        }

        private static void PrepareClientMap(Platform_Multiplayer.MPLobby lobby)
        {
            // Compare lobby metadata first; only the matching selection can resolve and capture its local file.
            if (localPath != null) return;
            localPath = Path.GetFullPath(Header(lobby).filePath);
            if (!File.Exists(localPath))
                throw new FileNotFoundException(UiText.Get("LobbyLaunch.MapFileUnavailable", pending.SaveName));
            localRecords = Capture(localPath, out localHash);
            Economy.WorkerProductionAgreement.SetPreparedDataPath(localPath);
            string mismatch = SavegameRecordComparison.Describe(pending.Records, localRecords, LobbyReadinessSupport.PlayerName(SteamUser.GetSteamID().m_SteamID));
            if (mismatch != null) throw new InvalidDataException(mismatch);
        }

        private static void CheckFile()
        { if (Hash(localPath) != localHash) throw new InvalidDataException("Local map changed after its simulation records were confirmed."); }
        private static void Tick()
        {
            if (pending == null || loading) return;
            string phase = "waiting for readiness";
            try
            {
                var lobby = CheckLobby();
                if (DateTime.UtcNow > deadline) throw new TimeoutException(TimeoutReason());
                if (attemptHost)
                {
                    CheckContext();
                    if (!FingerprintMatches(false)) throw new InvalidOperationException(FingerprintMismatch());
                    if (released || !agreement.Ready || !PrepareConfiguration()) return;
                    phase = "before GO";
                    CheckContext();
                    if (!FingerprintMatches(true)) throw new InvalidOperationException(FingerprintMismatch());
                    CheckFile();
                    if (!Economy.WorkerProductionAgreement.TryPrepareSaveAdmission(localPath, out bool productionWaiting, out string productionReason))
                    {
                        if (!productionWaiting) throw new InvalidOperationException(productionReason);
                        SetConfigurationWaiting(productionReason);
                        return;
                    }
                    released = true; deadline = DateTime.UtcNow.AddSeconds(LobbyReadinessSupport.StartWindowSeconds);
                    if (Humans(owner.activeLobby).Length > 1) Send(Reply(2));
                    NativeApiLog.Debug(log, "MAP_RECORDS_CONFIRMED: all human peers ready; publishing Vanilla GO once.");
                    original(owner);
                }
                else if (acknowledged)
                {
                    CheckContext();
                    if (!FingerprintMatches(false)) throw new InvalidOperationException(FingerprintMismatch());
                }
                else
                {
                    if (!FingerprintMatches(false))
                    {
                        SetConfigurationWaiting(null);
                        SendObservation(DiffObservation);
                        return;
                    }
                    // A roster with pending kicks is still settling even when its visible slot digest matches.
                    if (lobby.members == null || lobby.members.Any(member => member == null || member.dummyToBeKicked)) return;
                    PrepareClientMap(lobby);
                    if (!PrepareConfiguration()) { SendObservation(ConfigurationObservation); return; }
                    phase = "before acknowledgement";
                    CheckLobby();
                    if (!FingerprintMatches(true))
                    {
                        SetConfigurationWaiting(null);
                        SendObservation(DiffObservation);
                        return;
                    }
                    if (lobby.members == null || lobby.members.Any(member => member == null || member.dummyToBeKicked)) return;
                    CheckContext();
                    CheckFile();
                    if (!Economy.WorkerProductionAgreement.TryPrepareSaveAdmission(localPath, out bool productionWaiting, out string productionReason))
                    {
                        if (!productionWaiting) throw new InvalidOperationException(productionReason);
                        SetConfigurationWaiting(productionReason);
                        SendObservation(ProductionObservation);
                        return;
                    }
                    var sent = channel.SendToSteamPeer(new MapRecordReadinessPacket { Value = Reply(1) }, hostId);
                    if (!sent.Submitted) throw new InvalidOperationException("Map acknowledgement failed: " + sent.Reason);
                    acknowledged = true;
                }
            }
            catch (Exception error) { Abort(error.Message, true, phase: error is TimeoutException ? "timeout" : phase); }
            finally
            {
                Schedule();
                AnnounceWaiting();
            }
        }

        private static void AnnounceWaiting()
        {
            if (pending == null || released || loading || DateTime.UtcNow < waitNoticeAt ||
                (!attemptHost && !observationSendFailed) || !waitingReasonDirty) return;
            waitingReasonDirty = false;
            string reason = WaitingReason();
            if (reason == announcedWaiting) return;
            announcedWaiting = reason;
            LobbyLaunchStatus.ReportPreparationWaiting(log, reason,
                $"MAP_RECORD_WAITING role={(attemptHost ? "host" : "client")} attempt={attemptId}", attemptLobby, attemptHost, !attemptHost);
        }

        private static string WaitingReason()
        {
            if (configurationWaiting != null) return configurationWaiting;
            if (!attemptHost)
            {
                if (!fingerprintMatched && lastSnapshot != null)
                    return UiText.Get("LobbyLaunch.MapLocalMismatch", CategoryLabels(Differences(pending.Metadata, lastSnapshot.Digests)));
                return UiText.Get("LobbyLaunch.MapAwaitingHost");
            }

            var peers = agreement.Unconfirmed.ToArray();
            if (peers.Length == 0) return UiText.Get("LobbyLaunch.MapConfigurationWaiting");
            return string.Join(" ", peers.Select(peer =>
            {
                if (!PeerObservations.TryGetValue(peer, out byte[] observed))
                    return UiText.Get("LobbyLaunch.MapAwaitingPeer", LobbyReadinessSupport.PlayerName(peer));
                if (observed[0] == DiffObservation)
                    return UiText.Get("LobbyLaunch.MapPeerMismatch", LobbyReadinessSupport.PlayerName(peer), CategoryLabels(Differences(pending.Metadata, observed, 1)));
                string phase = observed[0] == ConfigurationObservation ? "MapConfigurationWaiting" : "MapProductionWaiting";
                return UiText.Get("LobbyLaunch.MapPeerPreparation", LobbyReadinessSupport.PlayerName(peer), UiText.Get("LobbyLaunch." + phase));
            }));
        }

        private static bool PrepareConfiguration()
        {
            bool ready = ModSettings.ModSettingsApplication.PrepareLaunch();
            if (!ready) SetConfigurationWaiting(UiText.Get("LobbyLaunch.MapConfigurationWaiting"));
            return ready;
        }

        private static void SetConfigurationWaiting(string reason)
        {
            if (configurationWaiting == reason) return;
            configurationWaiting = reason;
            waitingReasonDirty = true;
        }
        private static void Schedule() => frameWork?.SetContinuous(pending != null && !loading);
        internal static bool ValidateStart(string path, bool multiplayer)
        {
            if (!multiplayer) return true;
            if (!Internal.UnityMainThreadDispatch.IsMainThread)
            { Abort("Map readiness validation requires Unity's main thread."); return false; }
            var lobby = Platform_Multiplayer.Instance?.activeLobby;
            if (pending == null && lobby != null && ReferenceEquals(lobby, rejectedNativeLobby) &&
                string.Equals(path, rejectedNativePath, StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                if (!initialized || pending == null || (attemptHost ? !released : !acknowledged))
                    throw new InvalidOperationException("Multiplayer map has no confirmed record preparation.");
                CheckContext();
                if (!string.Equals(Path.GetFullPath(path), localPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Native map load differs from the confirmed local file.");
                CheckFile();
                if (!FingerprintMatches(true)) throw new InvalidOperationException(FingerprintMismatch());
                if (!PrepareConfiguration()) throw new InvalidOperationException(configurationWaiting);
                CheckContext();
                if (!FingerprintMatches(true)) throw new InvalidOperationException(FingerprintMismatch());
                if (!Economy.WorkerProductionAgreement.TryPrepareSaveAdmission(localPath, out _, out string productionReason))
                    throw new InvalidOperationException(productionReason);
                CheckFile();
                System.Threading.Volatile.Write(ref admission, new NativeAdmissionTicket(localPath, localHash));
                loading = true; Schedule(); return true;
            }
            catch (Exception error) { Abort(error.Message, true, phase: "before native start"); return false; }
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
            UiText.Get("LobbyLaunch.MapTimedOut", WaitingReason());

        internal static void Abort(string reason)
        {
            if (Internal.UnityMainThreadDispatch.IsMainThread) { Abort(reason, true, phase: "native admission"); return; }
            // Revoke native admission immediately, then retire only this exact
            // transaction on Unity; delayed failure reporting must not cancel a new map.
            var transaction = System.Threading.Volatile.Read(ref pending);
            var ticket = System.Threading.Volatile.Read(ref admission);
            System.Threading.Interlocked.CompareExchange(ref admission, null, ticket);
            if (!Internal.UnityMainThreadDispatch.TryEnqueue(() => {
                if (ReferenceEquals(pending, transaction)) Abort(reason, true, phase: "native admission");
                else ReportUnscoped(reason, transaction?.Token);
            })) ReportUnscoped(reason, transaction?.Token);
        }

        private static void ReportUnscoped(string reason, string token) => LobbyLaunchStatus.ReportPreparationBlocked(log, reason,
            "MAP_RECORD_LOAD_BLOCKED role=unknown phase=native admission attempt=" + (token ?? LobbyReadinessSupport.NewAttemptId()),
            0, false, false);

        private static bool ForwardCancel(SavegameLoadPacket packet, ulong host)
        {
            var result = channel.SendToSteamPeer(new MapRecordReadinessPacket { Value = packet }, host);
            if (!result.Submitted) NativeApiLog.Error(log, "Map preparation cancellation send failed: " + result.Reason);
            return result.Submitted;
        }

        private static void Abort(string reason, bool notify, bool showError = true, string phase = "preparation", bool localChat = false)
        {
            if (attemptId == null && showError)
            {
                var lobby = Platform_Multiplayer.Instance?.activeLobby;
                if (lobby != null) BeginAttempt(lobby, LobbyReadinessSupport.NewAttemptId());
            }
            if (pending == null && notify && !attemptHost && attemptLobby != 0) localChat = true;
            if (pending != null)
            {
                var currentLobby = Platform_Multiplayer.Instance?.activeLobby;
                if (notify && currentLobby?.id.m_SteamID == attemptLobby) try
                {
                    if (attemptHost && currentLobby.isHost && SteamMatchmaking.GetLobbyOwner(currentLobby.id).m_SteamID == hostId)
                        Send(Reply(3, reason));
                    else if (!attemptHost && !currentLobby.isHost && SteamMatchmaking.GetLobbyOwner(currentLobby.id).m_SteamID == hostId)
                        localChat = !ForwardCancel(Reply(3, reason), hostId);
                    else localChat = true;
                }
                catch (Exception error)
                {
                    localChat = !attemptHost;
                    NativeApiLog.Error(log, "Map preparation cancellation send failed: " + error.Message);
                }
                retired.Add(pending.Token);
            }
            string detail = null;
            if (showError)
            {
                try { detail = BlockDetail(phase); }
                catch (Exception error) { detail = BlockContext(phase) + "; comparison diagnostics failed: " + error.Message; }
            }
            ulong lobbyId = attemptLobby;
            bool broadcast = attemptHost;
            var rejectedLobby = owner?.activeLobby;
            if (showError && (released || acknowledged) && localPath != null && rejectedLobby?.id.m_SteamID == lobbyId)
            {
                // Vanilla may still call StartGame for the GO already published for this attempt.
                rejectedNativeLobby = rejectedLobby;
                rejectedNativePath = localPath;
            }
            try { Clear(); }
            catch (Exception error) { NativeApiLog.Error(log, "Map preparation cleanup failed: " + error); }
            if (!showError) { NativeApiLog.Debug(log, "MAP_RECORD_PREPARATION_CANCELLED: " + reason); return; }
            ShowBlocked(reason, detail, lobbyId, broadcast, localChat);
        }

        private static void Clear()
        {
            System.Threading.Volatile.Write(ref admission, null);
            string preparedPath = localPath;
            pending = null; agreement = null; owner = null; localRecords = null; localPath = localHash = null;
            acknowledged = released = loading = fingerprintMatched = false; fingerprintCheckedAt = 0;
            preparedSnapshot = lastSnapshot = null;
            attemptId = configurationWaiting = announcedWaiting = null; attemptLobby = 0; attemptHost = false;
            lastSentObservation = null; lastSentSnapshot = null; lastLoggedModFingerprint = null;
            observationSendFailed = waitingReasonDirty = false; PeerObservations.Clear();
            frameWork?.CancelPending(); frameWork?.SetContinuous(false);
            if (preparedPath != null && string.Equals(Economy.WorkerProductionAgreement.PreparedDataPath, preparedPath, StringComparison.OrdinalIgnoreCase))
                Economy.WorkerProductionAgreement.SetPreparedDataPath(null);
        }
        internal static void FinishLoad()
        {
            if (Internal.UnityMainThreadDispatch.IsMainThread)
            {
                if (pending != null) retired.Add(pending.Token);
                rejectedNativeLobby = null;
                rejectedNativePath = null;
                Clear();
                return;
            }
            var transaction = System.Threading.Volatile.Read(ref pending);
            var ticket = System.Threading.Volatile.Read(ref admission);
            System.Threading.Interlocked.CompareExchange(ref admission, null, ticket);
            if (!Internal.UnityMainThreadDispatch.TryEnqueue(() => {
                if (ReferenceEquals(pending, transaction)) FinishLoad();
            })) NativeApiLog.Error(log, "MAP_RECORD_FINISH_DEFERRED_UNAVAILABLE");
        }
    }
}
