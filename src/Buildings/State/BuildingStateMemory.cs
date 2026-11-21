using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace APIShared.Buildings
{
    // The native-readable value table of one BuildingStateTable. An entry belongs to exactly one
    // building identity (one-based ID plus GlobalId): reads for another identity return zero, and
    // the first write for a new identity clears the earlier values, so a destroyed building's entry
    // is inert and a new building on a reused slot starts clean. The block is never freed because
    // published native consumer code reads it until the process ends. Writers: the authenticated
    // Chore and simulation writes during play, archive loads and mission starts while the
    // simulation is not running. All of them produce identical bytes on every peer.
    internal sealed unsafe class BuildingStateMemory
    {
        // Far above the game's building array (the weapon-workshop owner audits 3999); IDs above it
        // are simply not stored. Native readers compare unsigned against this capacity.
        internal const int Capacity = 0x2000;
        internal const int GlobalIdOffset = 0;
        internal const int ValuesOffset = 4;
        internal const int MaximumValueCount = 12;
        private const int EncodedHeaderBytes = 1 + sizeof(int);

        private readonly byte* entries;

        internal BuildingStateMemory(byte[] maxima)
        {
            if (maxima == null || maxima.Length < 1 || maxima.Length > MaximumValueCount)
                throw new ArgumentOutOfRangeException(nameof(maxima));
            Maxima = (byte[])maxima.Clone();
            ValueCount = maxima.Length;
            // A power of two, so native code can address an entry with one scaled index.
            EntrySize = ValuesOffset + ValueCount <= 8 ? 8 : 16;
            entries = (byte*)Marshal.AllocHGlobal(Capacity * EntrySize);
            Clear();
        }

        internal int ValueCount { get; }
        internal int EntrySize { get; }
        internal byte[] Maxima { get; }
        internal ulong Address => (ulong)entries;
        private int EncodedEntryBytes => sizeof(ushort) + sizeof(uint) + ValueCount;

        internal static bool IsValidId(int buildingId) => buildingId > 0 && buildingId < Capacity;

        internal void Clear() => new Span<byte>(entries, Capacity * EntrySize).Clear();

        internal byte Get(int buildingId, uint globalId, int index)
        {
            if (!IsValidId(buildingId) || globalId == 0 || (uint)index >= (uint)ValueCount) return 0;
            byte* entry = entries + buildingId * EntrySize;
            return *(uint*)(entry + GlobalIdOffset) == globalId ? entry[ValuesOffset + index] : (byte)0;
        }

        // Only values in range are written; a new identity first clears the entry's earlier values.
        internal bool Set(int buildingId, uint globalId, int index, byte value)
        {
            if (!IsValidId(buildingId) || globalId == 0 || (uint)index >= (uint)ValueCount || value > Maxima[index])
                return false;
            byte* entry = entries + buildingId * EntrySize;
            if (*(uint*)(entry + GlobalIdOffset) != globalId)
            {
                new Span<byte>(entry + ValuesOffset, EntrySize - ValuesOffset).Clear();
                *(uint*)(entry + GlobalIdOffset) = globalId;
            }
            entry[ValuesOffset + index] = value;
            return true;
        }

        // Format: value count (byte), entry count (int32), then ascending entries of
        // ID (uint16), GlobalId (uint32) and one byte per value; all little-endian.
        // Entries whose values are all zero are omitted. Equal state gives equal bytes on every peer.
        internal byte[] Encode()
        {
            int count = 0;
            for (int id = 1; id < Capacity; id++)
                if (IsUsed(entries + id * EntrySize)) count++;

            var data = new byte[EncodedHeaderBytes + count * EncodedEntryBytes];
            data[0] = (byte)ValueCount;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(1), count);
            int offset = EncodedHeaderBytes;
            for (int id = 1; id < Capacity && offset < data.Length; id++)
            {
                byte* entry = entries + id * EntrySize;
                if (!IsUsed(entry)) continue;
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), (ushort)id);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 2), *(uint*)(entry + GlobalIdOffset));
                for (int index = 0; index < ValueCount; index++)
                    data[offset + 6 + index] = entry[ValuesOffset + index];
                offset += EncodedEntryBytes;
            }
            return data;
        }

        // Replaces the table. A record written with fewer values (an older registration that has
        // since added values at the end) restores them and leaves the new ones at zero. Damaged data,
        // more values than registered or a value above its maximum leave the table empty and return false.
        internal bool TryRestore(byte[] data)
        {
            Clear();
            if (data == null || data.Length < EncodedHeaderBytes) return false;
            int savedValues = data[0];
            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(1));
            int entryBytes = sizeof(ushort) + sizeof(uint) + savedValues;
            if (savedValues < 1 || savedValues > ValueCount || count < 0 || count >= Capacity ||
                data.Length != EncodedHeaderBytes + (long)count * entryBytes)
                return false;

            int previous = 0;
            for (int item = 0; item < count; item++)
            {
                int offset = EncodedHeaderBytes + item * entryBytes;
                int id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
                uint globalId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 2));
                if (!IsValidId(id) || id <= previous || globalId == 0) { Clear(); return false; }
                previous = id;
                byte* entry = entries + id * EntrySize;
                *(uint*)(entry + GlobalIdOffset) = globalId;
                for (int index = 0; index < savedValues; index++)
                {
                    byte value = data[offset + 6 + index];
                    if (value > Maxima[index]) { Clear(); return false; }
                    entry[ValuesOffset + index] = value;
                }
            }
            return true;
        }

        private bool IsUsed(byte* entry)
        {
            for (int index = 0; index < ValueCount; index++)
                if (entry[ValuesOffset + index] != 0) return true;
            return false;
        }
    }
}
