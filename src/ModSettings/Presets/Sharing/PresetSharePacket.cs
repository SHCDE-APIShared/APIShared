using MessagePack;
using MessagePack.Formatters;
using System;
using System.Buffers;
using System.Text;

namespace APIShared.ModSettings
{
    internal enum PresetShareOperation
    {
        Offer = 1,
        Request,
        Response,
        Unavailable,
        OfferAcknowledged,
        Busy
    }

    // Immutable strings and identifiers make it safe for the control-plane channel to
    // pass this snapshot through its FIFO without a second MessagePack round trip.
    [MessagePackFormatter(typeof(PresetSharePacketFormatter))]
    internal sealed class PresetSharePacket
    {
        internal const int ProtocolVersion = 2;
        internal const int MaximumJsonBytes = 64 * 1024;
        internal const int MaximumBodyBytes = MaximumJsonBytes + 2048;
        internal const int MaximumModGuidBytes = 256;
        internal const int MaximumPresetNameBytes = 1024;
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal PresetSharePacket(PresetShareOperation operation, ulong lobby, Guid fromMembership,
            Guid toMembership, Guid offerId, Guid requestId, string modGuid = null, string presetName = null,
            string json = null, ulong offerSequence = 0)
        {
            Operation = operation;
            Lobby = lobby;
            FromMembership = fromMembership;
            ToMembership = toMembership;
            OfferId = offerId;
            RequestId = requestId;
            ModGuid = modGuid;
            PresetName = presetName;
            Json = json;
            OfferSequence = offerSequence;
        }

        internal PresetShareOperation Operation { get; }
        internal ulong Lobby { get; }
        internal Guid FromMembership { get; }
        internal Guid ToMembership { get; }
        internal Guid OfferId { get; }
        internal Guid RequestId { get; }
        internal string ModGuid { get; }
        internal string PresetName { get; }
        internal string Json { get; }
        internal ulong OfferSequence { get; }

        internal bool IsValidShape()
        {
            if (Lobby == 0 || FromMembership == Guid.Empty || ToMembership == Guid.Empty || OfferId == Guid.Empty)
                return false;
            if (Operation == PresetShareOperation.Offer)
                return OfferSequence != 0 && RequestId == Guid.Empty && !string.IsNullOrWhiteSpace(ModGuid) &&
                    !string.IsNullOrWhiteSpace(PresetName) && Json == null;
            if (OfferSequence != 0 || ModGuid != null || PresetName != null)
                return false;
            if (Operation == PresetShareOperation.OfferAcknowledged)
                return RequestId == Guid.Empty && Json == null;
            if (RequestId == Guid.Empty)
                return false;
            switch (Operation)
            {
                case PresetShareOperation.Response:
                    return !string.IsNullOrWhiteSpace(Json);
                case PresetShareOperation.Request:
                case PresetShareOperation.Unavailable:
                case PresetShareOperation.Busy:
                    return Json == null;
                default:
                    return false;
            }
        }
    }

    internal sealed class PresetSharePacketFormatter : IMessagePackFormatter<PresetSharePacket>
    {
        private const string WireTag = "APIShared.PresetShare";
        private const int Fields = 12;

        public PresetSharePacketFormatter() { }

        public void Serialize(ref MessagePackWriter writer, PresetSharePacket packet, MessagePackSerializerOptions options)
        {
            if (packet == null || !packet.IsValidShape())
                throw new InvalidOperationException("Invalid preset-sharing packet.");
            writer.WriteArrayHeader(Fields);
            WriteText(ref writer, WireTag, 32);
            writer.Write(PresetSharePacket.ProtocolVersion);
            writer.Write((int)packet.Operation);
            writer.Write(packet.Lobby);
            writer.Write(packet.FromMembership.ToByteArray());
            writer.Write(packet.ToMembership.ToByteArray());
            writer.Write(packet.OfferId.ToByteArray());
            writer.Write(packet.RequestId.ToByteArray());
            WriteText(ref writer, packet.ModGuid, PresetSharePacket.MaximumModGuidBytes);
            WriteText(ref writer, packet.PresetName, PresetSharePacket.MaximumPresetNameBytes);
            WriteText(ref writer, packet.Json, PresetSharePacket.MaximumJsonBytes);
            writer.Write(packet.OfferSequence);
        }

        public PresetSharePacket Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.ReadArrayHeader() != Fields || ReadText(ref reader, 32) != WireTag ||
                reader.ReadInt32() != PresetSharePacket.ProtocolVersion)
                throw new InvalidOperationException("Unsupported preset-sharing packet.");
            var packet = new PresetSharePacket((PresetShareOperation)reader.ReadInt32(), reader.ReadUInt64(),
                ReadGuid(ref reader), ReadGuid(ref reader), ReadGuid(ref reader), ReadGuid(ref reader),
                ReadText(ref reader, PresetSharePacket.MaximumModGuidBytes),
                ReadText(ref reader, PresetSharePacket.MaximumPresetNameBytes),
                ReadText(ref reader, PresetSharePacket.MaximumJsonBytes), reader.ReadUInt64());
            if (!packet.IsValidShape() || !reader.End)
                throw new InvalidOperationException("Invalid preset-sharing packet.");
            return packet;
        }

        private static Guid ReadGuid(ref MessagePackReader reader)
        {
            ReadOnlySequence<byte>? value = reader.ReadBytes();
            if (!value.HasValue || value.Value.Length != 16)
                throw new InvalidOperationException("Expected a 16-byte preset-sharing identity.");
            return new Guid(value.Value.ToArray());
        }

        private static string ReadText(ref MessagePackReader reader, int maximum)
        {
            ReadOnlySequence<byte>? value = reader.ReadBytes();
            if (!value.HasValue)
                return null;
            if (value.Value.Length > maximum)
                throw new InvalidOperationException("Preset-sharing field exceeds its byte limit.");
            return PresetSharePacket.Utf8.GetString(value.Value.ToArray());
        }

        private static void WriteText(ref MessagePackWriter writer, string value, int maximum)
        {
            if (value == null)
            {
                writer.WriteNil();
                return;
            }
            if (PresetSharePacket.Utf8.GetByteCount(value) > maximum)
                throw new InvalidOperationException("Preset-sharing field exceeds its byte limit.");
            writer.Write(PresetSharePacket.Utf8.GetBytes(value));
        }
    }
}
