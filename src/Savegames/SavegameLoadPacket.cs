using MessagePack;
using MessagePack.Formatters;
using System;

namespace APIShared
{
    [MessagePackFormatter(typeof(SavegameLoadPacketFormatter))]
    internal sealed class SavegameLoadPacket
    {
        internal int Operation;
        internal ulong Lobby;
        internal string Token, SaveName, Fingerprint, Roster;
        internal int Checksum, Time;
        internal byte[] Metadata;
    }
    internal sealed class SavegameLoadPacketFormatter : IMessagePackFormatter<SavegameLoadPacket>
    {
        public SavegameLoadPacketFormatter() { }
        public void Serialize(ref MessagePackWriter writer, SavegameLoadPacket value, MessagePackSerializerOptions options)
        {
            if (value == null) { writer.WriteNil(); return; }
            writer.WriteArrayHeader(9); writer.Write(value.Operation); writer.Write(value.Lobby);
            writer.Write(value.Token); writer.Write(value.SaveName); writer.Write(value.Fingerprint);
            writer.Write(value.Roster); writer.Write(value.Checksum); writer.Write(value.Time); writer.Write(value.Metadata);
        }
        public SavegameLoadPacket Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil()) return null;
            if (reader.ReadArrayHeader() != 9) throw new InvalidOperationException("Unsupported save readiness packet.");
            var value = new SavegameLoadPacket { Operation = reader.ReadInt32(), Lobby = reader.ReadUInt64(),
                Token = reader.ReadString(), SaveName = reader.ReadString(), Fingerprint = reader.ReadString(),
                Roster = reader.ReadString(), Checksum = reader.ReadInt32(), Time = reader.ReadInt32() };
            var bytes = reader.ReadBytes();
            if (bytes.HasValue && bytes.Value.Length > 65536) throw new InvalidOperationException("Save provenance exceeds limit.");
            value.Metadata = bytes.HasValue ? System.Buffers.BuffersExtensions.ToArray(bytes.Value) : null;
            return value;
        }
    }
}
