using System;
using System.Collections.Generic;
using System.Text;

namespace APIShared.Savegames
{
    // Persisted record format, envelope version 1 (all integers little-endian):
    //   offset size field
    //   0      4    signature "APSV"
    //   4      2    envelope version of the writer (1)
    //   6      2    oldest envelope reader that can read this record (1)
    //   8      2    header size (32); version-1 readers skip header bytes beyond their own fields
    //   10     2    reserved, written as 0
    //   12     4    record version: the consumer's schema version of the writer, at least 1
    //   16     4    minimum reader version: 1 .. record version
    //   20     4    entry count: 0 .. MaximumEntries
    //   24     4    payload length: total length - header size
    //   28     4    CRC-32 (IEEE 802.3) of bytes [0, 28) and [32, end)
    //   then the entries in ordinal name order, each: u16 name byte count (1..1024), strict UTF-8
    //   name, i32 data length (0 .. MaximumEntryBytes), data.
    // A future writer that exceeds a version-1 limit or changes this layout must raise the oldest
    // envelope reader, so version-1 readers report TooNew (and keep the bytes) instead of Corrupt.
    // The limits are shared by writer and reader so a written record is always readable here (the
    // savegame settings record writes more than it reads: issue #10).
    internal static class VersionedSaveRecordCodec
    {
        internal const int MaximumEntries = 2048;               // SavegameModSettingsRecordFormatter.cs:69
        internal const int MaximumEntryBytes = 1024 * 1024;     // SavegameModSettings.cs:37
        internal const int MaximumRecordBytes = 8 * 1024 * 1024; // SavegameModSettings.cs:34
        internal const int MaximumNameLength = 256;             // SavegameModSettings.cs:490, :493
        internal const int EnvelopeVersion = 1;
        internal const int HeaderSize = 32;
        private const int CrcOffset = 28;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly uint[] CrcTable = CreateCrcTable();

        internal enum DecodeStatus { Valid, TooNew, Corrupt }

        internal sealed class Decoded
        {
            internal Decoded(DecodeStatus status, int recordVersion, int minimumReaderVersion,
                VersionedSaveEntries entries, string detail)
            {
                Status = status; RecordVersion = recordVersion; MinimumReaderVersion = minimumReaderVersion;
                Entries = entries; Detail = detail;
            }
            internal DecodeStatus Status { get; }
            internal int RecordVersion { get; }
            internal int MinimumReaderVersion { get; }
            internal VersionedSaveEntries Entries { get; }
            internal string Detail { get; }
        }

        internal static bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumNameLength) return false;
            for (int i = 0; i < name.Length; i++)
            {
                if (char.IsHighSurrogate(name[i]))
                {
                    if (i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1])) return false;
                    i++;
                }
                else if (char.IsLowSurrogate(name[i])) return false;
            }
            return true;
        }

        internal static bool HasSignature(byte[] data) =>
            data != null && data.Length >= 4 && data[0] == 'A' && data[1] == 'P' && data[2] == 'S' && data[3] == 'V';

        /// <summary>Encodes ordinal-sorted, validated entries; null when a limit would be exceeded.</summary>
        internal static byte[] Encode(int recordVersion, int minimumReaderVersion,
            ICollection<KeyValuePair<string, byte[]>> entries)
        {
            if (entries.Count > MaximumEntries) return null;
            var names = new byte[entries.Count][];
            long length = HeaderSize;
            int index = 0;
            foreach (KeyValuePair<string, byte[]> entry in entries)
            {
                names[index] = StrictUtf8.GetBytes(entry.Key);
                length += 2L + names[index].Length + 4L + entry.Value.Length;
                if (length > MaximumRecordBytes) return null;
                index++;
            }
            var data = new byte[length];
            data[0] = (byte)'A'; data[1] = (byte)'P'; data[2] = (byte)'S'; data[3] = (byte)'V';
            WriteU16(data, 4, EnvelopeVersion);
            WriteU16(data, 6, EnvelopeVersion);
            WriteU16(data, 8, HeaderSize);
            WriteU32(data, 12, (uint)recordVersion);
            WriteU32(data, 16, (uint)minimumReaderVersion);
            WriteU32(data, 20, (uint)entries.Count);
            WriteU32(data, 24, (uint)(length - HeaderSize));
            int offset = HeaderSize;
            index = 0;
            foreach (KeyValuePair<string, byte[]> entry in entries)
            {
                byte[] name = names[index++];
                WriteU16(data, offset, name.Length);
                Buffer.BlockCopy(name, 0, data, offset + 2, name.Length);
                offset += 2 + name.Length;
                WriteU32(data, offset, (uint)entry.Value.Length);
                Buffer.BlockCopy(entry.Value, 0, data, offset + 4, entry.Value.Length);
                offset += 4 + entry.Value.Length;
            }
            WriteU32(data, CrcOffset, Checksum(data));
            return data;
        }

        /// <summary>Validates every field before allocating entries; never throws for malformed data.</summary>
        internal static Decoded Decode(byte[] data, int readerVersion)
        {
            if (!HasSignature(data)) return Corrupt("The record signature is missing.");
            if (data.Length < 8) return Corrupt("The record header is truncated.");
            int writerEnvelope = ReadU16(data, 4), oldestEnvelopeReader = ReadU16(data, 6);
            if (oldestEnvelopeReader > EnvelopeVersion)
                return new Decoded(DecodeStatus.TooNew, 0, 0, null, "Envelope version " + writerEnvelope +
                    " needs envelope reader " + oldestEnvelopeReader + "; this APIShared reads " + EnvelopeVersion + ".");
            if (data.Length > MaximumRecordBytes) return Corrupt("The record exceeds " + MaximumRecordBytes + " bytes.");
            if (data.Length < HeaderSize) return Corrupt("The record header is truncated.");
            int headerSize = ReadU16(data, 8);
            if (headerSize < HeaderSize || headerSize > data.Length) return Corrupt("Invalid record header size.");
            int recordVersion = ReadI32(data, 12), minimumReader = ReadI32(data, 16);
            int count = ReadI32(data, 20), payload = ReadI32(data, 24);
            if (payload != data.Length - headerSize) return Corrupt("The record length does not match its header.");
            if (ReadU32(data, CrcOffset) != Checksum(data)) return Corrupt("The record checksum does not match.");
            if (recordVersion < 1 || minimumReader < 1 || minimumReader > recordVersion)
                return Corrupt("Invalid record version fields.");
            if (count < 0 || count > MaximumEntries) return Corrupt("Invalid record entry count.");
            if (minimumReader > readerVersion)
                return new Decoded(DecodeStatus.TooNew, recordVersion, minimumReader, null, "Record version " +
                    recordVersion + " needs reader version " + minimumReader + "; this reader is " + readerVersion + ".");
            var entries = new VersionedSaveEntries();
            int offset = headerSize;
            for (int i = 0; i < count; i++)
            {
                if (data.Length - offset < 2) return Corrupt("A record entry is truncated.");
                int nameBytes = ReadU16(data, offset);
                offset += 2;
                if (nameBytes == 0 || data.Length - offset < nameBytes) return Corrupt("A record entry name is truncated.");
                string name;
                try { name = StrictUtf8.GetString(data, offset, nameBytes); }
                catch (DecoderFallbackException) { return Corrupt("A record entry name is not valid UTF-8."); }
                offset += nameBytes;
                if (!IsValidName(name) || entries.Contains(name)) return Corrupt("Invalid or duplicate entry name.");
                if (data.Length - offset < 4) return Corrupt("A record entry is truncated.");
                int length = ReadI32(data, offset);
                offset += 4;
                if (length < 0 || length > MaximumEntryBytes || data.Length - offset < length)
                    return Corrupt("Invalid record entry length.");
                var value = new byte[length];
                Buffer.BlockCopy(data, offset, value, 0, length);
                offset += length;
                entries.SetOwned(name, value);
            }
            if (offset != data.Length) return Corrupt("The record has trailing bytes.");
            return new Decoded(DecodeStatus.Valid, recordVersion, minimumReader, entries, string.Empty);
        }

        private static Decoded Corrupt(string detail) => new Decoded(DecodeStatus.Corrupt, 0, 0, null, detail);

        private static uint Checksum(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < CrcOffset; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            for (int i = CrcOffset + 4; i < data.Length; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }

        private static void WriteU16(byte[] data, int offset, int value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteU32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }

        private static int ReadU16(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8);

        private static uint ReadU32(byte[] data, int offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

        private static int ReadI32(byte[] data, int offset) => (int)ReadU32(data, offset);
    }
}
