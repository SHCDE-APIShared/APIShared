using APIShared.Savegames;
using MessagePack;
using MessagePack.Formatters;
using System;
using System.Collections.Generic;

namespace APIShared
{
    [MessagePackFormatter(typeof(SavegameLoadPacketFormatter))]
    internal sealed class SavegameLoadPacket
    {
        /// <summary>Protocol 1, the first readiness protocol (development builds only, never released):
        /// nine fields, no version, no record digests.</summary>
        internal const int LegacyVersion = 1;
        /// <summary>Protocol 2: adds the version, declared simulation record digests and a cancel reason.</summary>
        internal const int CurrentVersion = 2;
        internal const int MaximumReasonLength = 1024;

        internal int Version = CurrentVersion;
        internal int Operation;
        internal ulong Lobby;
        internal string Token, SaveName, Fingerprint, Roster;
        internal int Checksum, Time;
        internal byte[] Metadata;
        internal IReadOnlyList<SavegameRecordDigest> Records = Array.Empty<SavegameRecordDigest>();
        internal string Reason;
    }

    // Protocol 1 is a nine-element array starting with the operation. Protocol 2 and every later
    // version start with the version and never use nine elements, so each build recognizes a
    // mismatched peer instead of misreading it. A packet from an unknown later version keeps only its
    // version (Operation -1); the coordinator then blocks the load with a version message.
    internal sealed class SavegameLoadPacketFormatter : IMessagePackFormatter<SavegameLoadPacket>
    {
        private const int LegacyFields = 9, CurrentFields = 12;
        public SavegameLoadPacketFormatter() { }
        public void Serialize(ref MessagePackWriter writer, SavegameLoadPacket value, MessagePackSerializerOptions options)
        {
            if (value == null) { writer.WriteNil(); return; }
            if (value.Version == SavegameLoadPacket.LegacyVersion)
            {
                // Only for telling a protocol-1 peer that this load is cancelled.
                writer.WriteArrayHeader(LegacyFields);
                WriteCommon(ref writer, value);
                return;
            }
            writer.WriteArrayHeader(CurrentFields);
            writer.Write(value.Version);
            WriteCommon(ref writer, value);
            IReadOnlyList<SavegameRecordDigest> records = value.Records ?? Array.Empty<SavegameRecordDigest>();
            writer.WriteArrayHeader(records.Count);
            foreach (SavegameRecordDigest record in records)
            {
                writer.WriteArrayHeader(4);
                writer.Write(record.Owner); writer.Write(record.Identifier);
                writer.Write((int)record.State); writer.Write(record.Hash);
            }
            writer.Write(value.Reason);
        }

        private static void WriteCommon(ref MessagePackWriter writer, SavegameLoadPacket value)
        {
            writer.Write(value.Operation); writer.Write(value.Lobby);
            writer.Write(value.Token); writer.Write(value.SaveName); writer.Write(value.Fingerprint);
            writer.Write(value.Roster); writer.Write(value.Checksum); writer.Write(value.Time); writer.Write(value.Metadata);
        }

        public SavegameLoadPacket Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil()) return null;
            int fields = reader.ReadArrayHeader();
            int version = SavegameLoadPacket.LegacyVersion;
            if (fields != LegacyFields)
            {
                if (fields < 1) throw new InvalidOperationException("Unsupported save readiness packet.");
                version = reader.ReadInt32();
                if (version != SavegameLoadPacket.CurrentVersion || fields != CurrentFields)
                {
                    for (int i = 1; i < fields; i++) reader.Skip();
                    return new SavegameLoadPacket { Version = version, Operation = -1 };
                }
            }
            var value = new SavegameLoadPacket { Version = version, Operation = reader.ReadInt32(), Lobby = reader.ReadUInt64(),
                Token = reader.ReadString(), SaveName = reader.ReadString(), Fingerprint = reader.ReadString(),
                Roster = reader.ReadString(), Checksum = reader.ReadInt32(), Time = reader.ReadInt32() };
            var bytes = reader.ReadBytes();
            if (bytes.HasValue && bytes.Value.Length > 65536) throw new InvalidOperationException("Save provenance exceeds limit.");
            value.Metadata = bytes.HasValue ? System.Buffers.BuffersExtensions.ToArray(bytes.Value) : null;
            if (version == SavegameLoadPacket.LegacyVersion) return value;
            int count = reader.ReadArrayHeader();
            if (count > SavegameRecordComparison.MaximumRecords) throw new InvalidOperationException("Too many saved simulation records.");
            var records = new SavegameRecordDigest[count];
            for (int i = 0; i < count; i++)
            {
                if (reader.ReadArrayHeader() != 4) throw new InvalidOperationException("Invalid saved simulation record.");
                string owner = reader.ReadString(), identifier = reader.ReadString();
                int state = reader.ReadInt32();
                var hash = reader.ReadBytes();
                if (owner == null || owner.Length > SavegameRecordComparison.MaximumOwnerLength ||
                    identifier == null || identifier.Length > SavegameRecordComparison.MaximumIdentifierLength ||
                    state < 0 || state > (int)SavegameRecordState.Unreadable ||
                    (hash.HasValue ? hash.Value.Length : 0) > SavegameRecordDigest.HashBytes)
                    throw new InvalidOperationException("Invalid saved simulation record.");
                records[i] = new SavegameRecordDigest(owner, identifier, (SavegameRecordState)state,
                    hash.HasValue ? System.Buffers.BuffersExtensions.ToArray(hash.Value) : null);
            }
            value.Records = records;
            value.Reason = reader.ReadString();
            if (value.Reason?.Length > SavegameLoadPacket.MaximumReasonLength) throw new InvalidOperationException("Save readiness reason exceeds limit.");
            return value;
        }
    }
}
