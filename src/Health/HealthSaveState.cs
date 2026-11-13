using System;
using System.Collections.Generic;
using System.IO;

namespace APIShared.Health
{
    internal sealed class HealthRecord
    {
        internal HealthObjectKind Kind;
        internal int Id, Type;
        internal uint GlobalId, Generation, Basis, AppliedMaximum, ProxyBasis, ProxyMaximum;
        internal long Key => KeyFor(Kind,Id);
        internal static long KeyFor(HealthObjectKind kind,int id) => ((long)kind << 32) | (uint)id;
    }

    // An explicit bounded binary archive, not reflection serialization. Raw engine capacities in
    // the audited build are <16384 combined records; the limit also bounds hostile input allocation.
    internal sealed class HealthSaveState
    {
        internal const int MaximumRecords = 16384;
        private const uint Magic = 0x31504841;
        private const int Version = 1, HeaderBytes = 13, RecordBytes = 33;
        internal bool FullBasis, LegacyLords;
        internal readonly List<HealthRecord> Records = new List<HealthRecord>();

        internal byte[] Encode()
        {
            if (Records.Count > MaximumRecords) throw new InvalidOperationException("Too many HP records.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic); writer.Write(Version);
                writer.Write((byte)((FullBasis ? 1 : 0) | (LegacyLords ? 2 : 0))); writer.Write(Records.Count);
                var identities = new HashSet<long>();
                foreach (var item in Records)
                {
                    Validate(item,identities);
                    writer.Write((byte)item.Kind); writer.Write(item.Id); writer.Write(item.GlobalId);
                    writer.Write(item.Type); writer.Write(item.Generation); writer.Write(item.Basis);
                    writer.Write(item.AppliedMaximum); writer.Write(item.ProxyBasis); writer.Write(item.ProxyMaximum);
                }
                writer.Flush(); return stream.ToArray();
            }
        }
        internal static HealthSaveState Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < HeaderBytes || bytes.Length > HeaderBytes + MaximumRecords * RecordBytes)
                throw new InvalidOperationException("Invalid HP archive length.");
            using (var stream = new MemoryStream(bytes,false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
                    throw new InvalidOperationException("Unsupported HP archive schema.");
                byte flags = reader.ReadByte(); int count = reader.ReadInt32();
                if (flags > 3 || count < 0 || count > MaximumRecords || bytes.Length != HeaderBytes + count * RecordBytes)
                    throw new InvalidOperationException("Invalid HP archive header.");
                var state = new HealthSaveState { FullBasis = (flags & 1) != 0, LegacyLords = (flags & 2) != 0 };
                var identities = new HashSet<long>();
                for (int i = 0; i < count; i++)
                {
                    var item = new HealthRecord { Kind = (HealthObjectKind)reader.ReadByte(), Id = reader.ReadInt32(),
                        GlobalId = reader.ReadUInt32(), Type = reader.ReadInt32(), Generation = reader.ReadUInt32(),
                        Basis = reader.ReadUInt32(), AppliedMaximum = reader.ReadUInt32(),
                        ProxyBasis = reader.ReadUInt32(), ProxyMaximum = reader.ReadUInt32() };
                    Validate(item,identities); state.Records.Add(item);
                }
                return state;
            }
        }
        private static void Validate(HealthRecord item,HashSet<long> identities)
        {
            if (item == null || (item.Kind != HealthObjectKind.Unit && item.Kind != HealthObjectKind.Building) ||
                item.Id <= 0 || item.Id > MaximumRecords || item.GlobalId == 0 || item.Type < 0 || item.Type > ushort.MaxValue ||
                item.Generation == 0 || item.Basis == 0 || item.AppliedMaximum == 0 ||
                // A saved pending generation has its frozen proxy basis but no applied target yet.
                (item.ProxyBasis == 0 && item.ProxyMaximum != 0) || item.ProxyBasis > short.MaxValue ||
                item.ProxyMaximum > short.MaxValue || !identities.Add(item.Key))
                throw new InvalidOperationException("Invalid or duplicate HP archive record.");
        }
    }
}
