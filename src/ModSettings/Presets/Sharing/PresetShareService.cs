using APIShared.Internal;
using APIShared.Networking;
using BepInEx.Logging;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace APIShared.ModSettings
{
    internal enum PresetShareFailure
    {
        None,
        Unavailable,
        NoCompatiblePeers,
        Invalid,
        TooLarge,
        SendFailed,
        Expired,
        AlreadyPending,
        TimedOut,
        Busy,
        DeliveryTimedOut,
        PartialDelivery
    }

    internal sealed class PresetShareOffer
    {
        internal PresetShareOffer(ulong sender, string senderName, string modGuid, PresetSharePacket packet)
        {
            Sender = sender;
            SenderName = senderName;
            ModGuid = modGuid;
            PresetName = packet.PresetName;
            Id = packet.OfferId;
            Membership = packet.FromMembership;
        }

        internal ulong Sender { get; }
        internal string SenderName { get; }
        internal string ModGuid { get; }
        internal string PresetName { get; }
        internal Guid Id { get; }
        internal Guid Membership { get; }
        internal long NextRequestAt { get; set; }
    }

    internal sealed class PresetShareResult
    {
        internal PresetShareResult(PresetShareFailure failure, string json = null)
        {
            Failure = failure;
            Json = json;
        }

        internal PresetShareFailure Failure { get; }
        internal string Json { get; }
    }

    // The APIShared packet channel, lobby observer and mission lifecycle root this service
    // for the process. All mutable state and presentation callbacks belong to Unity.
    internal static class PresetShareService
    {
        private const string RegistrationId = "preset-sharing";
        private const string AdvertisementKey = "apishared_preset_sharing";
        private const int MaximumOffers = 32;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan OfferRetryInterval = TimeSpan.FromSeconds(1);
        private const int DeliveryWindowSeconds = 8;
        private static long RequestCooldown => Stopwatch.Frequency;
        private static readonly Dictionary<string, PublishedOffer> Published =
            new Dictionary<string, PublishedOffer>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<PresetShareOffer> Inbox = new List<PresetShareOffer>();
        private static readonly Dictionary<Guid, PendingRequest> Requests = new Dictionary<Guid, PendingRequest>();
        private static readonly Dictionary<(ulong Sender, string Mod), (Guid Membership, ulong Sequence, Guid Id)> LastOffers =
            new Dictionary<(ulong, string), (Guid, ulong, Guid)>();
        private static MessageChannel<PresetSharePacket> channel;
        private static Callback<SteamNetworkingMessagesSessionRequest_t> sessionRequests;
        private static ManualLogSource log;
        private static Platform_Multiplayer.MPLobby lobby;
        private static ulong lobbyId;
        private static Guid membership;
        private static HashSet<ulong> peers = new HashSet<ulong>();
        private static bool initialized;
        private static bool offerRetryScheduled;

        internal static event Action Changed;
        internal static Guid LobbySession => initialized && UnityMainThreadDispatch.IsMainThread &&
            !MissionLifecycleService.HasContext && lobbyId != 0 && ReferenceEquals(lobby, Platform_Multiplayer.Instance?.activeLobby)
            ? membership : Guid.Empty;
        internal static bool IsLobbyAvailable => LobbySession != Guid.Empty && peers.Count > 0;

        internal static IReadOnlyList<PresetShareOffer> Offers =>
            IsLobbyAvailable ? Inbox.ToArray() : Array.Empty<PresetShareOffer>();

        internal static void Initialize(ManualLogSource logger)
        {
            if (initialized)
                return;
            log = logger;
            var api = ApiShared.ForMod(APISharedPlugin.PluginGuid);
            // Reserve the Extender packet ID even when this machine cannot enable sharing.
            if (channel == null)
            {
                channel = api.Network.RegisterMessage<PresetSharePacket>(RegistrationId, Receive,
                    new MessageChannelOptions<PresetSharePacket>
                    {
                        MaximumBodyBytes = PresetSharePacket.MaximumBodyBytes,
                        Snapshot = packet => packet,
                        IsEnabled = () => initialized
                    });
            }

            if (!api.TryGetMissionLifecycle(out IMissionLifecycleCapability missions, out NativeCapabilityDiagnostic missionDiagnostic))
                throw new InvalidOperationException("Preset sharing requires mission lifecycle observation: " + missionDiagnostic);
            if (!api.TryGetLobbyState(out ILobbyStateCapability lobbies, out NativeCapabilityDiagnostic lobbyDiagnostic))
                throw new InvalidOperationException("Preset sharing requires lobby state observation: " + lobbyDiagnostic);

            if (!missions.TryRegisterObserver(RegistrationId, _ => ObserveMission(), _ => ObserveMission(),
                _ => ObserveMission(), out missionDiagnostic))
                throw new InvalidOperationException("Preset-sharing mission observer registration failed: " + missionDiagnostic);
            if (!lobbies.TryRegisterObserver(RegistrationId, _ => RefreshLobby(), out lobbyDiagnostic))
                throw new InvalidOperationException("Preset-sharing lobby observer registration failed: " + lobbyDiagnostic);

            initialized = true;
            RefreshLobby();
        }

        internal static bool TryOffer(string modGuid, string presetName, string json, out PresetShareFailure failure,
            Action<PresetShareFailure> delivered = null)
        {
            RefreshLobby();
            if (!IsLobbyAvailable)
                return Fail(PresetShareFailure.Unavailable, out failure);

            try
            {
                if (string.IsNullOrWhiteSpace(modGuid) || string.IsNullOrWhiteSpace(presetName) ||
                    string.IsNullOrWhiteSpace(json) ||
                    PresetSharePacket.Utf8.GetByteCount(modGuid) > PresetSharePacket.MaximumModGuidBytes ||
                    PresetSharePacket.Utf8.GetByteCount(presetName) > PresetSharePacket.MaximumPresetNameBytes)
                    return Fail(PresetShareFailure.Invalid, out failure);
                if (PresetSharePacket.Utf8.GetByteCount(json) > PresetSharePacket.MaximumJsonBytes)
                    return Fail(PresetShareFailure.TooLarge, out failure);
            }
            catch (System.Text.EncoderFallbackException)
            {
                return Fail(PresetShareFailure.Invalid, out failure);
            }

            var offer = new PublishedOffer(Guid.NewGuid(), modGuid, presetName, json, peers,
                Stopwatch.GetTimestamp(), delivered);
            if (!Published.ContainsKey(modGuid) && Published.Count == MaximumOffers)
                Published.Remove(Published.OrderBy(item => item.Value.Sequence).First().Key);
            Published[modGuid] = offer;
            AdvanceOffer(offer);
            ScheduleOfferRetry();
            failure = PresetShareFailure.None;
            return true;
        }

        private static void AdvanceOffer(PublishedOffer offer)
        {
            var outcome = offer.AdvanceDelivery(Stopwatch.GetTimestamp(),
                peer => TryGetCompatiblePeer(peer, out Guid token) ? token : (Guid?)null,
                (peer, token) => channel.SendToSteamPeer(NewPacket(PresetShareOperation.Offer, token,
                    offer.Id, Guid.Empty, offer.ModGuid, offer.PresetName, offerSequence: offer.Sequence), peer).Submitted);
            if (outcome.HasValue)
                FinishOffer(offer, outcome.Value);
        }

        private static void ScheduleOfferRetry()
        {
            if (offerRetryScheduled || !Published.Values.Any(offer => offer.DeliveryPending))
                return;
            offerRetryScheduled = true;
            _ = RetryOffersAfterDelay();
        }

        private static async Task RetryOffersAfterDelay()
        {
            await Task.Delay(OfferRetryInterval).ConfigureAwait(false);
            UnityMainThreadDispatch.TryEnqueue(() =>
            {
                offerRetryScheduled = false;
                RefreshLobby();
                foreach (PublishedOffer offer in Published.Values.Where(item => item.DeliveryPending).ToArray())
                {
                    if (Published.TryGetValue(offer.ModGuid, out var current) && ReferenceEquals(offer, current))
                        AdvanceOffer(offer);
                }
                ScheduleOfferRetry();
            });
        }

        private static void FinishOffer(PublishedOffer offer, PresetShareFailure outcome)
        {
            var callback = offer.FinishDelivery();
            try { callback?.Invoke(outcome); }
            catch (Exception exception) { NativeApiLog.Error(log, "Preset offer delivery notification failed: " + exception); }
        }

        internal static bool TryRequest(PresetShareOffer offer, Action<PresetShareResult> complete,
            out PresetShareFailure failure)
        {
            RefreshLobby();
            if (!IsLobbyAvailable)
                return Fail(PresetShareFailure.Unavailable, out failure);
            if (offer == null || complete == null)
                return Fail(PresetShareFailure.Invalid, out failure);
            if (!Inbox.Contains(offer) || !TryGetCompatiblePeer(offer.Sender, out Guid peerMembership) ||
                peerMembership != offer.Membership)
                return Fail(PresetShareFailure.Expired, out failure);
            if (Requests.Values.Any(request => ReferenceEquals(request.Offer, offer)))
                return Fail(PresetShareFailure.AlreadyPending, out failure);
            if (Stopwatch.GetTimestamp() < offer.NextRequestAt)
                return Fail(PresetShareFailure.Busy, out failure);

            Guid requestId = Guid.NewGuid();
            var packet = NewPacket(PresetShareOperation.Request, peerMembership, offer.Id, requestId);
            var pending = new PendingRequest(offer, complete);
            Requests.Add(requestId, pending);
            if (!channel.SendToSteamPeer(packet, offer.Sender).Submitted)
            {
                Requests.Remove(requestId);
                return Fail(PresetShareFailure.SendFailed, out failure);
            }

            _ = AwaitResponse(requestId);
            failure = PresetShareFailure.None;
            return true;
        }

        internal static bool Dismiss(PresetShareOffer offer)
        {
            if (!UnityMainThreadDispatch.IsMainThread || offer == null || !Inbox.Remove(offer))
                return false;
            ExpireRequestsFor(offer);
            NotifyChanged();
            return true;
        }

        private static void Receive(NetworkMessage<PresetSharePacket> message)
        {
            RefreshLobby();
            PresetSharePacket packet = message.Packet;
            ulong sender = message.SenderSteamId;
            if (!IsLobbyAvailable || !IsCurrentRecipient(packet, lobbyId, membership) ||
                !TryGetCompatiblePeer(sender, out Guid senderMembership) || packet.FromMembership != senderMembership)
                return;

            switch (packet.Operation)
            {
                case PresetShareOperation.Offer:
                    ReceiveOffer(sender, packet);
                    break;
                case PresetShareOperation.OfferAcknowledged:
                    ReceiveOfferAcknowledgement(sender, packet);
                    break;
                case PresetShareOperation.Request:
                    ReplyToRequest(sender, packet);
                    break;
                case PresetShareOperation.Response:
                case PresetShareOperation.Unavailable:
                case PresetShareOperation.Busy:
                    ReceiveResponse(sender, packet);
                    break;
            }
        }

        private static void ReceiveOffer(ulong sender, PresetSharePacket packet)
        {
            // Only installed endpoints can acknowledge an offer as usable. Keep the
            // receipt after dismissal so a retransmission can be acknowledged without reopening it.
            var endpoints = ModSettingsApplication.Endpoints;
            string modGuid = endpoints.ContainsKey(packet.ModGuid) ? packet.ModGuid :
                endpoints.Keys.FirstOrDefault(key => string.Equals(key, packet.ModGuid, StringComparison.OrdinalIgnoreCase));
            if (modGuid == null)
                return;

            var key = (sender, modGuid);
            var disposition = GetOfferDisposition(LastOffers.TryGetValue(key, out var previous) ? previous : default, packet);
            if (disposition == OfferDisposition.Obsolete)
                return;
            if (disposition == OfferDisposition.Duplicate)
            {
                AcknowledgeOffer(sender, packet);
                return;
            }
            LastOffers[key] = (packet.FromMembership, packet.OfferSequence, packet.OfferId);
            int existing = Inbox.FindIndex(offer => offer.Sender == sender &&
                string.Equals(offer.ModGuid, modGuid, StringComparison.OrdinalIgnoreCase));
            PresetShareOffer obsolete = null;
            if (existing >= 0)
            {
                obsolete = Inbox[existing];
                Inbox.RemoveAt(existing);
            }
            else if (Inbox.Count == MaximumOffers)
            {
                obsolete = Inbox[0];
                Inbox.RemoveAt(0);
            }

            Inbox.Add(new PresetShareOffer(sender, DisplayText(LobbyReadinessSupport.PlayerName(sender)), modGuid, packet));
            AcknowledgeOffer(sender, packet);
            if (obsolete != null)
                ExpireRequestsFor(obsolete);
            NotifyChanged();
        }

        internal enum OfferDisposition { New, Duplicate, Obsolete }

        internal static OfferDisposition GetOfferDisposition((Guid Membership, ulong Sequence, Guid Id) previous,
            PresetSharePacket offer)
        {
            if (previous.Membership != offer.FromMembership || previous.Sequence < offer.OfferSequence)
                return OfferDisposition.New;
            return previous.Sequence == offer.OfferSequence && previous.Id == offer.OfferId
                ? OfferDisposition.Duplicate : OfferDisposition.Obsolete;
        }

        private static void AcknowledgeOffer(ulong sender, PresetSharePacket offer) =>
            channel.SendToSteamPeer(NewPacket(PresetShareOperation.OfferAcknowledged, offer.FromMembership,
                offer.OfferId, Guid.Empty), sender);

        private static void ReceiveOfferAcknowledgement(ulong sender, PresetSharePacket acknowledgement)
        {
            var offer = Published.Values.FirstOrDefault(item => item.Id == acknowledgement.OfferId);
            if (offer != null && offer.RecordAcknowledgement(sender, acknowledgement.FromMembership, Stopwatch.GetTimestamp()))
                FinishOffer(offer, PresetShareFailure.None);
        }

        private static void ReplyToRequest(ulong sender, PresetSharePacket request)
        {
            PublishedOffer offer = Published.Values.FirstOrDefault(item => item.Id == request.OfferId);
            if (offer == null || !offer.WasSubmittedTo(sender, request.FromMembership))
            {
                channel.SendToSteamPeer(NewPacket(PresetShareOperation.Unavailable, request.FromMembership,
                    request.OfferId, request.RequestId), sender);
                return;
            }

            var target = offer.Targets[sender];
            long now = Stopwatch.GetTimestamp();
            bool completesDelivery = offer.RecordAcknowledgement(sender, request.FromMembership, now);
            SendPresetReply(offer, target, sender, request, now);
            if (completesDelivery)
                FinishOffer(offer, PresetShareFailure.None);
        }

        private static void SendPresetReply(PublishedOffer offer, OfferTarget target, ulong sender,
            PresetSharePacket request, long now)
        {
            var action = PlanRequestReply(target.LastRequest, request.RequestId, now);
            if (action == RequestReply.Ignore)
                return;
            if (action == RequestReply.Busy)
            {
                // A normal receiver observes a full cooldown after the previous reply.
                // Limit small Busy replies separately for callers bypassing that guard.
                if (!target.LastBusyAt.HasValue || now - target.LastBusyAt.Value >= RequestCooldown / 4)
                {
                    target.LastBusyAt = now;
                    channel.SendToSteamPeer(NewPacket(PresetShareOperation.Busy, request.FromMembership,
                        request.OfferId, request.RequestId), sender);
                }
                return;
            }
            if (channel.SendToSteamPeer(NewPacket(PresetShareOperation.Response, request.FromMembership,
                request.OfferId, request.RequestId, json: offer.Json), sender).Submitted)
                target.LastRequest = (request.RequestId, now);
        }

        internal enum RequestReply { Ignore, Busy, Send }

        internal static RequestReply PlanRequestReply((Guid Id, long SentAt) last, Guid requestId, long now)
        {
            if (last.Id == requestId)
                return RequestReply.Ignore;
            return last.Id != Guid.Empty && now - last.SentAt < RequestCooldown ? RequestReply.Busy : RequestReply.Send;
        }

        private static void ReceiveResponse(ulong sender, PresetSharePacket response)
        {
            long now = Stopwatch.GetTimestamp();
            if (!TryTakeResponse(Requests, lobbyId, membership, sender, response, now,
                out PendingRequest request, out PresetShareResult result))
                return;

            if (result.Failure == PresetShareFailure.Expired)
            {
                Inbox.Remove(request.Offer);
                Complete(request, result);
                NotifyChanged();
                return;
            }

            if (result.Failure == PresetShareFailure.None || result.Failure == PresetShareFailure.Busy)
                request.Offer.NextRequestAt = now + RequestCooldown;

            // The dialog dismisses after an atomically saved import. A transient
            // local storage failure leaves this offer available for a retry.
            Complete(request, result);
        }

        internal static bool TryTakeResponse(Dictionary<Guid, PendingRequest> requests, ulong currentLobby,
            Guid currentMembership, ulong sender, PresetSharePacket response, long now,
            out PendingRequest request, out PresetShareResult result)
        {
            result = null;
            request = null;
            if (!IsCurrentRecipient(response, currentLobby, currentMembership) ||
                (response.Operation != PresetShareOperation.Response && response.Operation != PresetShareOperation.Unavailable &&
                response.Operation != PresetShareOperation.Busy) ||
                !requests.TryGetValue(response.RequestId, out PendingRequest pending) ||
                pending.Offer.Id != response.OfferId || pending.Offer.Sender != sender ||
                pending.Offer.Membership != response.FromMembership)
                return false;

            requests.Remove(response.RequestId);
            request = pending;
            if (now >= pending.Deadline)
                result = new PresetShareResult(PresetShareFailure.TimedOut);
            else if (response.Operation == PresetShareOperation.Response)
                result = new PresetShareResult(PresetShareFailure.None, response.Json);
            else
                result = new PresetShareResult(response.Operation == PresetShareOperation.Busy
                    ? PresetShareFailure.Busy : PresetShareFailure.Expired);
            return true;
        }

        private static bool IsCurrentRecipient(PresetSharePacket packet, ulong currentLobby, Guid currentMembership) =>
            packet.Lobby == currentLobby && packet.ToMembership == currentMembership;

        private static async Task AwaitResponse(Guid requestId)
        {
            await Task.Delay(RequestTimeout).ConfigureAwait(false);
            UnityMainThreadDispatch.TryEnqueue(() =>
            {
                RefreshLobby();
                if (Requests.TryGetValue(requestId, out PendingRequest request))
                {
                    Requests.Remove(requestId);
                    Complete(request, new PresetShareResult(PresetShareFailure.TimedOut));
                }
            });
        }

        // A user may operate during the frame that changes the active lobby. Commands
        // and receive processing always confirm the live object and roster again.
        private static void RefreshLobby()
        {
            if (!initialized || !UnityMainThreadDispatch.IsMainThread)
                return;

            Platform_Multiplayer.MPLobby current = MissionLifecycleService.HasContext
                ? null : Platform_Multiplayer.Instance?.activeLobby;
            ulong currentId = current?.id.m_SteamID ?? 0;
            ulong localId = currentId == 0 ? 0 : SteamUser.GetSteamID().m_SteamID;
            ulong[] humans = localId == 0 ? Array.Empty<ulong>() : LobbyReadinessSupport.ObservedHumans(current);
            if (currentId == 0 || !humans.Contains(localId))
            {
                if (lobby != null)
                    ResetLobby(null, 0, new HashSet<ulong>());
                return;
            }

            var currentPeers = new HashSet<ulong>(humans.Where(id => id != localId));
            if (currentId != lobbyId || !ReferenceEquals(current, lobby))
            {
                Guid instance = Guid.NewGuid();
                // Steam is available here. Vanilla's pre-game listener admits the
                // lobby owner, while preset offers also travel between two clients.
                if (sessionRequests == null)
                    sessionRequests = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSteamSessionRequested);
                // Each same-room rejoin receives a fresh membership token. A late
                // packet from the old incarnation cannot match the new recipient.
                SteamMatchmaking.SetLobbyMemberData(current.id, AdvertisementKey, string.Format(
                    CultureInfo.InvariantCulture, "{0}:{1}:{2:N}", PresetSharePacket.ProtocolVersion, channel.PacketId, instance));
                ResetLobby(current, currentId, currentPeers, instance);
                return;
            }

            if (peers.SetEquals(currentPeers))
                return;

            peers = currentPeers;
            foreach (var key in LastOffers.Keys.Where(key => !peers.Contains(key.Sender)).ToArray())
                LastOffers.Remove(key);
            foreach (PublishedOffer offered in Published.Values)
            {
                foreach (ulong id in offered.Targets.Keys.Where(id => !peers.Contains(id)).ToArray())
                    offered.Targets.Remove(id);
            }

            foreach (PresetShareOffer offer in Inbox.Where(offer => !peers.Contains(offer.Sender)).ToArray())
            {
                Inbox.Remove(offer);
                ExpireRequestsFor(offer);
            }
            NotifyChanged();
        }

        private static void ObserveMission()
        {
            if (UnityMainThreadDispatch.IsMainThread)
                RefreshLobby();
            else
                UnityMainThreadDispatch.TryEnqueue(RefreshLobby);
        }

        private static void OnSteamSessionRequested(SteamNetworkingMessagesSessionRequest_t notification)
        {
            SteamNetworkingIdentity remote = notification.m_identityRemote;
            UnityMainThreadDispatch.TryEnqueue(() => AcceptSharingSession(remote));
        }

        private static void AcceptSharingSession(SteamNetworkingIdentity remote)
        {
            RefreshLobby();
            if (TryResolveCompatiblePeer(IsLobbyAvailable, remote.GetSteamID().m_SteamID, peers,
                channel.PacketId, GetPeerAdvertisement, out _))
                SteamNetworkingMessages.AcceptSessionWithUser(ref remote);
        }

        private static void ResetLobby(Platform_Multiplayer.MPLobby next, ulong id, HashSet<ulong> nextPeers,
            Guid nextMembership = default)
        {
            lobby = next;
            lobbyId = id;
            peers = nextPeers;
            membership = nextMembership;
            Published.Clear();
            Inbox.Clear();
            LastOffers.Clear();
            PendingRequest[] expired = Requests.Values.ToArray();
            Requests.Clear();
            foreach (PendingRequest request in expired)
                Complete(request, new PresetShareResult(PresetShareFailure.Expired));
            NotifyChanged();
        }

        private static bool TryGetCompatiblePeer(ulong peer, out Guid peerMembership) =>
            TryResolveCompatiblePeer(IsLobbyAvailable, peer, peers, channel.PacketId, GetPeerAdvertisement, out peerMembership);

        private static string GetPeerAdvertisement(ulong peer) =>
            SteamMatchmaking.GetLobbyMemberData(new CSteamID(lobbyId), new CSteamID(peer), AdvertisementKey);

        internal static bool TryResolveCompatiblePeer(bool lobbyAvailable, ulong peer, ISet<ulong> currentPeers,
            short packetId, Func<ulong, string> getAdvertisement, out Guid peerMembership)
        {
            peerMembership = Guid.Empty;
            return lobbyAvailable && currentPeers.Contains(peer) &&
                TryParseAdvertisement(getAdvertisement(peer), packetId, out peerMembership);
        }

        internal static bool TryParseAdvertisement(string value, short expectedPacketId, out Guid membershipToken)
        {
            membershipToken = Guid.Empty;
            if (value == null || value.Length > 45)
                return false;
            string[] fields = value.Split(':');
            return fields.Length == 3 &&
                int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int version) &&
                version == PresetSharePacket.ProtocolVersion &&
                short.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out short id) &&
                id == expectedPacketId &&
                Guid.TryParseExact(fields[2], "N", out membershipToken) && membershipToken != Guid.Empty;
        }

        private static PresetSharePacket NewPacket(PresetShareOperation operation, Guid peer, Guid offer, Guid request,
            string mod = null, string name = null, string json = null, ulong offerSequence = 0) =>
            new PresetSharePacket(operation, lobbyId, membership, peer, offer, request, mod, name, json, offerSequence);

        private static bool Fail(PresetShareFailure error, out PresetShareFailure failure)
        {
            failure = error;
            return false;
        }

        private static string DisplayText(string value) =>
            new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

        private static void ExpireRequestsFor(PresetShareOffer offer)
        {
            foreach (var item in Requests.Where(item => ReferenceEquals(item.Value.Offer, offer)).ToArray())
            {
                Requests.Remove(item.Key);
                Complete(item.Value, new PresetShareResult(PresetShareFailure.Expired));
            }
        }

        private static void Complete(PendingRequest request, PresetShareResult result)
        {
            try { request.Complete(result); }
            catch (Exception exception) { NativeApiLog.Error(log, "Preset-sharing completion failed: " + exception); }
        }

        private static void NotifyChanged()
        {
            Delegate[] handlers = Changed?.GetInvocationList() ?? Array.Empty<Delegate>();
            foreach (Action observer in handlers)
                try { observer(); }
                catch (Exception exception) { NativeApiLog.Error(log, "Preset-sharing observer failed: " + exception); }
        }

        internal sealed class PublishedOffer
        {
            private static ulong sequence;
            private readonly long deadline;
            private Action<PresetShareFailure> completed;

            internal PublishedOffer(Guid id, string modGuid, string presetName, string json, IEnumerable<ulong> peers,
                long now, Action<PresetShareFailure> completed = null)
            {
                Id = id;
                ModGuid = modGuid;
                PresetName = presetName;
                Json = json;
                Sequence = ++sequence;
                deadline = now + DeliveryWindowSeconds * Stopwatch.Frequency;
                this.completed = completed;
                Targets = peers.ToDictionary(peer => peer, _ => new OfferTarget());
            }

            internal Guid Id { get; }
            internal string ModGuid { get; }
            internal string PresetName { get; }
            internal string Json { get; }
            internal ulong Sequence { get; }
            internal bool DeliveryPending { get; private set; } = true;
            internal Dictionary<ulong, OfferTarget> Targets { get; }

            internal PresetShareFailure? AdvanceDelivery(long now, Func<ulong, Guid?> getMembership,
                Func<ulong, Guid, bool> send)
            {
                if (Targets.Count > 0 && Targets.Values.All(target => target.Acknowledged))
                    return PresetShareFailure.None;
                if (now >= deadline || Targets.Count == 0)
                {
                    if (Targets.Values.Any(target => target.Acknowledged))
                        return PresetShareFailure.PartialDelivery;
                    if (Targets.Values.Any(target => target.Submitted))
                        return PresetShareFailure.DeliveryTimedOut;
                    return Targets.Values.Any(target => target.Membership != Guid.Empty)
                        ? PresetShareFailure.SendFailed : PresetShareFailure.NoCompatiblePeers;
                }

                foreach (var entry in Targets)
                {
                    var target = entry.Value;
                    if (target.Acknowledged)
                        continue;
                    Guid? token = getMembership(entry.Key);
                    if (!token.HasValue || (target.Membership != Guid.Empty && target.Membership != token.Value))
                        continue;
                    target.Membership = token.Value;
                    if (send(entry.Key, token.Value))
                        target.Submitted = true;
                }
                return null;
            }

            internal bool WasSubmittedTo(ulong sender, Guid membership) =>
                Targets.TryGetValue(sender, out var target) && target.Submitted && target.Membership == membership;

            internal bool RecordAcknowledgement(ulong sender, Guid membership, long now)
            {
                if (!DeliveryPending || now >= deadline || !WasSubmittedTo(sender, membership))
                    return false;
                Targets[sender].Acknowledged = true;
                return Targets.Values.All(target => target.Acknowledged);
            }

            internal Action<PresetShareFailure> FinishDelivery()
            {
                DeliveryPending = false;
                var callback = completed;
                completed = null;
                return callback;
            }
        }

        internal sealed class OfferTarget
        {
            internal Guid Membership;
            internal bool Submitted;
            internal bool Acknowledged;
            internal (Guid Id, long SentAt) LastRequest;
            internal long? LastBusyAt;
        }

        internal sealed class PendingRequest
        {
            internal PendingRequest(PresetShareOffer offer, Action<PresetShareResult> complete)
            {
                Offer = offer;
                Complete = complete;
                Deadline = Stopwatch.GetTimestamp() + (long)(RequestTimeout.TotalSeconds * Stopwatch.Frequency);
            }

            internal PresetShareOffer Offer { get; }
            internal Action<PresetShareResult> Complete { get; }
            internal long Deadline { get; }
        }
    }
}
