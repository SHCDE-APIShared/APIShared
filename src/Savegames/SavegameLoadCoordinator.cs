using APIShared.Networking;
using APIShared.Localization;
using APIShared.Savegames;
using BepInEx.Logging;
using CrusaderDE;
using MessagePack;
using MonoMod.RuntimeDetour;
using SHCDESE.API;
using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace APIShared
{
    // Owns the HostLoadGame entry, not FRONT_Multiplayer.Update or a plugin
    // callback. The static hook/channel/render publisher survive startup cleanup.
    internal static class SavegameLoadCoordinator
    {
        private delegate void HostLoad(Platform_Multiplayer self, string name);
        private static Hook hook;
        private static HostLoad original;
        private static MessageChannel<SavegameLoadPacket> channel;
        private static ManualLogSource log;
        private static SavegameLoadPacket pending;
        private static SavegameModSettingsRecord record;
        private static SavegameLoadAgreement agreement;
        private static Platform_Multiplayer owner;
        private static DateTime deadline, waitNoticeAt;
        private static string waitingReason, announcedWaiting;
        private static bool acknowledged, released, loading, initialized;
        private static ulong hostId;
        private static Presentation.IUiFrameWorkRegistration frameWork;
        private static string localSavePath, localSaveHash;
        private static string attempt, observedFingerprint;
        private static bool attemptHost;
        private static Platform_Multiplayer.MPLobby rejectedNativeLobby;
        private static string rejectedNativePath;
        // This peer's digests of the declared simulation records in its own copy of the selected save.
        private static IReadOnlyList<SavegameRecordDigest> localRecords;
        private static readonly BoundedTokenSet retired = new BoundedTokenSet();

        internal static void Initialize(ManualLogSource logger)
        {
            if (initialized) return;
            log = logger;
            channel = ApiShared.ForMod(APISharedPlugin.PluginGuid).Network.RegisterMessage<SavegameLoadPacket>(
                "SavegameLoadReadiness", Receive, new MessageChannelOptions<SavegameLoadPacket> { MaximumBodyBytes = LobbyReadinessSupport.MaximumBodyBytes });
            Hook candidate = null;
            try
            {
                candidate = new Hook(typeof(Platform_Multiplayer).GetMethod(nameof(Platform_Multiplayer.HostLoadGame)),
                    (HostLoad)Request, new HookConfig { ManualApply = true });
                original = candidate.GenerateTrampoline<HostLoad>();
                candidate.Apply();
                hook = candidate;
                if (!Presentation.UiWorkRuntime.TryRegister(APISharedPlugin.PluginGuid, "internal/20-save-load",
                    Tick, 0, out frameWork, out string frameReason)) throw new InvalidOperationException(frameReason);
                initialized = true;
                NativeApiLog.Debug(log, "SAVE_SETTINGS_PREPARATION_READY: permanent host-load gate installed after startup cleanup.");
            }
            catch { if (hook == null) candidate?.Dispose(); throw; }
        }

        private static ulong[] Humans(Platform_Multiplayer.MPLobby lobby)
        {
            if (lobby?.members == null) throw new InvalidOperationException("Lobby roster unavailable.");
            if (lobby.members.Any(x => x == null || x.dummyToBeKicked)) throw new InvalidOperationException("Lobby roster is changing.");
            var result = LobbyReadinessSupport.ObservedHumans(lobby);
            if (result.Length == 0 || result.Length != result.Distinct().Count()) throw new InvalidOperationException("Invalid human roster.");
            return result;
        }
        private static string Roster(Platform_Multiplayer.MPLobby lobby) => string.Join(",", Humans(lobby));
        private static bool Matches(FileHeader header, SavegameLoadPacket packet) => header != null &&
            SavegameLoadAgreement.MatchesSaveIdentity(header.fileName, header.xPlaySaveChecksum, header.xPlaySaveTime,
                packet.SaveName, packet.Checksum, packet.Time);
        private static string HashLocalSave(string path)
        {
            using (var stream = File.OpenRead(path))
                return Sha256Hex.Of(stream);
        }

        private static void Request(Platform_Multiplayer self, string name)
        {
            if (self.activeLobby?.isHost != true) { original(self, name); return; }
            if (!MapRecordLoadCoordinator.CancelSelection())
            { ShowBlocked("A confirmed multiplayer map is already starting.", self.activeLobby, LobbyReadinessSupport.NewAttemptId(), "request", true); return; }
            if (pending != null)
            {
                if (pending.SaveName == name) return; // Repeated button callback, not another load.
                if (released || loading)
                { ShowBlocked("A confirmed multiplayer save is already starting.", self.activeLobby, LobbyReadinessSupport.NewAttemptId(), "request", true); return; }
                Abort("Selected save changed.", notify: true, showError: false);
            }
            owner = self;
            attempt = LobbyReadinessSupport.NewAttemptId();
            waitNoticeAt = DateTime.UtcNow.AddSeconds(LobbyReadinessSupport.WaitingNoticeDelaySeconds);
            attemptHost = true;
            rejectedNativeLobby = null; rejectedNativePath = null;
            try
            {
                var header = MapFileManager.Instance.GetHeaderFromMpSaveFileName(name);
                if (header == null) throw new InvalidDataException("Selected multiplayer save is unavailable.");
                localSavePath = Path.GetFullPath(header.filePath);
                Economy.WorkerProductionAgreement.SetPreparedSavePath(localSavePath);
                localSaveHash = HashLocalSave(localSavePath);
                record = SavegameModSettings.PrepareHostSave(header.filePath);
                if (HashLocalSave(localSavePath) != localSaveHash) throw new InvalidDataException("Save changed during preparation.");
                string fingerprint = SavegameModSettings.CaptureHostFingerprint();
                var humans = Humans(self.activeLobby);
                ulong local = SteamUser.GetSteamID().m_SteamID;
                if (!humans.Contains(local)) throw new InvalidOperationException("Host is absent from lobby roster.");
                hostId = SteamMatchmaking.GetLobbyOwner(self.activeLobby.id).m_SteamID;
                if (hostId != local) throw new InvalidOperationException("Local host identity differs from lobby owner.");
                // The stored records of this exact file; ValidateStart's whole-file hash keeps them current.
                // A solo host has nobody to compare with and reads nothing.
                IReadOnlyList<SavegameRecordDigest> records = humans.Length > 1
                    ? VersionedSaveDataRuntime.CaptureSimulationDigests(localSavePath) : Array.Empty<SavegameRecordDigest>();
                pending = new SavegameLoadPacket { Operation = 0, Lobby = self.activeLobby.id.m_SteamID,
                    Token = attempt, SaveName = header.fileName,
                    Checksum = header.xPlaySaveChecksum, Time = header.xPlaySaveTime,
                    Fingerprint = fingerprint, Roster = Roster(self.activeLobby),
                    Metadata = record == null ? null : MessagePackSerializer.Serialize(record), Records = records };
                agreement = new SavegameLoadAgreement(pending.Token, fingerprint, humans.Where(x => x != local), records);
                deadline = DateTime.UtcNow.AddSeconds(LobbyReadinessSupport.PreparationSeconds);
                acknowledged = false; released = false; loading = false;
                SavegameModSettings.PublishPreparedHostValues();
                if (!agreement.Ready) Send(pending);
                Tick(); // Solo host: no network echo, no artificial delay.
            }
            catch (Exception error) { Abort(error.Message, phase: "request"); }
            finally { ScheduleWaitingCheck(); }
        }

        private static void Send(SavegameLoadPacket packet)
        {
            var result = channel.SendToLobby(packet);
            if (!result.Submitted) throw new InvalidOperationException("Save readiness transport failed: " + result.Reason);
        }
        private static SavegameLoadPacket Reply(int operation, string reason = null) => new SavegameLoadPacket
        {
            Operation = operation, Lobby = pending.Lobby, Token = pending.Token, SaveName = pending.SaveName,
            Checksum = pending.Checksum, Time = pending.Time, Fingerprint = pending.Fingerprint, Roster = pending.Roster,
            Records = operation == 1 ? localRecords ?? Array.Empty<SavegameRecordDigest>() : Array.Empty<SavegameRecordDigest>(),
            Reason = LobbyReadinessSupport.TruncateReason(reason)
        };

        // A peer on another readiness protocol cannot take part. A protocol-1 peer still understands a
        // protocol-1 cancel, so it is told; the load is blocked with a message naming the versions.
        private static void RejectProtocol(SavegameLoadPacket packet, ulong sender, Platform_Multiplayer.MPLobby lobby)
        {
            if (!Humans(lobby).Contains(sender) || packet.Token != null && retired.Contains(packet.Token)) return;
            if (lobby.isHost ? pending == null || sender == hostId : sender != SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID) return;
            if (packet.Token != null) retired.Add(packet.Token); // The peer's own cancel echo is not reported twice.
            string text = LobbyReadinessSupport.PlayerName(sender) + " uses save readiness protocol " + packet.Version + " and this APIShared build uses " +
                SavegameLoadPacket.CurrentVersion + ". Every player needs the same APIShared version.";
            if (packet.Version == SavegameLoadPacket.LegacyVersion && packet.Lobby == lobby.id.m_SteamID && packet.Token?.Length == 32)
            {
                var cancel = new SavegameLoadPacket { Version = SavegameLoadPacket.LegacyVersion, Operation = 3, Lobby = packet.Lobby,
                    Token = packet.Token, SaveName = packet.SaveName, Checksum = packet.Checksum, Time = packet.Time,
                    Fingerprint = packet.Fingerprint, Roster = packet.Roster };
                var result = channel.SendToSteamPeer(cancel, sender);
                if (!result.Submitted) NativeApiLog.Error(log, "Could not cancel the protocol-1 peer's preparation: " + result.Reason);
            }
            if (pending != null) { Abort(text, phase: "protocol"); return; }
            ShowBlocked(text, lobby, packet.Token ?? LobbyReadinessSupport.NewAttemptId(), "protocol", lobby.isHost);
        }

        private static void Receive(NetworkMessage<SavegameLoadPacket> message)
        {
            try { ReceiveCore(message); }
            catch (Exception error) { Abort(error.Message, phase: "receive"); }
        }

        private static void ReceiveCore(NetworkMessage<SavegameLoadPacket> message)
        {
            var packet = message.Packet;
            var mp = Platform_Multiplayer.Instance;
            var lobby = mp?.activeLobby;
            if (packet == null || lobby == null) return;
            if (packet.Version != SavegameLoadPacket.CurrentVersion) { RejectProtocol(packet, message.SenderSteamId, lobby); return; }
            ulong host = SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID;
            if (!lobby.isHost && message.SenderSteamId != host) return;
            if (packet.Operation == 3)
            {
                // A cancellation closes the prepared attempt, even if the live roster has changed.
                if (pending == null || lobby.id.m_SteamID != pending.Lobby || host != hostId ||
                    packet.Lobby != pending.Lobby || packet.Token != pending.Token || packet.Fingerprint != pending.Fingerprint ||
                    packet.Roster != pending.Roster || packet.SaveName != pending.SaveName ||
                    packet.Checksum != pending.Checksum || packet.Time != pending.Time) return;
                if (lobby.isHost)
                {
                    if (message.SenderSteamId == host || lobby.members?.Any(member => member != null && !member.dummyToBeKicked &&
                        !member.SkirmishMember && member.id.m_SteamID == message.SenderSteamId) != true) return;
                    Abort(string.IsNullOrEmpty(packet.Reason) ? "A participant cancelled save preparation." :
                        LobbyReadinessSupport.PlayerName(message.SenderSteamId) + " cancelled save preparation: " + packet.Reason, phase: "peer-cancel");
                }
                else Abort(string.IsNullOrEmpty(packet.Reason) ? "Host cancelled the save preparation." :
                    "Host cancelled the save preparation: " + packet.Reason, notify: false, phase: "host-cancel");
                return;
            }
            if (packet.Lobby != lobby.id.m_SteamID ||
                packet.Operation < 0 || packet.Operation > 2 || packet.Token?.Length != 32 ||
                !Guid.TryParseExact(packet.Token, "N", out _) || packet.SaveName?.Length > 1024 ||
                string.IsNullOrEmpty(packet.SaveName) || packet.Fingerprint?.Length != 64 ||
                packet.Roster != Roster(lobby)) return;
            if (lobby.isHost)
            {
                // The host compares every peer's declared simulation records with its own; a difference
                // ends the preparation for everyone with the explanation.
                if (packet.Operation == 1 && pending != null && agreement != null && packet.SaveName == pending.SaveName &&
                    packet.Checksum == pending.Checksum && packet.Time == pending.Time &&
                    !agreement.Accept(message.SenderSteamId, packet.Token, packet.Fingerprint, packet.Records,
                        LobbyReadinessSupport.PlayerName(message.SenderSteamId), out string mismatch) && mismatch != null)
                { Abort(mismatch, phase: "peer-comparison"); return; }
                Tick(); return;
            }
            if (retired.Contains(packet.Token)) return;
            if (packet.Operation == 0)
            {
                if (pending?.Token == packet.Token) { Tick(); return; }
                if (pending != null) Abort("Host replaced the save preparation.", notify: false, showError: false);
                pending = packet; owner = mp; attempt = packet.Token; agreement = null;
                waitNoticeAt = DateTime.UtcNow.AddSeconds(LobbyReadinessSupport.WaitingNoticeDelaySeconds);
                acknowledged = false; released = false; loading = false; hostId = host; attemptHost = false;
                rejectedNativeLobby = null; rejectedNativePath = null;
                if (packet.Metadata != null && !SavegameModSettings.TryDeserialize(packet.Metadata, out record))
                    throw new InvalidDataException("Invalid host save provenance.");
                if (packet.Metadata == null) record = null;
                if (record != null && record.Mods.Count != 0) throw new InvalidDataException("Readiness must not carry settings values.");
                if (!SavegameRecordComparison.IsValid(packet.Records)) throw new InvalidDataException("Invalid host save record list.");
                var header = MapFileManager.Instance.GetHeaderFromMpSaveFileName(packet.SaveName);
                if (!Matches(header, packet)) { Abort("Local save does not match host selection."); return; }
                localSavePath = Path.GetFullPath(header.filePath);
                Economy.WorkerProductionAgreement.SetPreparedSavePath(localSavePath);
                localSaveHash = HashLocalSave(localSavePath);
                // Taken from this peer's own copy of the selected save, never from earlier in-memory
                // state, so a previous single-player session or different local settings do not count.
                localRecords = VersionedSaveDataRuntime.CaptureSimulationDigests(localSavePath);
                deadline = DateTime.UtcNow.AddSeconds(LobbyReadinessSupport.PreparationSeconds);
                Tick();
            }
            else if (pending?.Token == packet.Token && packet.Operation == 2)
            {
                if (!released) deadline = DateTime.UtcNow.AddSeconds(LobbyReadinessSupport.StartWindowSeconds);
                released = true;
            }
        }

        // A release or acknowledgement always compares a fresh fingerprint, as does ValidateStart.
        private static string fingerprintToken;
        private static long fingerprintCheckedAt;
        private static bool fingerprintMatched;
        private static bool FingerprintMatches(bool decisive)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (decisive || fingerprintToken != pending.Token || LobbyReadinessSupport.FingerprintRecheckDue(fingerprintCheckedAt, now))
            {
                observedFingerprint = SavegameModSettings.CaptureHostFingerprint();
                fingerprintMatched = observedFingerprint == pending.Fingerprint;
                fingerprintToken = pending.Token;
                fingerprintCheckedAt = now;
            }
            return fingerprintMatched;
        }

        private static void Tick()
        {
            if (pending == null || loading) return;
            try
            {
                var lobby = owner?.activeLobby;
                if (lobby == null || lobby.id.m_SteamID != pending.Lobby || Roster(lobby) != pending.Roster)
                    throw new InvalidOperationException("Lobby or human roster changed.");
                if (SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID != hostId)
                    throw new InvalidOperationException("Lobby owner changed.");
                if (DateTime.UtcNow > deadline) throw new TimeoutException(TimeoutReason());
                var header = MapFileManager.Instance.GetHeaderFromMpSaveFileName(pending.SaveName);
                if (!Matches(header, pending)) throw new InvalidDataException("Local save no longer matches the prepared selection.");
                if (lobby.isHost)
                {
                    if (!FingerprintMatches(false))
                        throw new InvalidOperationException("Prepared host settings changed.");
                    if (released || !agreement.Ready) return;
                    if (!ModSettings.ModSettingsApplication.PrepareLaunch())
                    { waitingReason = UiText.Get("LobbyLaunch.MapConfigurationWaiting"); return; }
                    if (!FingerprintMatches(true))
                        throw new InvalidOperationException("Prepared host settings changed.");
                    if (!Economy.WorkerProductionAgreement.TryPrepareSaveAdmission(localSavePath, out bool productionWaiting, out string productionReason))
                    {
                        if (!productionWaiting) throw new InvalidOperationException(productionReason);
                        waitingReason = productionReason;
                        return;
                    }
                    RequirePreparedLaunchGuards(lobby);
                    released = true;
                    deadline = DateTime.UtcNow.AddSeconds(LobbyReadinessSupport.StartWindowSeconds);
                    if (Humans(lobby).Length > 1) Send(Reply(2));
                    NativeApiLog.Debug(log, "SAVE_SETTINGS_CONFIRMED: all current human peers ready; releasing Vanilla load once.");
                    original(owner, pending.SaveName);
                }
                else if (acknowledged)
                {
                    if (!FingerprintMatches(false)) throw new InvalidOperationException("Confirmed client settings changed.");
                }
                else if (FingerprintMatches(false) && ModSettings.ModSettingsApplication.PrepareLaunch() && FingerprintMatches(true))
                {
                    if (!Economy.WorkerProductionAgreement.TryPrepareSaveAdmission(localSavePath, out bool productionWaiting, out string productionReason))
                    {
                        if (!productionWaiting) throw new InvalidOperationException(productionReason);
                        waitingReason = productionReason;
                        return;
                    }
                    RequirePreparedLaunchGuards(lobby);
                    SavegameModSettings.AcceptPreparedClient(header.filePath, record);
                    var result = channel.SendToSteamPeer(Reply(1), SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID);
                    if (!result.Submitted) throw new InvalidOperationException(result.Reason);
                    acknowledged = true;
                }
            }
            catch (Exception error) { Abort(error.Message, phase: error is TimeoutException ? "timeout" : "preparation"); }
            finally
            {
                ScheduleWaitingCheck();
                AnnounceWaiting();
            }
        }

        private static void AnnounceWaiting()
        {
            if (pending == null || !attemptHost || released || loading || DateTime.UtcNow < waitNoticeAt) return;
            string reason = waitingReason ?? (agreement.Ready ? UiText.Get("LobbyLaunch.MapConfigurationWaiting") :
                UiText.Get("LobbyLaunch.MapAwaitingPeer", string.Join(", ", agreement.Unconfirmed.Select(LobbyReadinessSupport.PlayerName))));
            if (announcedWaiting == reason) return;
            announcedWaiting = reason;
            Lobby.LobbyLaunchStatus.ReportPreparationWaiting(log, reason,
                "SAVE_SETTINGS_WAITING role=host attempt=" + attempt, pending.Lobby, true, false);
        }

        private static void ScheduleWaitingCheck()
        {
            // Preserve every original preparation/roster/authorization check per frame.
            // Dormant and already loading preparations create no frame demand.
            frameWork?.SetContinuous(pending != null && !loading);
        }

        // A confirmed multiplayer save load is running: from ValidateStart until FinishLoad or Abort.
        // Written on Unity's main thread (Tick, readiness messages after Unity dispatch, the
        // HostLoadGame hook) and by the mission lifecycle's save entries (ValidateStart, FinishLoad).
        // VersionedSaveData reads it on the Extender's OnLoadMap thread, which is not proven to be
        // the same thread, so the read is volatile.
        internal static bool IsLoadingMultiplayerSave => System.Threading.Volatile.Read(ref loading);

        internal static void ValidateStart(string path, bool multiplayer)
        {
            if (!multiplayer) return;
            try
            {
                var header = MapFileManager.Instance.GetHeaderFromMpSaveFileName(Path.GetFileNameWithoutExtension(path));
                if (pending == null || !Matches(header, pending) ||
                    owner?.activeLobby?.id.m_SteamID != pending.Lobby || Roster(owner.activeLobby) != pending.Roster ||
                    SteamMatchmaking.GetLobbyOwner(owner.activeLobby.id).m_SteamID != hostId ||
                    !string.Equals(Path.GetFullPath(path), localSavePath, StringComparison.OrdinalIgnoreCase) ||
                    (owner.activeLobby.isHost ? !released : !acknowledged))
                    throw new InvalidOperationException("Multiplayer save has no confirmed settings preparation.");
                // Client saves can legitimately differ; compare this peer's own file with its earlier copy.
                if (HashLocalSave(localSavePath) != localSaveHash)
                    throw new InvalidDataException("Local save changed after readiness confirmation.");
                observedFingerprint = SavegameModSettings.CaptureHostFingerprint();
                if (observedFingerprint != pending.Fingerprint)
                    throw new InvalidOperationException("Multiplayer save settings changed before loading.");
                if (!ModSettings.ModSettingsApplication.PrepareLaunch())
                    throw new InvalidOperationException("Multiplayer save configuration is still being applied.");
                observedFingerprint = SavegameModSettings.CaptureHostFingerprint();
                if (observedFingerprint != pending.Fingerprint)
                    throw new InvalidOperationException("Multiplayer save settings changed before loading.");
                if (!Economy.WorkerProductionAgreement.TryPrepareSaveAdmission(localSavePath, out _, out string productionReason))
                    throw new InvalidOperationException(productionReason);
                RequirePreparedLaunchGuards(owner.activeLobby);
            }
            catch (Exception error)
            {
                // BeginLoad has not entered its mission state yet. Revoke this released
                // preparation now, including remote peers, instead of waiting for timeout.
                // Cleanup/reporting must never turn the rejection into a normal return.
                var lobby = Platform_Multiplayer.Instance?.activeLobby;
                // Vanilla retries the same published StartSave each frame when admission throws.
                // Those callbacks belong to one attempt. A new request/proposal clears this marker.
                if (pending == null && lobby != null && ReferenceEquals(lobby, rejectedNativeLobby) &&
                    string.Equals(path, rejectedNativePath, StringComparison.OrdinalIgnoreCase)) throw;
                rejectedNativeLobby = lobby;
                rejectedNativePath = path;
                try { Abort(error.Message, phase: "native-start"); }
                catch (Exception cleanup)
                {
                    try { NativeApiLog.Error(log, "Save launch rejection cleanup failed: " + cleanup); }
                    catch { }
                }
                throw;
            }
            loading = true;
            ScheduleWaitingCheck();
        }
        private static void RequirePreparedLaunchGuards(Platform_Multiplayer.MPLobby lobby)
        {
            // A previous Ready button does not admit a selected save: preparation can
            // apply different settings, and peer capability reports are not fingerprints.
            var decision = Lobby.LobbyLaunchGuards.EvaluatePreparedMultiplayerLoad(Humans(lobby).Length);
            if (decision.Blocked) throw new InvalidOperationException(decision.Reason);
        }
        internal static bool CancelSelection()
        {
            if (pending == null) return true;
            // Before commit, cancellation must invalidate even an identical
            // values fingerprint. After publication, retain admission until the
            // load completes; dialog cleanup must not revoke a committed load.
            if (released || loading) return false;
            Abort("Save selection cancelled.", notify: true, showError: false);
            return true;
        }
        private static void Abort(string reason, bool notify = true, bool showError = true, string phase = "preparation")
        {
            var lobby = owner?.activeLobby ?? Platform_Multiplayer.Instance?.activeLobby;
            ulong lobbyId = pending?.Lobby ?? lobby?.id.m_SteamID ?? 0;
            bool isHost = attempt != null ? attemptHost : lobby?.isHost == true;
            string attemptId = attempt ?? LobbyReadinessSupport.NewAttemptId();
            string comparison = " expected=" + (pending?.Fingerprint ?? "unavailable") +
                " observed=" + (observedFingerprint ?? "unavailable");
            if (phase == "timeout" && agreement != null)
                comparison += " unconfirmed=" + string.Join(",", agreement.Unconfirmed);
            bool relayed = false;
            if (pending != null)
            {
                if (notify && lobby?.id.m_SteamID == lobbyId && isHost == lobby.isHost)
                {
                    try
                    {
                        if (SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID == hostId)
                        {
                            if (isHost) Send(Reply(3, reason));
                            else
                            {
                                var result = channel.SendToSteamPeer(Reply(3, reason), hostId);
                                relayed = result.Submitted;
                                if (!relayed) NativeApiLog.Warning(log, "Save readiness cancellation could not reach the host: " + result.Reason);
                            }
                        }
                    }
                    catch (Exception error) { NativeApiLog.Warning(log, "Save readiness cancellation failed: " + error); }
                }
                retired.Add(pending.Token);
            }
            // Vanilla may still call StartSave for the start value already published for this attempt.
            if (showError && (released || acknowledged) && localSavePath != null && lobby?.id.m_SteamID == lobbyId)
            {
                rejectedNativeLobby = lobby;
                rejectedNativePath = localSavePath;
            }
            pending = null; agreement = null; record = null; owner = null;
            localSavePath = localSaveHash = null; localRecords = null;
            acknowledged = released = loading = false;
            attempt = observedFingerprint = waitingReason = announcedWaiting = null;
            try
            {
                frameWork?.CancelPending();
                frameWork?.SetContinuous(false);
                Economy.WorkerProductionAgreement.SetPreparedSavePath(null);
                SavegameModSettings.CancelLoadChoice();
            }
            finally
            {
                if (showError) ShowBlocked(reason, lobbyId, isHost, attemptId, phase, notify && isHost, notify && !relayed, comparison);
                else NativeApiLog.Debug(log, "SAVE_SETTINGS_PREPARATION_CANCELLED: " + reason);
            }
        }

        private static void ShowBlocked(string reason, Platform_Multiplayer.MPLobby lobby, string attemptId, string phase, bool broadcast) =>
            ShowBlocked(reason, lobby.id.m_SteamID, lobby.isHost, attemptId, phase, broadcast, !broadcast);

        private static void ShowBlocked(string reason, ulong lobbyId, bool isHost, string attemptId, string phase,
            bool broadcast, bool showLocal, string comparison = "")
        {
            Lobby.LobbyLaunchStatus.ReportPreparationBlocked(log, reason, "SAVE_SETTINGS_LOAD_BLOCKED attempt=" + attemptId +
                " role=" + (isHost ? "host" : "client") + " phase=" + phase + comparison, lobbyId, broadcast, showLocal);
            try { HUD_ConfirmationPopup.ShowConfirmationOKMessage(APIShared.Localization.UiText.Get("Savegames.LoadTitle"), () => { }, APIShared.Localization.UiText.Get("Savegames.LoadFailed") + reason); }
            catch (Exception error) { NativeApiLog.Error(log, "Could not display save preparation failure: " + error); }
        }
        // The host names the players that have not confirmed: an older or newer APIShared build drops
        // packets it cannot read, so a missing confirmation is the only sign of it on this side.
        private static string TimeoutReason()
        {
            if (released)
                return "The confirmed save load did not start within " + LobbyReadinessSupport.StartWindowSeconds + " seconds after its release.";
            if (waitingReason != null) return "Save settings preparation timed out: " + waitingReason;
            string waiting = owner?.activeLobby?.isHost == true && agreement != null
                ? string.Join(", ", agreement.Unconfirmed.Select(LobbyReadinessSupport.PlayerName)) : string.Empty;
            return waiting.Length == 0 ? "Save settings preparation timed out after 60 seconds." :
                "Save settings preparation timed out after 60 seconds without confirmation from " + waiting +
                ". Every player needs the same APIShared and mod versions and a copy of the selected save.";
        }
        internal static void FinishLoad()
        {
            if (pending != null) retired.Add(pending.Token);
            pending = null; agreement = null; record = null; owner = null;
            attempt = observedFingerprint = waitingReason = announcedWaiting = null;
            rejectedNativeLobby = null; rejectedNativePath = null;
            frameWork?.CancelPending();
            frameWork?.SetContinuous(false);
            localSavePath = localSaveHash = null; localRecords = null;
            Economy.WorkerProductionAgreement.SetPreparedSavePath(null);
            acknowledged = released = loading = false;
        }
    }
}
