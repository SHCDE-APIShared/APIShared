using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace APISharedTests
{
    [TestClass]
    public sealed class PresetSharingTests
    {
        [TestMethod]
        public void OfferAndRequestCarryNoPresetDocument()
        {
            var lobby = 1234UL;
            var sender = Guid.NewGuid();
            var receiver = Guid.NewGuid();
            var offerId = Guid.NewGuid();
            var requestId = Guid.NewGuid();
            var offer = new PresetSharePacket(PresetShareOperation.Offer, lobby, sender, receiver,
                offerId, Guid.Empty, "Example_Serp", "Überblick", offerSequence: 1);
            var request = new PresetSharePacket(PresetShareOperation.Request, lobby, receiver, sender,
                offerId, requestId);

            var copiedOffer = RoundTrip(offer);
            var copiedRequest = RoundTrip(request);
            var acknowledgement = RoundTrip(new PresetSharePacket(PresetShareOperation.OfferAcknowledged,
                lobby, receiver, sender, offerId, Guid.Empty));

            Assert.AreEqual(PresetShareOperation.OfferAcknowledged, acknowledgement.Operation);
            Assert.AreEqual(offerId, acknowledgement.OfferId);
            Assert.IsNull(acknowledgement.Json);
            Assert.AreEqual("Überblick", copiedOffer.PresetName);
            Assert.AreEqual(receiver, copiedOffer.ToMembership);
            Assert.AreEqual(offerId, copiedRequest.OfferId);
            Assert.AreEqual(requestId, copiedRequest.RequestId);
            Assert.IsNull(copiedOffer.Json);
            Assert.IsNull(copiedRequest.Json);

            var leakedOffer = new PresetSharePacket(PresetShareOperation.Offer, lobby, sender, receiver,
                offerId, Guid.Empty, "Example_Serp", "Private", json: "{}", offerSequence: 1);
            Assert.ThrowsExactly<MessagePackSerializationException>(() => MessagePackSerializer.Serialize(leakedOffer));
        }

        [TestMethod]
        public void ResponseLimitUsesUtf8BytesAndRejectsTrailingData()
        {
            // Multi-byte characters distinguish the wire ceiling from a character count.
            var json = new string('é', PresetSharePacket.MaximumJsonBytes / 2);
            var valid = Response(json);
            byte[] bytes = MessagePackSerializer.Serialize(valid);
            Assert.IsLessThanOrEqualTo(PresetSharePacket.MaximumBodyBytes, bytes.Length);
            Assert.AreEqual(json, RoundTrip(valid).Json);

            var tooLarge = Response(json + "x");
            Assert.ThrowsExactly<MessagePackSerializationException>(() => MessagePackSerializer.Serialize(tooLarge));
            Assert.ThrowsExactly<MessagePackSerializationException>(() =>
                MessagePackSerializer.Deserialize<PresetSharePacket>(bytes.Concat(new byte[] { 0xc0 }).ToArray()));
        }

        [TestMethod]
        public void CompatibilityRequiresMatchingVersionPacketIdAndMembership()
        {
            const short PacketId = 1200;
            Guid membership = Guid.NewGuid();
            int wire = PresetSharePacket.ProtocolVersion;
            Assert.IsTrue(PresetShareService.TryParseAdvertisement($"{wire}:{PacketId}:{membership:N}", PacketId, out Guid parsed));
            Assert.AreEqual(membership, parsed);
            Assert.IsFalse(PresetShareService.TryParseAdvertisement($"{wire + 1}:{PacketId}:{membership:N}", PacketId, out _));
            Assert.IsFalse(PresetShareService.TryParseAdvertisement($"{wire}:{PacketId + 1}:{membership:N}", PacketId, out _));
            Assert.IsFalse(PresetShareService.TryParseAdvertisement($"{wire}:{PacketId}:{Guid.Empty:N}", PacketId, out _));
            Assert.IsFalse(PresetShareService.TryParseAdvertisement(null, PacketId, out _));
        }

        [TestMethod]
        public void ASharingSessionOnlyAdmitsCompatibleCurrentLobbyPeers()
        {
            const short packetId = 1200;
            const ulong peer = 1234;
            var peers = new HashSet<ulong> { peer };
            Guid membership = Guid.NewGuid();
            string Advertisement(ulong id) => id == peer
                ? $"{PresetSharePacket.ProtocolVersion}:{packetId}:{membership:N}"
                : throw new InvalidOperationException("Read metadata for an unrelated peer.");

            Assert.IsFalse(PresetShareService.TryResolveCompatiblePeer(false, peer, peers, packetId,
                _ => throw new InvalidOperationException("Read metadata outside the lobby."), out _));
            Assert.IsFalse(PresetShareService.TryResolveCompatiblePeer(true, peer + 1, peers, packetId, Advertisement, out _));
            Assert.IsFalse(PresetShareService.TryResolveCompatiblePeer(true, peer, peers, packetId + 1, Advertisement, out _));
            Assert.IsTrue(PresetShareService.TryResolveCompatiblePeer(true, peer, peers, packetId, Advertisement, out Guid received));
            Assert.AreEqual(membership, received);
        }

        [TestMethod]
        public void AReplyMustMatchTheOpenRequestAndCanOnlyCompleteOnce()
        {
            const ulong lobby = 1234;
            const ulong sender = 5678;
            Guid from = Guid.NewGuid();
            Guid to = Guid.NewGuid();
            Guid offerId = Guid.NewGuid();
            Guid requestId = Guid.NewGuid();
            var offerPacket = new PresetSharePacket(PresetShareOperation.Offer, lobby, from, to,
                offerId, Guid.Empty, "Example_Serp", "Preset", offerSequence: 1);
            var offer = new PresetShareOffer(sender, "Other player", "Example_Serp", offerPacket);
            var request = new PresetShareService.PendingRequest(offer, _ => Assert.Fail("Unexpected direct callback."));
            var pending = new Dictionary<Guid, PresetShareService.PendingRequest> { [requestId] = request };
            var reply = new PresetSharePacket(PresetShareOperation.Response, lobby, from, to, offerId,
                requestId, json: "{}");
            long beforeDeadline = request.Deadline - 1;

            Assert.IsFalse(PresetShareService.TryTakeResponse(pending, lobby + 1, to, sender, reply,
                beforeDeadline, out _, out _));
            Assert.IsFalse(PresetShareService.TryTakeResponse(pending, lobby, Guid.NewGuid(), sender, reply,
                beforeDeadline, out _, out _));
            Assert.IsFalse(PresetShareService.TryTakeResponse(pending, lobby, to, sender + 1, reply,
                beforeDeadline, out _, out _));
            Assert.AreEqual(1, pending.Count);

            Assert.IsTrue(PresetShareService.TryTakeResponse(pending, lobby, to, sender, reply,
                beforeDeadline, out var accepted, out var result));
            Assert.AreSame(request, accepted);
            Assert.AreEqual(PresetShareFailure.None, result.Failure);
            Assert.AreEqual("{}", result.Json);
            Assert.IsFalse(PresetShareService.TryTakeResponse(pending, lobby, to, sender, reply,
                beforeDeadline, out _, out _));

            pending.Add(requestId, request);
            Assert.IsTrue(PresetShareService.TryTakeResponse(pending, lobby, to, sender, reply,
                request.Deadline, out _, out var timedOut));
            Assert.AreEqual(PresetShareFailure.TimedOut, timedOut.Failure);
            Assert.IsNull(timedOut.Json);
            Assert.IsEmpty(pending);
        }

        [TestMethod]
        public void ADelayedAdvertisementAndLostAcknowledgementCanBeRetried()
        {
            const ulong peer = 17;
            const ulong secondPeer = 19;
            Guid membership = Guid.NewGuid();
            Guid secondMembership = Guid.NewGuid();
            var offer = new PresetShareService.PublishedOffer(Guid.NewGuid(), "Example_Serp", "Preset", "{}",
                new[] { peer, secondPeer }, 0);

            int attempts = 0;
            Assert.IsNull(offer.AdvanceDelivery(0, _ => null, (_, _) => throw new AssertFailedException()));
            Assert.IsNull(offer.AdvanceDelivery(Stopwatch.Frequency, _ => membership, (_, _) => ++attempts > 0));
            Assert.AreEqual(2, attempts);
            Assert.IsFalse(offer.RecordAcknowledgement(peer, secondMembership, Stopwatch.Frequency));
            Assert.IsFalse(offer.RecordAcknowledgement(peer, membership, Stopwatch.Frequency));
            Assert.IsNull(offer.AdvanceDelivery(2 * Stopwatch.Frequency,
                _ => membership, (id, token) =>
                {
                    Assert.AreEqual(secondPeer, id);
                    Assert.AreEqual(membership, token);
                    ++attempts;
                    return true;
                }));
            Assert.AreEqual(3, attempts);
            Assert.IsTrue(offer.RecordAcknowledgement(secondPeer, membership, 2 * Stopwatch.Frequency));
            Assert.AreEqual(PresetShareFailure.None, offer.AdvanceDelivery(
                2 * Stopwatch.Frequency, _ => throw new AssertFailedException(), (_, _) => throw new AssertFailedException()));
        }

        [TestMethod]
        public void ADeliveryDeadlineReportsPartialSuccessAndKeepsEligibleDownloads()
        {
            const ulong peer = 17;
            const ulong unanswered = 19;
            Guid membership = Guid.NewGuid();
            var offer = new PresetShareService.PublishedOffer(Guid.NewGuid(), "Example_Serp", "Preset", "{}",
                new[] { peer, unanswered }, 0);
            Assert.IsNull(offer.AdvanceDelivery(0, _ => membership, (_, _) => true));
            Assert.IsFalse(offer.RecordAcknowledgement(peer, membership, 0));
            Assert.IsNull(offer.AdvanceDelivery(Stopwatch.Frequency, _ => Guid.NewGuid(),
                (_, _) => throw new AssertFailedException("A new membership inherited the old offer.")));

            // A comfortably expired monotonic timestamp isolates the deadline's effects from the actual timer interval.
            long expired = 60 * Stopwatch.Frequency;
            Assert.AreEqual(PresetShareFailure.PartialDelivery, offer.AdvanceDelivery(expired,
                _ => throw new AssertFailedException(), (_, _) => throw new AssertFailedException()));
            offer.FinishDelivery();
            Assert.IsFalse(offer.RecordAcknowledgement(unanswered, membership, expired));
            Assert.IsTrue(offer.WasSubmittedTo(peer, membership));
            Assert.IsTrue(offer.WasSubmittedTo(unanswered, membership));
            Assert.IsFalse(offer.WasSubmittedTo(peer, Guid.NewGuid()));
        }

        [TestMethod]
        public void ARepeatedOfferCanBeAcknowledgedWithoutReopeningIt()
        {
            var senderMembership = Guid.NewGuid();
            var receiver = Guid.NewGuid();
            var offerId = Guid.NewGuid();
            var offer = new PresetSharePacket(PresetShareOperation.Offer, 1234, senderMembership, receiver,
                offerId, Guid.Empty, "Example_Serp", "Preset", offerSequence: 2);
            Assert.AreEqual(PresetShareService.OfferDisposition.New, PresetShareService.GetOfferDisposition(default, offer));
            Assert.AreEqual(PresetShareService.OfferDisposition.Duplicate,
                PresetShareService.GetOfferDisposition((senderMembership, 2, offerId), offer));
            Assert.AreEqual(PresetShareService.OfferDisposition.Obsolete,
                PresetShareService.GetOfferDisposition((senderMembership, 3, Guid.NewGuid()), offer));
            Assert.AreEqual(PresetShareService.OfferDisposition.Obsolete,
                PresetShareService.GetOfferDisposition((senderMembership, 2, Guid.NewGuid()), offer));
        }

        [TestMethod]
        public void AQuickNewRequestHasABusyReplyAndCanBeRetried()
        {
            const ulong peer = 17;
            const ulong lobby = 1234;
            Guid first = Guid.NewGuid();
            Guid next = Guid.NewGuid();
            Guid sender = Guid.NewGuid();
            Guid receiver = Guid.NewGuid();
            Guid offerId = Guid.NewGuid();
            long now = Stopwatch.GetTimestamp();
            Assert.AreEqual(PresetShareService.RequestReply.Ignore, PresetShareService.PlanRequestReply((first, now), first, now));
            Assert.AreEqual(PresetShareService.RequestReply.Busy, PresetShareService.PlanRequestReply((first, now), next, now));
            Assert.AreEqual(PresetShareService.RequestReply.Send,
                PresetShareService.PlanRequestReply((first, now), next, now + Stopwatch.Frequency));

            var packet = new PresetSharePacket(PresetShareOperation.Offer, lobby, sender, receiver,
                offerId, Guid.Empty, "Example_Serp", "Preset", offerSequence: 1);
            var offer = new PresetShareOffer(peer, "Peer", "Example_Serp", packet);
            var pending = new Dictionary<Guid, PresetShareService.PendingRequest>
            {
                [next] = new PresetShareService.PendingRequest(offer, _ => Assert.Fail("Unexpected direct completion.")),
            };
            var busy = RoundTrip(new PresetSharePacket(PresetShareOperation.Busy, lobby, sender, receiver, offerId, next));
            Assert.IsTrue(PresetShareService.TryTakeResponse(pending, lobby, receiver, peer, busy,
                now, out var request, out var result));
            Assert.AreSame(offer, request.Offer);
            Assert.AreEqual(PresetShareFailure.Busy, result.Failure);
            Assert.IsNull(result.Json);
            Assert.IsEmpty(pending);
        }

        private static PresetSharePacket Response(string json) =>
            new PresetSharePacket(PresetShareOperation.Response, 1234, Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), json: json);

        private static PresetSharePacket RoundTrip(PresetSharePacket packet) =>
            MessagePackSerializer.Deserialize<PresetSharePacket>(MessagePackSerializer.Serialize(packet));
    }
}
