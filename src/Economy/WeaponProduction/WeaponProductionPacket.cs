using System;
using MessagePack;
using MessagePack.Formatters;

namespace APIShared.Economy
{
    [MessagePackFormatter(typeof(WeaponProductionPacketFormatter))]
    internal sealed class WeaponProductionPacket
    {
        internal WeaponProductionPacket(string catalog, int optionIndex, int buildingId, uint buildingGlobalId)
        { Catalog = catalog; OptionIndex = optionIndex; BuildingId = buildingId; BuildingGlobalId = buildingGlobalId; }

        internal string Catalog { get; }
        internal int OptionIndex { get; }
        // Zero identifies a type-wide operation. Its type comes from the catalogue entry.
        internal int BuildingId { get; }
        internal uint BuildingGlobalId { get; }
        internal bool IsValid => Catalog != null && Catalog.Length == 64 && OptionIndex >= 1 && OptionIndex <= 20 &&
            (BuildingId == 0 ? BuildingGlobalId == 0 : BuildingId > 0 && BuildingGlobalId != 0);
    }

    internal sealed class WeaponProductionPacketFormatter : IMessagePackFormatter<WeaponProductionPacket>
    {
        public WeaponProductionPacketFormatter() { }

        public void Serialize(ref MessagePackWriter writer, WeaponProductionPacket value, MessagePackSerializerOptions options)
        {
            if (value == null || !value.IsValid) throw new ArgumentException("Invalid workshop selection.");
            writer.WriteArrayHeader(5);
            writer.Write(1);
            writer.Write(value.Catalog);
            writer.Write(value.OptionIndex);
            writer.Write(value.BuildingId);
            writer.Write(value.BuildingGlobalId);
        }

        public WeaponProductionPacket Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.ReadArrayHeader() != 5 || reader.ReadInt32() != 1)
                throw new MessagePackSerializationException("Unsupported workshop selection packet.");
            var packet = new WeaponProductionPacket(reader.ReadString(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadUInt32());
            if (!packet.IsValid) throw new MessagePackSerializationException("Invalid workshop selection.");
            return packet;
        }
    }
}
