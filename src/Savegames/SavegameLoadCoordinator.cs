using APIShared.Networking;
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
using System.Security.Cryptography;
using UnityEngine;

namespace APIShared
{
    // Owns the HostLoadGame entry, not FRONT_Multiplayer.Update or a plugin
    // callback. The static hook/channel/render publisher survive startup cleanup.
    internal static class SavegameLoadCoordinator
    {
        private delegate void HostLoad(Platform_Multiplayer self, string name);
        // Vanilla starts a released load asynchronously: HostLoadGame only publishes the lobby
        // "start" data. The readiness deadline must not abort that window; it gets its own bound.
        private const int StartWindowSeconds = 30;
        private static Hook hook;
        private static HostLoad original;
        private static MessageChannel<SavegameLoadPacket> channel;
        private static ManualLogSource log;
        private static SavegameLoadPacket pending;
        private static SavegameModSettingsRecord record;
        private static SavegameLoadAgreement agreement;
        private static Platform_Multiplayer owner;
        private static DateTime deadline;
        private static bool acknowledged, released, loading, initialized;
        private static ulong hostId;
        private static string localSavePath, localSaveHash;
        // This peer's digests of the declared simulation records in its own copy of the selected save.
        private static IReadOnlyList<SavegameRecordDigest> localRecords;
        private static readonly Queue<string> retiredOrder = new Queue<string>();
        private static readonly HashSet<string> retired = new HashSet<string>(StringComparer.Ordinal);

        internal static void Initialize(ManualLogSource logger)
        {
            if (initialized) return;
            log = logger;
            // 64 KiB provenance plus at most 64 record digests (owner, identifier, SHA-256) and a reason.
            channel = ApiShared.ForMod(APISharedPlugin.PluginGuid).Network.RegisterMessage<SavegameLoadPacket>(
                "SavegameLoadReadiness", Receive, new MessageChannelOptions<SavegameLoadPacket> { MaximumBodyBytes = 200000 });
            Hook candidate = null;
            try
            {
                candidate = new Hook(typeof(Platform_Multiplayer).GetMethod(nameof(Platform_Multiplayer.HostLoadGame)),
                    (HostLoad)Request, new HookConfig { ManualApply = true });
                original = candidate.GenerateTrampoline<HostLoad>();
                candidate.Apply();
                hook = candidate;
                Application.onBeforeRender += Tick;
                initialized = true;
                NativeApiLog.Debug(log, "SAVE_SETTINGS_PREPARATION_READY: permanent host-load gate installed after startup cleanup.");
            }
            catch { if (hook == null) candidate?.Dispose(); throw; }
        }

        private static ulong[] Humans(Platform_Multiplayer.MPLobby lobby)
        {
            if (lobby?.members == null) throw new InvalidOperationException("Lobby roster unavailable.");
            if (lobby.members.Any(x => x == null || x.dummyToBeKicked)) throw new InvalidOperationException("Lobby roster is changing.");
            var result = lobby.members.Where(x => !x.SkirmishMember && x.id.m_SteamID > 1000)
                .Select(x => x.id.m_SteamID).OrderBy(x => x).ToArray();
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
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
        private static string Name(ulong steamId)
        {
            try
            {
                string name = SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            return steamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void Request(Platform_Multiplayer self, string name)
        {
            if (self.activeLobby?.isHost != true) { original(self, name); return; }
            if (pending != null)
            {
                if (pending.SaveName == name) return; // Repeated button callback, not another load.
                if (released || loading) return; // A published Vanilla start cannot be replaced by another dialog.
                Abort("Selected save changed.");
            }
            try
            {
                var header = MapFileManager.Instance.GetHeaderFromMpSaveFileName(name);
                if (header == null) throw new InvalidDataException("Selected multiplayer save is unavailable.");
                owner = self;
                localSavePath = Path.GetFullPath(header.filePath);
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
                    Token = Guid.NewGuid().ToString("N"), SaveName = header.fileName,
                    Checksum = header.xPlaySaveChecksum, Time = header.xPlaySaveTime,
                    Fingerprint = fingerprint, Roster = Roster(self.activeLobby),
                    Metadata = record == null ? null : MessagePackSerializer.Serialize(record), Records = records };
                agreement = new SavegameLoadAgreement(pending.Token, fingerprint, humans.Where(x => x != local), records);
                deadline = DateTime.UtcNow.AddSeconds(60);
                acknowledged = false; released = false; loading = false;
                SavegameModSettings.PublishPreparedHostValues();
                if (!agreement.Ready) Send(pending);
                Tick(); // Solo host: no network echo, no artificial delay.
            }
            catch (Exception error) { Abort(error.Message); }
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
            Reason = reason == null || reason.Length <= SavegameLoadPacket.MaximumReasonLength
                ? reason : reason.Substring(0, SavegameLoadPacket.MaximumReasonLength)
        };

        // A peer on another readiness protocol cannot take part. A protocol-1 peer still understands a
        // protocol-1 cancel, so it is told; the load is blocked with a message naming the versions.
        private static void RejectProtocol(SavegameLoadPacket packet, ulong sender, Platform_Multiplayer.MPLobby lobby)
        {
            if (!Humans(lobby).Contains(sender) || packet.Token != null && retired.Contains(packet.Token)) return;
            if (packet.Token != null) Retire(packet.Token); // The peer's own cancel echo is not reported twice.
            string text = Name(sender) + " uses save readiness protocol " + packet.Version + " and this APIShared build uses " +
                SavegameLoadPacket.CurrentVersion + ". Every player needs the same APIShared version.";
            if (packet.Version == SavegameLoadPacket.LegacyVersion && packet.Lobby == lobby.id.m_SteamID && packet.Token?.Length == 32)
            {
                var cancel = new SavegameLoadPacket { Version = SavegameLoadPacket.LegacyVersion, Operation = 3, Lobby = packet.Lobby,
                    Token = packet.Token, SaveName = packet.SaveName, Checksum = packet.Checksum, Time = packet.Time,
                    Fingerprint = packet.Fingerprint, Roster = packet.Roster };
                var result = channel.SendToSteamPeer(cancel, sender);
                if (!result.Submitted) NativeApiLog.Error(log, "Could not cancel the protocol-1 peer's preparation: " + result.Reason);
            }
            if (pending != null) { Abort(text); return; }
            ShowBlocked(text);
        }

        private static void Receive(NetworkMessage<SavegameLoadPacket> message)
        {
            try { ReceiveCore(message); }
            catch (Exception error) { Abort(error.Message); }
        }

        private static void ReceiveCore(NetworkMessage<SavegameLoadPacket> message)
        {
            var packet = message.Packet;
            var mp = Platform_Multiplayer.Instance;
            var lobby = mp?.activeLobby;
            if (packet == null || lobby == null) return;
            if (packet.Version != SavegameLoadPacket.CurrentVersion) { RejectProtocol(packet, message.SenderSteamId, lobby); return; }
            if (packet.Lobby != lobby.id.m_SteamID ||
                packet.Operation < 0 || packet.Operation > 3 || packet.Token?.Length != 32 ||
                !Guid.TryParseExact(packet.Token, "N", out _) || packet.SaveName?.Length > 1024 ||
                string.IsNullOrEmpty(packet.SaveName) || packet.Fingerprint?.Length != 64 ||
                packet.Roster != Roster(lobby)) return;
            ulong host = SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID;
            if (lobby.isHost)
            {
                // The host compares every peer's declared simulation records with its own; a difference
                // ends the preparation for everyone with the explanation.
                if (packet.Operation == 1 && pending != null && agreement != null && packet.SaveName == pending.SaveName &&
                    packet.Checksum == pending.Checksum && packet.Time == pending.Time &&
                    !agreement.Accept(message.SenderSteamId, packet.Token, packet.Fingerprint, packet.Records,
                        Name(message.SenderSteamId), out string mismatch) && mismatch != null)
                { Abort(mismatch); return; }
                if (packet.Operation == 3 && pending?.Token == packet.Token && Humans(lobby).Contains(message.SenderSteamId))
                {
                    Abort(string.IsNullOrEmpty(packet.Reason) ? "A participant cancelled save preparation." :
                        Name(message.SenderSteamId) + " cancelled save preparation: " + packet.Reason);
                    return;
                }
                Tick(); return;
            }
            if (message.SenderSteamId != host || retired.Contains(packet.Token)) return;
            if (packet.Operation == 0)
            {
                if (pending?.Token == packet.Token) { Tick(); return; }
                if (pending != null) Abort("Host replaced the save preparation.", false, false);
                if (packet.Metadata != null && !SavegameModSettings.TryDeserialize(packet.Metadata, out record))
                    throw new InvalidDataException("Invalid host save provenance.");
                if (packet.Metadata == null) record = null;
                if (record != null && record.Mods.Count != 0) throw new InvalidDataException("Readiness must not carry settings values.");
                if (!SavegameRecordComparison.IsValid(packet.Records)) throw new InvalidDataException("Invalid host save record list.");
                pending = packet; owner = mp; agreement = null; acknowledged = false; released = false; loading = false;
                hostId = host;
                var header = MapFileManager.Instance.GetHeaderFromMpSaveFileName(packet.SaveName);
                if (!Matches(header, packet)) { Abort("Local save does not match host selection."); return; }
                localSavePath = Path.GetFullPath(header.filePath);
                localSaveHash = HashLocalSave(localSavePath);
                // Taken from this peer's own copy of the selected save, never from earlier in-memory
                // state, so a previous single-player session or different local settings do not count.
                localRecords = VersionedSaveDataRuntime.CaptureSimulationDigests(localSavePath);
                deadline = DateTime.UtcNow.AddSeconds(60);
                Tick();
            }
            else if (pending?.Token == packet.Token && packet.Operation == 3)
                Abort(string.IsNullOrEmpty(packet.Reason) ? "Host cancelled the save preparation." :
                    "Host cancelled the save preparation: " + packet.Reason, false);
            else if (pending?.Token == packet.Token && packet.Operation == 2)
            {
                if (!released) deadline = DateTime.UtcNow.AddSeconds(StartWindowSeconds);
                released = true;
            }
        }

        // Serializing and hashing every host setting each frame is too costly while peers
        // prepare. Waiting frames recheck at most twice per second; a release or
        // acknowledgement always compares a fresh fingerprint, as does ValidateStart.
        private static string fingerprintToken;
        private static long fingerprintCheckedAt;
        private static bool fingerprintMatched;
        private static bool FingerprintMatches(bool decisive)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (decisive || fingerprintToken != pending.Token || now - fingerprintCheckedAt >= System.Diagnostics.Stopwatch.Frequency / 2)
            {
                fingerprintMatched = SavegameModSettings.CaptureHostFingerprint() == pending.Fingerprint;
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
                    if (!ModSettings.ModSettingsApplication.PrepareLaunch()) return;
                    if (!FingerprintMatches(true))
                        throw new InvalidOperationException("Prepared host settings changed.");
                    released = true;
                    deadline = DateTime.UtcNow.AddSeconds(StartWindowSeconds);
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
                    SavegameModSettings.AcceptPreparedClient(header.filePath, record);
                    var result = channel.SendToSteamPeer(Reply(1), SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID);
                    if (!result.Submitted) throw new InvalidOperationException(result.Reason);
                    acknowledged = true;
                }
            }
            catch (Exception error) { Abort(error.Message); }
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
            var header = MapFileManager.Instance.GetHeaderFromMpSaveFileName(Path.GetFileNameWithoutExtension(path));
            if (pending == null || !Matches(header, pending) ||
                owner?.activeLobby?.id.m_SteamID != pending.Lobby || Roster(owner.activeLobby) != pending.Roster ||
                SteamMatchmaking.GetLobbyOwner(owner.activeLobby.id).m_SteamID != hostId ||
                !string.Equals(Path.GetFullPath(path), localSavePath, StringComparison.OrdinalIgnoreCase) ||
                (owner.activeLobby.isHost ? !released : !acknowledged))
                throw new InvalidOperationException("Multiplayer save has no confirmed settings preparation.");
            // This compares each peer's file with its own earlier file, never
            // with the host's whole-file hash (client saves can legitimately differ).
            if (HashLocalSave(localSavePath) != localSaveHash)
                throw new InvalidDataException("Local save changed after readiness confirmation.");
            if (SavegameModSettings.CaptureHostFingerprint() != pending.Fingerprint || !ModSettings.ModSettingsApplication.PrepareLaunch())
                throw new InvalidOperationException("Multiplayer save settings changed before loading.");
            loading = true;
        }
        private static void Retire(string token)
        {
            if (!retired.Add(token)) return;
            retiredOrder.Enqueue(token);
            if (retiredOrder.Count > 64) retired.Remove(retiredOrder.Dequeue());
        }
        internal static bool CancelSelection()
        {
            if (pending == null) return true;
            // Before commit, cancellation must invalidate even an identical
            // values fingerprint. After publication, retain admission until the
            // load completes; dialog cleanup must not revoke a committed load.
            if (released || loading) return false;
            Abort("Save selection cancelled.", true, false);
            return true;
        }
        private static void Abort(string reason, bool notify = true, bool showError = true)
        {
            if (pending != null)
            {
                if (notify && owner?.activeLobby?.isHost == true)
                    try { Send(Reply(3, reason)); } catch (Exception error) { NativeApiLog.Error(log, error.ToString()); }
                else if (notify && owner?.activeLobby != null)
                    try { channel.SendToSteamPeer(Reply(3, reason), hostId); } catch (Exception error) { NativeApiLog.Error(log, error.ToString()); }
                Retire(pending.Token);
            }
            pending = null; agreement = null; record = null; owner = null;
            localSavePath = localSaveHash = null; localRecords = null;
            acknowledged = released = loading = false;
            SavegameModSettings.CancelLoadChoice();
            if (!showError) { NativeApiLog.Debug(log, "SAVE_SETTINGS_PREPARATION_CANCELLED: " + reason); return; }
            ShowBlocked(reason);
        }
        private static void ShowBlocked(string reason)
        {
            NativeApiLog.Error(log, "SAVE_SETTINGS_LOAD_BLOCKED: " + reason);
            try { HUD_ConfirmationPopup.ShowConfirmationOKMessage(APIShared.Localization.UiText.Get("Savegames.LoadTitle"), () => { }, APIShared.Localization.UiText.Get("Savegames.LoadFailed") + reason); }
            catch (Exception error) { NativeApiLog.Error(log, "Could not display save preparation failure: " + error); }
        }
        // The host names the players that have not confirmed: an older or newer APIShared build drops
        // packets it cannot read, so a missing confirmation is the only sign of it on this side.
        private static string TimeoutReason()
        {
            if (released)
                return "The confirmed save load did not start within " + StartWindowSeconds + " seconds after its release.";
            string waiting = owner?.activeLobby?.isHost == true && agreement != null
                ? string.Join(", ", agreement.Unconfirmed.Select(Name)) : string.Empty;
            return waiting.Length == 0 ? "Save settings preparation timed out after 60 seconds." :
                "Save settings preparation timed out after 60 seconds without confirmation from " + waiting +
                ". Every player needs the same APIShared and mod versions and a copy of the selected save.";
        }
        internal static void FinishLoad()
        {
            if (pending != null) Retire(pending.Token);
            pending = null; agreement = null; record = null; owner = null;
            localSavePath = localSaveHash = null; localRecords = null;
            acknowledged = released = loading = false;
        }
    }
}
