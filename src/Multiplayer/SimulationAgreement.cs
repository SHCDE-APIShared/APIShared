using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using APIShared.Internal;
using APIShared.Lobby;
using APIShared.Networking;
using BepInEx.Logging;
using MessagePack;
using MessagePack.Formatters;
using Steamworks;

namespace APIShared.Multiplayer
{
    /// <summary>Agrees on native simulation feature readiness before a multiplayer world runs.</summary>
    public static class SimulationAgreement
    {
        private sealed class Feature
        {
            internal string Key;
            internal bool Required;
            internal Func<bool> Ready;
            internal Action<bool> Apply;
        }

        internal sealed class Receipt
        {
            internal string Enabled, Roster;
            internal ulong[] Peers;
            internal IReadOnlyDictionary<int, ulong> LobbyPlayers;
            internal ulong Lobby, Host, Local;
            internal long Round;
        }

        internal sealed class Attempt
        {
            internal Receipt Decision;
            internal long Epoch, Session;
            internal int Faulted;
            internal bool ResyncCohortEnabled;
            internal AdmittedResyncWorld ResyncWorld;
        }

        internal sealed class AdmittedResyncWorld
        {
            internal AdmittedResyncWorld(Attempt attempt, Dictionary<int, ulong> players, string digest)
            {
                var decision = attempt.Decision;
                Lobby = decision.Lobby;
                Round = decision.Round;
                Epoch = attempt.Epoch;
                Host = decision.Host;
                Local = decision.Local;
                Peers = new ReadOnlyDictionary<int, ulong>(players);
                HostPlayerId = players.Single(peer => peer.Value == Host).Key;
                LocalPlayerId = players.Single(peer => peer.Value == Local).Key;
                Session = attempt.Session;
                ManifestDigest = digest;
            }

            internal ulong Lobby { get; }
            internal long Round { get; }
            internal long Epoch { get; }
            internal ulong Host { get; }
            internal ulong Local { get; }
            internal IReadOnlyDictionary<int, ulong> Peers { get; }
            internal int HostPlayerId { get; }
            internal int LocalPlayerId { get; }
            internal long Session { get; }
            internal string ManifestDigest { get; }
        }

        private static readonly List<Feature> Features = new List<Feature>();
        private static readonly Dictionary<ulong, string> Reports = new Dictionary<ulong, string>();
        private static readonly HashSet<ulong> Acknowledged = new HashSet<ulong>();
        private static LobbyStateSnapshot roster;
        private static MessageChannel<SimulationAgreementPacket> channel;
        private static ManualLogSource log;
        private static string rosterKey = "", localManifest = "";
        private static ulong host, self;
        private static long round, nextRound, consumedRound;
        private static long retryAfter;
        private static bool rejectedDecision;
        private static Receipt proposal, accepted;
        private static Attempt world;
        private static bool manifestCaptured;
        private static Feature resyncCohort;
        private static Func<string> freezeResyncManifest;
        private static string resyncDigest;

        /// <summary>
        /// Registers a process-lifetime feature on the Unity thread during plug-in startup.
        /// A required feature rejects unequal readiness reports. An optional feature enables only
        /// when all peers are ready. Distribute the same owner and feature identifiers to all peers.
        /// The readiness callback must be side-effect free. The synchronous apply callback runs at
        /// native admission or retirement and must only publish the supplied logical activation.
        /// </summary>
        public static void Register(string ownerGuid, string featureId, bool required, Func<bool> isReady, Action<bool> apply)
        {
            if (!UnityMainThreadDispatch.IsMainThread)
                throw new InvalidOperationException("Simulation agreement registration requires the Unity thread.");
            if (!ValidId(ownerGuid) || !ValidId(featureId)) throw new ArgumentException("Invalid simulation feature identity.");
            if (isReady == null) throw new ArgumentNullException(nameof(isReady));
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            string key = ownerGuid + "/" + featureId;
            if (Features.Count == 32 || Features.Any(item => item.Key == key) ||
                key.StartsWith(SimulationAgreementPolicy.ResyncCohortKeyPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Simulation feature registration refused: " + key);
            Features.Add(new Feature { Key = key, Required = required, Ready = isReady, Apply = apply });
            Features.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        }

        internal static bool TryRegisterResyncCohort(Func<string> freezeManifestDigest, Func<bool> isReady,
            Action<bool> apply, out string reason)
        {
            if (!UnityMainThreadDispatch.IsMainThread || freezeManifestDigest == null || isReady == null || apply == null)
            {
                reason = "Resync cohort registration requires the Unity thread and all callbacks.";
                return false;
            }
            if (manifestCaptured || resyncCohort != null || Features.Count == 32)
            {
                reason = "The simulation agreement cannot admit another resync cohort.";
                return false;
            }
            resyncCohort = new Feature
            {
                Key = SimulationAgreementPolicy.ResyncCohortKeyPrefix,
                Required = true,
                Ready = () => resyncDigest != null && isReady(),
                Apply = apply
            };
            freezeResyncManifest = freezeManifestDigest;
            Features.Add(resyncCohort);
            Features.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
            reason = null;
            return true;
        }

        /// <summary>
        /// Stops the active agreed world using the terminal engine boundary and notifies the
        /// roster. Safe from the simulation thread. Stale notifications cannot stop a replacement world.
        /// </summary>
        public static void ReportFault(string message)
        {
            var active = Volatile.Read(ref world);
            var context = MissionLifecycleService.ActiveContext;
            if (active == null || context?.SessionId != active.Session ||
                Interlocked.Exchange(ref active.Faulted, 1) != 0) return;

            UnityMainThreadDispatch.TryEnqueue(() =>
            {
                var receipt = active.Decision;
                if (ReferenceEquals(active, Volatile.Read(ref world)))
                    foreach (ulong peer in receipt.Peers)
                        if (peer != receipt.Local) Send(peer, SimulationAgreementPacket.Fault, receipt.Round,
                            active.Epoch.ToString(CultureInfo.InvariantCulture), receipt);
            });
            if (ReferenceEquals(active, Volatile.Read(ref world)))
                Economy.WorkerProductionWorldSafety.Fault(message ?? "Multiplayer simulation capability failed.");
        }

        internal static void Initialize(ManualLogSource logger)
        {
            log = logger;
            channel = ApiShared.ForMod(APISharedPlugin.PluginGuid).Network.RegisterMessage<SimulationAgreementPacket>(
                "simulation-capability-agreement", Receive, new MessageChannelOptions<SimulationAgreementPacket> { MaximumBodyBytes = 8192 });
            if (!LobbyLaunchGuards.TryRegisterReadiness(APISharedPlugin.PluginGuid, "simulation-capability-agreement", Guard, out string error))
                throw new InvalidOperationException(error);
            if (!ApiShared.Current.TryGetLobbyState(APISharedPlugin.PluginGuid, out var state, out var diagnostic) ||
                !state.TryRegisterObserver("simulation-capability-agreement", Observe, out diagnostic))
                throw new InvalidOperationException(diagnostic?.Reason ?? "Simulation capability lobby state unavailable.");
            if (!ApiShared.Current.TryGetMissionLifecycle(APISharedPlugin.PluginGuid, out var lifecycle, out diagnostic) ||
                !lifecycle.TryRegisterObserver("simulation-capability-agreement", null,
                    note => Retire(note.Context.SessionId), null, out diagnostic))
                throw new InvalidOperationException(diagnostic?.Reason ?? "Simulation capability mission lifecycle unavailable.");
        }

        private static bool ValidId(string value) => !string.IsNullOrEmpty(value) && value.Length <= 96 &&
            value.All(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.');

        private static string CaptureManifest() => string.Join(";", Features.Select(item =>
            item.Key + "=" + (item.Required ? "r" : "o") + (item.Ready() ? "1" : "0")));

        // Freeze only when a resolved multiplayer roster can actually capture a manifest.
        private static void FreezeResyncCohort()
        {
            if (manifestCaptured) return;
            manifestCaptured = true;
            if (resyncCohort == null) return;
            try
            {
                string digest = freezeResyncManifest();
                if (!SimulationAgreementPolicy.IsResyncDigest(digest))
                    throw new InvalidOperationException("Invalid resync cohort manifest digest.");
                resyncDigest = digest;
            }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "Could not freeze the multiplayer resync manifest: " + error);
            }
            resyncCohort.Key += resyncDigest ?? new string('0', 64);
            freezeResyncManifest = null;
        }

        private static void Observe(LobbyStateSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.LobbyId.HasValue && snapshot.PreserveForMapTransition) return;
            roster = snapshot;
            Refresh();
        }

        private static void Refresh()
        {
            if (channel == null) return;
            ulong currentHost = 0, currentSelf = 0;
            string key = "";
            if (roster?.LobbyId != null && !roster.HasUnresolvedPlayers && roster.Error.Length == 0 &&
                roster.Players.TryGetValue(roster.LocalPlayerId, out currentSelf))
            {
                currentHost = SteamMatchmaking.GetLobbyOwner(new CSteamID(roster.LobbyId.Value)).m_SteamID;
                if (roster.Players.Values.Contains(currentHost))
                    key = string.Join(",", roster.Players.OrderBy(item => item.Key).Select(item =>
                        item.Key.ToString(CultureInfo.InvariantCulture) + ":" + item.Value.ToString(CultureInfo.InvariantCulture)));
            }
            if (key.Length != 0) FreezeResyncCohort();
            if (Features.Count == 0) return;
            string manifest = key.Length != 0 || resyncCohort == null ? CaptureManifest() : "";
            bool rosterChanged = key != rosterKey || currentHost != host || currentSelf != self ||
                roster?.LobbyId != observedLobby;
            if (rosterChanged || manifest != localManifest)
            {
                if (rosterChanged) round = 0;
                observedLobby = roster?.LobbyId;
                rosterKey = key;
                host = currentHost;
                self = currentSelf;
                localManifest = manifest;
                proposal = accepted = null;
                rejectedDecision = false;
                Reports.Clear();
                Acknowledged.Clear();
                if (key.Length != 0 && host == self) BeginRound();
                else if (key.Length != 0) Send(host, SimulationAgreementPacket.Hello, 0, "");
            }
        }

        private static ulong? observedLobby;

        private static void BeginRound()
        {
            round = Math.Max(nextRound + 1, DateTime.UtcNow.Ticks);
            nextRound = round;
            proposal = accepted = null;
            Reports.Clear();
            Acknowledged.Clear();
            Reports[self] = localManifest;
            DeferRetry();
            foreach (ulong peer in roster.Players.Values)
                if (peer != self) Send(peer, SimulationAgreementPacket.Challenge, round, "");
            if (roster.Players.Count == 1) TryPropose(false);
        }

        private static LobbyLaunchGuardResult Guard(LobbyLaunchGuardContext context)
        {
            if (!context.RequiresPeerAgreement) return default;
            if (context.Operation == LobbyLaunchOperation.Ready) { Refresh(); return default; }
            return EvaluateStart();
        }

        // File acknowledgement on the client itself requires the simulation decision.
        // Drive the existing exchange while the host still awaits those file acknowledgements.
        internal static LobbyLaunchGuardResult AdvancePreparedSaveExchange(ulong lobbyId)
        {
            if (Features.Count == 0) return default;
            if (roster?.LobbyId != lobbyId)
                return Waiting("Simulation capability agreement is waiting for the multiplayer roster.");
            return EvaluateStart(allowMissingReports: false);
        }

        private static LobbyLaunchGuardResult EvaluateStart(bool allowMissingReports = true)
        {
            if (Features.Count == 0) return default;
            Refresh();
            if (rosterKey.Length == 0) return Waiting("Simulation capability agreement is waiting for the multiplayer roster.");
            if (host != self)
            {
                if (accepted != null && accepted.Round != consumedRound) return default;
                return rejectedDecision
                    ? new LobbyLaunchGuardResult(LobbyLaunchGuardState.Rejected, "The host's simulation capability decision differs from the local capabilities.")
                    : Waiting("Waiting for the host's simulation capability agreement.");
            }

            if (round == 0 || round == consumedRound) BeginRound();
            var result = TryPropose(allowMissingReports);
            RetryOutstanding();
            if (proposal == null) return result;
            if (Acknowledged.Count != proposal.Peers.Length)
                return Waiting("Waiting for every player to acknowledge the simulation capability agreement.");
            accepted = proposal;
            return default;
        }

        private static LobbyLaunchGuardResult Waiting(string reason) =>
            new LobbyLaunchGuardResult(LobbyLaunchGuardState.Waiting, reason);

        private static void DeferRetry() => retryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency;

        private static void RetryOutstanding()
        {
            if (Stopwatch.GetTimestamp() < retryAfter) return;
            DeferRetry();
            foreach (ulong peer in roster.Players.Values)
            {
                if (!Reports.ContainsKey(peer)) Send(peer, SimulationAgreementPacket.Challenge, round, "");
                if (proposal != null && !Acknowledged.Contains(peer))
                    Send(peer, SimulationAgreementPacket.Decision, round, proposal.Enabled);
            }
        }

        private static LobbyLaunchGuardResult TryPropose(bool allowMissing)
        {
            if (proposal != null) return default;
            if (!SimulationAgreementPolicy.TryDecide(localManifest, roster.Players.Values.Select(peer =>
                Reports.TryGetValue(peer, out string manifest) ? manifest : null), out string enabled, out string reason, out bool waiting))
                return new LobbyLaunchGuardResult(waiting ? LobbyLaunchGuardState.Waiting : LobbyLaunchGuardState.Rejected, reason);
            if (!allowMissing && Reports.Count != roster.Players.Count)
                return Waiting("Waiting for all players to report simulation capabilities.");
            proposal = CreateReceipt(enabled);
            Acknowledged.Add(self);
            DeferRetry();
            foreach (ulong peer in proposal.Peers)
                if (peer != self) Send(peer, SimulationAgreementPacket.Decision, round, enabled);
            return default;
        }

        private static void Receive(NetworkMessage<SimulationAgreementPacket> message)
        {
            var packet = message.Packet;
            if (packet == null || Features.Count == 0) return;
            if (packet.Operation == SimulationAgreementPacket.Fault)
            {
                var active = Volatile.Read(ref world);
                var receipt = active?.Decision;
                if (receipt != null && packet.Lobby == receipt.Lobby && packet.Round == receipt.Round &&
                    packet.Roster == receipt.Roster && receipt.Peers.Contains(message.SenderSteamId) &&
                    packet.Data == active.Epoch.ToString(CultureInfo.InvariantCulture) &&
                    MissionLifecycleService.ActiveContext?.SessionId == active.Session &&
                    Interlocked.Exchange(ref active.Faulted, 1) == 0)
                    Economy.WorkerProductionWorldSafety.Fault("A multiplayer peer reported a simulation capability fault.");
                return;
            }

            Refresh();
            if (rosterKey.Length == 0 || packet.Lobby != observedLobby || packet.Roster != rosterKey ||
                !roster.Players.Values.Contains(message.SenderSteamId)) return;
            if (host == self)
            {
                if (message.SenderSteamId == self) return;
                if (packet.Operation == SimulationAgreementPacket.Hello)
                {
                    if (proposal != null) BeginRound();
                    else Send(message.SenderSteamId, SimulationAgreementPacket.Challenge, round, "");
                }
                else if (packet.Round == round && packet.Operation == SimulationAgreementPacket.Report)
                {
                    if (proposal != null && Reports.TryGetValue(message.SenderSteamId, out string previous) &&
                        previous != packet.Data)
                    {
                        BeginRound();
                        return;
                    }
                    Reports[message.SenderSteamId] = packet.Data;
                    TryPropose(false);
                }
                else if (packet.Round == round && packet.Operation == SimulationAgreementPacket.Acknowledge &&
                         proposal != null && proposal.Enabled == packet.Data)
                {
                    Acknowledged.Add(message.SenderSteamId);
                    if (Acknowledged.Count == proposal.Peers.Length) accepted = proposal;
                }
                return;
            }

            if (message.SenderSteamId != host || packet.Round < round) return;
            if (packet.Operation == SimulationAgreementPacket.Challenge)
            {
                if (packet.Round > round) { accepted = null; rejectedDecision = false; }
                round = packet.Round;
                Send(host, SimulationAgreementPacket.Report, round, localManifest);
            }
            else if (packet.Operation == SimulationAgreementPacket.Decision && packet.Round == round)
            {
                if (accepted != null && accepted.Enabled != packet.Data) return;
                if (!SimulationAgreementPolicy.Accepts(localManifest, packet.Data))
                { rejectedDecision = true; return; }
                rejectedDecision = false;
                if (Send(host, SimulationAgreementPacket.Acknowledge, round, packet.Data))
                    accepted = CreateReceipt(packet.Data);
            }
        }

        private static Receipt CreateReceipt(string enabled) => new Receipt
        {
            Lobby = roster.LobbyId.Value, Host = host, Local = self, Roster = rosterKey, Round = round,
            Enabled = enabled, LobbyPlayers = roster.Players, Peers = roster.Players.Values.ToArray()
        };

        private static bool Send(ulong peer, int operation, long epoch, string data, Receipt receipt = null)
        {
            ulong lobby = receipt?.Lobby ?? observedLobby.GetValueOrDefault();
            if (lobby == 0 || epoch < 0) return false;
            var result = channel.SendToSteamPeer(new SimulationAgreementPacket
            {
                Lobby = lobby, Roster = receipt?.Roster ?? rosterKey, Round = epoch, Operation = operation, Data = data
            }, peer);
            return result.Submitted;
        }

        internal static Attempt PrepareAttempt(long? restartedFrom)
        {
            var previous = Volatile.Read(ref world);
            var decision = restartedFrom.HasValue
                ? previous != null && restartedFrom == previous.Session && Volatile.Read(ref previous.Faulted) == 0
                    ? previous.Decision : null
                : accepted?.Round == consumedRound ? null : accepted;
            return decision == null ? null : new Attempt
            {
                Decision = decision, Epoch = restartedFrom.HasValue ? previous.Epoch + 1 : 1
            };
        }

        internal static string Admit(MissionContext context, Attempt attempt)
        {
            if (Features.Count == 0) return null;
            if (!context.Mode.IsRealMultiplayer) { Retire(); return null; }
            Receipt decision = attempt?.Decision;
            if (decision == null)
                return "The multiplayer simulation capability agreement is missing or has changed.";
            if (decision.Enabled.Length != 0 && !Economy.WorkerProductionWorldSafety.IsAvailable)
                return "The multiplayer simulation safety boundary is unavailable.";
            try
            {
                if (!SimulationAgreementPolicy.Accepts(CaptureManifest(), decision.Enabled))
                    return "Local simulation capability readiness changed after the multiplayer agreement.";
                var enabled = new HashSet<string>(decision.Enabled.Split(';'), StringComparer.Ordinal);
                foreach (var feature in Features) feature.Apply(enabled.Contains(feature.Key));
                attempt.ResyncCohortEnabled = resyncCohort != null && enabled.Contains(resyncCohort.Key);
            }
            catch (Exception error)
            {
                Retire();
                return "Multiplayer simulation capability activation failed: " + error.Message;
            }
            attempt.Session = context.SessionId;
            Volatile.Write(ref world, attempt);
            consumedRound = decision.Round;
            return null;
        }

        // Native callbacks read a managed publication only. The cohort is already required
        // during the window before Vanilla makes the final slot mapping visible on Unity.
        internal static bool IsResyncCohortAdmitted => Volatile.Read(ref world)?.ResyncCohortEnabled == true;

        internal static AdmittedResyncWorld GetAdmittedResyncWorld()
        {
            var active = Volatile.Read(ref world);
            if (active == null || Volatile.Read(ref active.Faulted) != 0) return null;
            return Volatile.Read(ref active.ResyncWorld);
        }

        internal static bool IsAdmittedResyncWorldCurrent(AdmittedResyncWorld snapshot) =>
            snapshot != null && ReferenceEquals(snapshot, GetAdmittedResyncWorld());

        internal static AdmittedResyncWorld TryBindAdmittedResyncWorld()
        {
            if (!UnityMainThreadDispatch.IsMainThread) return null;
            var active = Volatile.Read(ref world);
            if (active == null || !active.ResyncCohortEnabled || Volatile.Read(ref active.Faulted) != 0) return null;
            var bound = Volatile.Read(ref active.ResyncWorld);
            if (bound != null) return bound;

            // A loaded save can change native player slots after admission. Bind the Steam
            // identities from the receipt to Vanilla's active game roster only after remapping.
            Platform_Multiplayer platform = Platform_Multiplayer.Instance;
            if (!HasLiveResyncRoster(platform) ||
                !PlayerIdentityHelper.TryCaptureHumanRoster(out var players, out _) ||
                players.Count != active.Decision.LobbyPlayers.Count) return null;
            foreach (ulong steamId in players.Values)
                if (!active.Decision.Peers.Contains(steamId)) return null;
            var candidate = new AdmittedResyncWorld(active, players, resyncDigest);
            if (!MatchesLiveResyncRoster(platform, candidate) ||
                !ReferenceEquals(active, Volatile.Read(ref world)) || Volatile.Read(ref active.Faulted) != 0) return null;
            Volatile.Write(ref active.ResyncWorld, candidate);
            return IsAdmittedResyncWorldCurrent(candidate) ? candidate : null;
        }

        internal static bool VerifyLiveResyncRoster(AdmittedResyncWorld snapshot)
        {
            if (!UnityMainThreadDispatch.IsMainThread || !IsAdmittedResyncWorldCurrent(snapshot)) return false;
            Platform_Multiplayer platform = Platform_Multiplayer.Instance;
            return HasLiveResyncRoster(platform) && MatchesLiveResyncRoster(platform, snapshot);
        }

        private static bool HasLiveResyncRoster(Platform_Multiplayer platform) =>
            Platform_Multiplayer.MPGameActive && platform != null &&
            platform.GetActiveLobby() == null && platform.gameMembers != null;

        private static bool MatchesLiveResyncRoster(Platform_Multiplayer platform, AdmittedResyncWorld snapshot)
        {
            if (platform.IsHost != (snapshot.Local == snapshot.Host)) return false;
            int seenSlots = 0, matched = 0;
            foreach (var member in platform.gameMembers)
            {
                if (member == null || member.kicked || member.skirmishAI) continue;
                if (!snapshot.Peers.TryGetValue(member.playerID, out ulong steamId) || steamId != member.steamID ||
                    member.isHost != (steamId == snapshot.Host) || member.isSelf != (steamId == snapshot.Local))
                    return false;
                int slot = 1 << (member.playerID - PlayerSlots.FirstPlayerId);
                if ((seenSlots & slot) != 0) return false;
                seenSlots |= slot;
                matched++;
            }
            return matched == snapshot.Peers.Count;
        }

        internal static void Retire(long sessionId = 0)
        {
            if (sessionId != 0 && sessionId != Volatile.Read(ref world)?.Session) return;
            Volatile.Write(ref world, null);
            foreach (var feature in Features)
                try { feature.Apply(false); }
                catch (Exception error) { NativeApiLog.Error(log, "Could not retire simulation capability " + feature.Key + ": " + error); }
        }
    }

    [MessagePackFormatter(typeof(SimulationAgreementPacketFormatter))]
    internal sealed class SimulationAgreementPacket
    {
        internal const int Hello = 0, Challenge = 1, Report = 2, Decision = 3, Acknowledge = 4, Fault = 5;
        internal ulong Lobby;
        internal long Round;
        internal int Operation;
        internal string Roster, Data;
    }

    internal sealed class SimulationAgreementPacketFormatter : IMessagePackFormatter<SimulationAgreementPacket>
    {
        public SimulationAgreementPacketFormatter() { }

        public void Serialize(ref MessagePackWriter writer, SimulationAgreementPacket value, MessagePackSerializerOptions options)
        {
            writer.WriteArrayHeader(6);
            writer.Write(1);
            writer.Write(value.Operation);
            writer.Write(value.Lobby);
            writer.Write(value.Round);
            writer.Write(value.Roster);
            writer.Write(value.Data);
        }

        public SimulationAgreementPacket Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.ReadArrayHeader() != 6 || reader.ReadInt32() != 1)
                throw new InvalidOperationException("Unsupported simulation agreement packet.");
            var packet = new SimulationAgreementPacket
            {
                Operation = reader.ReadInt32(), Lobby = reader.ReadUInt64(), Round = reader.ReadInt64(),
                Roster = reader.ReadString(), Data = reader.ReadString()
            };
            if (packet.Operation < 0 || packet.Operation > SimulationAgreementPacket.Fault || packet.Round < 0 ||
                packet.Lobby == 0 || packet.Roster == null || packet.Roster.Length > 256 ||
                packet.Data == null || packet.Data.Length > 7000)
                throw new InvalidOperationException("Invalid simulation agreement packet.");
            return packet;
        }
    }
}
