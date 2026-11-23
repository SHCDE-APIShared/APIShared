using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using APIShared.Networking;
using static APIShared.Multiplayer.MultiplayerResyncDataPacket;

namespace APIShared.Multiplayer
{
    internal static partial class MultiplayerResyncDataRuntime
    {
        private static void Receive(NetworkMessage<MultiplayerResyncDataPacket> message)
        {
            lock (Gate)
            {
                var attempt = current;
                var packet = message.Packet;
                if (!Live(attempt) || attempt.Released || packet.Lobby != attempt.World.Lobby ||
                    packet.Round != attempt.World.Round || packet.Epoch != attempt.World.Epoch ||
                    packet.Repair != attempt.Repair) return;

                ulong sender = message.SenderSteamId;
                Peer peer = null;
                if (attempt.IsHost)
                {
                    if (!attempt.Peers.TryGetValue(sender, out peer)) return;
                    if (packet.Operation != Kind.Hello && peer.Nonce != packet.Nonce) return;
                }
                else if (sender != attempt.World.Host || packet.Nonce != attempt.Nonce) return;

                try
                {
                    UpdateRosterProof(attempt);
                    if (!Live(attempt)) return;
                    if (attempt.IsHost) ReceiveFromClient(attempt, peer, sender, packet);
                    else if (packet.Operation == Kind.Manifest) ReceiveManifest(attempt, packet.Body);
                    else if (packet.Operation == Kind.Chunk) ReceiveChunk(attempt, packet);
                    else throw new InvalidDataException("Unexpected message from the resync host.");
                }
                catch (Exception error) { Fail(attempt, error.Message); }
            }
        }

        private static void UpdateRosterProof(Attempt attempt)
        {
            attempt.RosterVerified = SimulationAgreement.VerifyLiveResyncRoster(attempt.World);
            if (!attempt.RosterVerified) Fail(attempt, "The admitted resync roster changed.");
        }

        private static void ReceiveFromClient(Attempt attempt, Peer peer, ulong sender, MultiplayerResyncDataPacket packet)
        {
            switch (packet.Operation)
            {
                case Kind.Hello:
                    if (peer.Nonce != 0 && peer.Nonce != packet.Nonce)
                        throw new InvalidDataException("A resync peer changed its attempt nonce.");
                    peer.Nonce = packet.Nonce;
                    if (attempt.Manifest != null)
                        Send(attempt, sender, peer.Nonce, Kind.Manifest, body: attempt.Manifest);
                    return;
                case Kind.Ready:
                    if (attempt.ManifestHash == null || peer.Index >= 0 ||
                        !packet.Body.AsSpan().SequenceEqual(attempt.ManifestHash))
                        throw new InvalidDataException("A resync peer acknowledged an unfinished or different manifest.");
                    peer.Ready = true;
                    return;
                case Kind.Request:
                case Kind.Progress:
                    if (attempt.Records == null || packet.Index >= attempt.Records.Length || peer.Ready)
                        throw new InvalidDataException("Unexpected record transfer request or progress.");
                    break;
                default:
                    throw new InvalidDataException("Unexpected message from a resync peer.");
            }

            byte[] record = attempt.Records[packet.Index];
            if (packet.Operation == Kind.Request)
            {
                if (record.Length == 0 || peer.Index >= 0 || peer.Delivered[packet.Index])
                    throw new InvalidDataException("A resync peer requested an invalid record or overlapped transfers.");
                peer.Index = packet.Index;
                peer.Sent = peer.Acknowledged = 0;
                attempt.RequestedRecords++;
            }
            else
            {
                if (peer.Index != packet.Index || !IsExpectedProgress(record.Length, peer.Sent, peer.Acknowledged, packet.Offset))
                    throw new InvalidDataException("A resync peer acknowledged data outside the active window.");
                peer.Acknowledged = packet.Offset;
                if (peer.Acknowledged == record.Length)
                {
                    peer.Delivered[packet.Index] = true;
                    peer.Index = -1;
                    return;
                }
            }

            int end = Math.Min(record.Length, peer.Acknowledged + Window * ChunkBytes);
            while (peer.Sent < end && Live(attempt))
            {
                int length = Math.Min(ChunkBytes, end - peer.Sent);
                if (!Send(attempt, sender, peer.Nonce, Kind.Chunk, packet.Index, peer.Sent,
                    record.AsSpan(peer.Sent, length).ToArray())) return;
                peer.Sent += length;
                attempt.TransferredBytes += length;
            }
        }

        internal static bool IsExpectedProgress(int length, int sent, int acknowledged, int offset) =>
            offset > acknowledged && offset == sent && offset == Math.Min(length, acknowledged + Window * ChunkBytes);

        private static byte[] EncodeManifest(byte[][] records)
        {
            var manifest = new byte[records.Length * ManifestEntryBytes];
            for (int i = 0; i < records.Length; i++)
            {
                var entry = manifest.AsSpan(i * ManifestEntryBytes, ManifestEntryBytes);
                BinaryPrimitives.WriteInt32LittleEndian(entry, records[i].Length);
                Hash(records[i]).AsSpan().CopyTo(entry.Slice(sizeof(int)));
            }
            return manifest;
        }

        private static void ReceiveManifest(Attempt attempt, byte[] manifest)
        {
            if (attempt.Manifest != null)
            {
                if (!manifest.AsSpan().SequenceEqual(attempt.Manifest))
                    throw new InvalidDataException("The host changed its resync manifest.");
                return;
            }

            if (manifest.Length != Providers.Count * ManifestEntryBytes)
                throw new InvalidDataException("Unexpected resync manifest record count.");
            var records = new byte[Providers.Count][];
            int total = 0;
            for (int i = 0; i < records.Length; i++)
            {
                int length = RecordLength(manifest, i);
                if (length < 0 || length > Providers[i].Maximum || (total += length) > MaxTotalBytes)
                    throw new InvalidDataException("Resync manifest record exceeds its admitted bound.");
                byte[] candidate = length == 0 ? Array.Empty<byte>() : attempt.Local[i];
                if (candidate.Length == length && MatchesRecordDigest(manifest, i, candidate)) records[i] = candidate;
                else if (length == 0) throw new InvalidDataException("Invalid digest for an empty resync record.");
            }

            attempt.Manifest = manifest;
            attempt.ManifestHash = Hash(manifest);
            attempt.Records = records;
            attempt.Local = null;
            SetDeadline(attempt, Stopwatch.GetTimestamp());
            RequestNext(attempt);
        }

        private static int RecordLength(byte[] manifest, int index) =>
            BinaryPrimitives.ReadInt32LittleEndian(manifest.AsSpan(index * ManifestEntryBytes, sizeof(int)));

        private static bool MatchesRecordDigest(byte[] manifest, int index, byte[] bytes) =>
            manifest.AsSpan(index * ManifestEntryBytes + sizeof(int), DigestBytes).SequenceEqual(Hash(bytes));

        private static void RequestNext(Attempt attempt)
        {
            attempt.ReceiveIndex = Array.IndexOf(attempt.Records, null);
            if (attempt.ReceiveIndex < 0) return;
            attempt.Received = 0;
            attempt.Records[attempt.ReceiveIndex] = new byte[RecordLength(attempt.Manifest, attempt.ReceiveIndex)];
            attempt.RequestedRecords++;
            Send(attempt, attempt.World.Host, attempt.Nonce, Kind.Request, attempt.ReceiveIndex);
        }

        private static void ReceiveChunk(Attempt attempt, MultiplayerResyncDataPacket packet)
        {
            if (attempt.ReceiveIndex != packet.Index || attempt.Received != packet.Offset)
                throw new InvalidDataException("Resync chunk arrived outside its requested record or offset.");
            byte[] record = attempt.Records[packet.Index];
            if (packet.Body.Length != Math.Min(ChunkBytes, record.Length - attempt.Received))
                throw new InvalidDataException("Invalid resync chunk length.");
            packet.Body.AsSpan().CopyTo(record.AsSpan(attempt.Received));
            attempt.Received += packet.Body.Length;
            attempt.TransferredBytes += packet.Body.Length;
            bool complete = attempt.Received == record.Length;
            if (complete && !MatchesRecordDigest(attempt.Manifest, packet.Index, record))
                throw new InvalidDataException("Resync record digest mismatch.");
            if (!complete && attempt.Received % (Window * ChunkBytes) != 0) return;
            if (Send(attempt, attempt.World.Host, attempt.Nonce, Kind.Progress, packet.Index, attempt.Received) && complete)
                RequestNext(attempt);
        }

        private static bool Send(Attempt attempt, ulong recipient, ulong nonce, Kind kind, int index = -1,
            int offset = 0, byte[] body = null)
        {
            if (!Live(attempt)) return false;
            var world = attempt.World;
            var packet = new MultiplayerResyncDataPacket(kind, world.Lobby, world.Round, world.Epoch,
                attempt.Repair, nonce, index, offset, body ?? Array.Empty<byte>());
            var result = channel.SendToSteamPeer(packet, recipient);
            if (!result.Submitted) Fail(attempt, "Could not submit " + kind + " to an admitted peer: " + result.Reason);
            return result.Submitted;
        }
    }
}
