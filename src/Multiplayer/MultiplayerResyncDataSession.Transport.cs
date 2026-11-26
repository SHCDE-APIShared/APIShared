using System;
using System.Buffers.Binary;
using System.IO;
using APIShared.Networking;
using static APIShared.Multiplayer.MultiplayerResyncDataPacket;

namespace APIShared.Multiplayer
{
    internal sealed partial class MultiplayerResyncDataSession
    {
        internal void Receive(ulong sender, MultiplayerResyncDataPacket packet)
        {
            lock (Gate)
            {
                var attempt = current;
                if (!Live(attempt) || attempt.Released || packet.Lobby != attempt.World.Lobby ||
                    packet.Round != attempt.World.Round || packet.Epoch != attempt.World.Epoch ||
                    packet.Repair != attempt.Repair) return;

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

        private void UpdateRosterProof(Attempt attempt)
        {
            attempt.RosterVerified = services.VerifyRoster(attempt.World);
            if (!attempt.RosterVerified) Fail(attempt, "The admitted resync roster changed.");
        }

        private void ReceiveFromClient(Attempt attempt, Peer peer, ulong sender, MultiplayerResyncDataPacket packet)
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
                    if (attempt.Manifest == null || packet.Index >= attempt.Records.Length || peer.Ready)
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

            SendWindow(attempt, sender, peer);
        }

        private void SendWindow(Attempt attempt, ulong recipient, Peer peer)
        {
            byte[] record = attempt.Records[peer.Index];
            int end = GetHostWindowEnd(attempt.Prepared != null, record.Length, peer.Acknowledged);
            while (peer.Sent < end && Live(attempt))
            {
                int length = Math.Min(ChunkBytes, end - peer.Sent);
                if (!Send(attempt, recipient, peer.Nonce, Kind.Chunk, peer.Index, peer.Sent,
                    record.AsSpan(peer.Sent, length).ToArray())) return;
                peer.Sent += length;
                attempt.TransferredBytes += length;
            }
        }

        // Early requests are remembered without competing with native repair payloads.
        internal static int GetHostWindowEnd(bool hostPrepared, int length, int acknowledged) =>
            hostPrepared ? Math.Min(length, acknowledged + Window * ChunkBytes) : 0;

        private void StartDeferredWindows(Attempt attempt)
        {
            foreach (var peer in attempt.Peers.Values)
            {
                if (peer.Index < 0) continue;
                QueueUnity(attempt, () => SendDeferredWindows(attempt));
                return;
            }
            if (!attempt.RosterVerified) QueueUnity(attempt, () => UpdateRosterProof(attempt));
        }

        private void SendDeferredWindows(Attempt attempt)
        {
            foreach (var entry in attempt.Peers)
                if (entry.Value.Index >= 0 && entry.Value.Sent == 0)
                    SendWindow(attempt, entry.Key, entry.Value);
        }

        private void SetManifest(Attempt attempt)
        {
            attempt.Manifest = EncodeManifest(attempt.Records);
            attempt.ManifestHash = Hash(attempt.Manifest);
        }

        private void AdvertiseManifest(Attempt attempt)
        {
            foreach (var peer in attempt.Peers)
                if (peer.Value.Nonce != 0)
                    Send(attempt, peer.Key, peer.Value.Nonce, Kind.Manifest, body: attempt.Manifest);
            if (Live(attempt)) UpdateRosterProof(attempt);
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

        private void ReceiveManifest(Attempt attempt, byte[] manifest)
        {
            if (attempt.Manifest != null)
            {
                if (!manifest.AsSpan().SequenceEqual(attempt.Manifest))
                    throw new InvalidDataException("The host changed its resync manifest.");
                return;
            }

            if (manifest.Length != providers.Count * ManifestEntryBytes)
                throw new InvalidDataException("Unexpected resync manifest record count.");
            var records = new byte[providers.Count][];
            int total = 0;
            for (int i = 0; i < records.Length; i++)
            {
                int length = RecordLength(manifest, i);
                if (length < 0 || length > providers[i].Maximum || (total += length) > MaxTotalBytes)
                    throw new InvalidDataException("Resync manifest record exceeds its admitted bound.");
                byte[] candidate = length == 0 ? Array.Empty<byte>() : attempt.Local[i];
                if (candidate.Length == length && MatchesRecordDigest(manifest, i, candidate)) records[i] = candidate;
                else if (length == 0) throw new InvalidDataException("Invalid digest for an empty resync record.");
            }

            attempt.Manifest = manifest;
            attempt.ManifestHash = Hash(manifest);
            attempt.Records = records;
            attempt.Local = null;
            // An early manifest can arrive while Vanilla still has substantial repair work.
            if (attempt.Deadline != 0) SetDeadline(attempt, services.Timestamp());
            RequestNext(attempt);
        }

        private static int RecordLength(byte[] manifest, int index) =>
            BinaryPrimitives.ReadInt32LittleEndian(manifest.AsSpan(index * ManifestEntryBytes, sizeof(int)));

        private static bool MatchesRecordDigest(byte[] manifest, int index, byte[] bytes) =>
            manifest.AsSpan(index * ManifestEntryBytes + sizeof(int), DigestBytes).SequenceEqual(Hash(bytes));

        private void RequestNext(Attempt attempt)
        {
            attempt.ReceiveIndex = Array.IndexOf(attempt.Records, null);
            if (attempt.ReceiveIndex < 0) return;
            attempt.Received = 0;
            attempt.Records[attempt.ReceiveIndex] = new byte[RecordLength(attempt.Manifest, attempt.ReceiveIndex)];
            attempt.RequestedRecords++;
            Send(attempt, attempt.World.Host, attempt.Nonce, Kind.Request, attempt.ReceiveIndex);
        }

        private void ReceiveChunk(Attempt attempt, MultiplayerResyncDataPacket packet)
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

        private bool Send(Attempt attempt, ulong recipient, ulong nonce, Kind kind, int index = -1,
            int offset = 0, byte[] body = null)
        {
            if (!Live(attempt)) return false;
            var world = attempt.World;
            var packet = new MultiplayerResyncDataPacket(kind, world.Lobby, world.Round, world.Epoch,
                attempt.Repair, nonce, index, offset, body ?? Array.Empty<byte>());
            var result = services.Send(recipient, packet);
            if (!result.Submitted) Fail(attempt, "Could not submit " + kind + " to an admitted peer: " + result.Reason);
            return result.Submitted;
        }
    }
}
