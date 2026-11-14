using System;
using MessagePack;
using MessagePack.Formatters;

namespace APIShared.Buildings
{
    // One owner request for one value of one building identity. The table is named by its
    // owner GUID and registration ID, so registration order never changes its meaning.
    [MessagePackFormatter(typeof(BuildingStatePacketFormatter))]
    internal sealed class BuildingStatePacket
    {
        internal const int Protocol = 1;
        internal const int MaximumNameLength = 128;

        internal BuildingStatePacket(string owner, string id, int buildingId, uint globalId, int valueIndex, byte value, int serial)
        {
            Owner = owner; Id = id; BuildingId = buildingId; GlobalId = globalId;
            ValueIndex = valueIndex; Value = value; Serial = serial;
        }

        internal string Owner { get; }
        internal string Id { get; }
        internal int BuildingId { get; }
        internal uint GlobalId { get; }
        internal int ValueIndex { get; }
        internal byte Value { get; }
        // Presentation only: lets the sending peer recognise its own latest request.
        internal int Serial { get; }

        internal bool IsValid => IsName(Owner) && IsName(Id) && BuildingStateMemory.IsValidId(BuildingId) &&
            GlobalId != 0 && ValueIndex >= 0 && ValueIndex < BuildingStateMemory.MaximumValueCount && Serial > 0;

        internal static bool IsName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumNameLength;
    }

    internal sealed class BuildingStatePacketFormatter : IMessagePackFormatter<BuildingStatePacket>
    {
        public BuildingStatePacketFormatter() { }

        public void Serialize(ref MessagePackWriter writer, BuildingStatePacket value, MessagePackSerializerOptions options)
        {
            if (value == null || !value.IsValid) throw new ArgumentException("Invalid building state request.");
            writer.WriteArrayHeader(8);
            writer.Write(BuildingStatePacket.Protocol);
            writer.Write(value.Owner);
            writer.Write(value.Id);
            writer.Write(value.BuildingId);
            writer.Write(value.GlobalId);
            writer.Write(value.ValueIndex);
            writer.Write(value.Value);
            writer.Write(value.Serial);
        }

        public BuildingStatePacket Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.ReadArrayHeader() != 8 || reader.ReadInt32() != BuildingStatePacket.Protocol)
                throw new MessagePackSerializationException("Unsupported building state request.");
            var packet = new BuildingStatePacket(reader.ReadString(), reader.ReadString(), reader.ReadInt32(),
                reader.ReadUInt32(), reader.ReadInt32(), reader.ReadByte(), reader.ReadInt32());
            if (!packet.IsValid) throw new MessagePackSerializationException("Invalid building state request.");
            return packet;
        }
    }
}
