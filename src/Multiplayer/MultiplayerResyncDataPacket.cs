using System;
using System.Buffers;
using MessagePack;
using MessagePack.Formatters;

namespace APIShared.Multiplayer
{
    [MessagePackFormatter(typeof(MultiplayerResyncDataPacketFormatter))]
    internal sealed class MultiplayerResyncDataPacket
    {
        internal const int Protocol = 1, ChunkBytes = 8192, Window = 8, DigestBytes = 32;
        internal const int ManifestEntryBytes = sizeof(int) + DigestBytes;
        internal const int MaxRecords = 32, MaxRecordBytes = 1024 * 1024, MaxTotalBytes = 4 * MaxRecordBytes;
        internal enum Kind { Hello, Manifest, Request, Chunk, Progress, Ready }

        internal MultiplayerResyncDataPacket(Kind operation, ulong lobby, long round, long epoch, long repair,
            ulong nonce, int index, int offset, byte[] body)
        {
            Operation = operation; Lobby = lobby; Round = round; Epoch = epoch;
            Repair = repair; Nonce = nonce; Index = index; Offset = offset; Body = body;
        }
        internal readonly Kind Operation;
        internal readonly ulong Lobby, Nonce;
        internal readonly long Round, Epoch, Repair;
        internal readonly int Index, Offset;
        // The formatter owns this copy; no code mutates a packet after construction.
        internal readonly byte[] Body;
    }

    internal sealed class MultiplayerResyncDataPacketFormatter : IMessagePackFormatter<MultiplayerResyncDataPacket>
    {
        public MultiplayerResyncDataPacketFormatter() { }

        public void Serialize(ref MessagePackWriter writer, MultiplayerResyncDataPacket value, MessagePackSerializerOptions options)
        {
            Validate(value.Operation, value.Lobby, value.Round, value.Epoch, value.Repair, value.Nonce,
                value.Index, value.Offset, value.Body?.Length ?? -1);
            writer.WriteArrayHeader(10);
            writer.Write(MultiplayerResyncDataPacket.Protocol);
            writer.Write((int)value.Operation);
            writer.Write(value.Lobby);
            writer.Write(value.Round);
            writer.Write(value.Epoch);
            writer.Write(value.Repair);
            writer.Write(value.Nonce);
            writer.Write(value.Index);
            writer.Write(value.Offset);
            writer.Write(value.Body);
        }

        public MultiplayerResyncDataPacket Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.ReadArrayHeader() != 10 || reader.ReadInt32() != MultiplayerResyncDataPacket.Protocol)
                throw new InvalidOperationException("Unsupported resync data packet.");
            var operation = (MultiplayerResyncDataPacket.Kind)reader.ReadInt32();
            ulong lobby = reader.ReadUInt64();
            long round = reader.ReadInt64(), epoch = reader.ReadInt64(), repair = reader.ReadInt64();
            ulong nonce = reader.ReadUInt64();
            int index = reader.ReadInt32(), offset = reader.ReadInt32();
            var body = reader.ReadBytes();
            Validate(operation, lobby, round, epoch, repair, nonce, index, offset, body?.Length ?? -1);
            return new MultiplayerResyncDataPacket(operation, lobby, round, epoch, repair, nonce, index, offset,
                body.Value.IsEmpty ? Array.Empty<byte>() : body.Value.ToArray());
        }

        private static void Validate(MultiplayerResyncDataPacket.Kind kind, ulong lobby, long round, long epoch,
            long repair, ulong nonce, int index, int offset, long length)
        {
            if (lobby == 0 || round <= 0 || epoch <= 0 || repair <= 0 || nonce == 0 ||
                offset < 0 || offset > MultiplayerResyncDataPacket.MaxRecordBytes)
                throw new InvalidOperationException("Invalid resync data packet identity.");
            bool record = index >= 0 && index < MultiplayerResyncDataPacket.MaxRecords;
            bool valid;
            switch (kind)
            {
                case MultiplayerResyncDataPacket.Kind.Hello: valid = index == -1 && offset == 0 && length == 0; break;
                case MultiplayerResyncDataPacket.Kind.Manifest:
                    valid = index == -1 && offset == 0 && length > 0 &&
                        length <= MultiplayerResyncDataPacket.MaxRecords * MultiplayerResyncDataPacket.ManifestEntryBytes &&
                        length % MultiplayerResyncDataPacket.ManifestEntryBytes == 0;
                    break;
                case MultiplayerResyncDataPacket.Kind.Request: valid = record && offset == 0 && length == 0; break;
                case MultiplayerResyncDataPacket.Kind.Chunk:
                    valid = record && length > 0 && length <= MultiplayerResyncDataPacket.ChunkBytes &&
                        offset % MultiplayerResyncDataPacket.ChunkBytes == 0;
                    break;
                case MultiplayerResyncDataPacket.Kind.Progress: valid = record && offset > 0 && length == 0; break;
                case MultiplayerResyncDataPacket.Kind.Ready:
                    valid = index == -1 && offset == 0 && length == MultiplayerResyncDataPacket.DigestBytes;
                    break;
                default: valid = false; break;
            }
            if (!valid) throw new InvalidOperationException("Invalid resync data packet body.");
        }
    }
}
