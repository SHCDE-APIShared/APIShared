using System;
using System.Buffers.Binary;
using System.IO;
using APIShared.Multiplayer;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Buildings
{
    public static partial class BuildingStates
    {
        internal static bool TryRegisterResync(BuildingStateTable table, out string reason)
        {
            return MultiplayerResyncData.TryRegister<byte[]>(table.OwnerGuid, table.RegistrationId, 1,
                table.Memory.MaximumResyncBytes, () => table.Memory.CaptureResync(ReadIdentity),
                data => table.Memory.PrepareResync(data.Span, ReadIdentity), table.Memory.CommitResync, out reason);
        }

        private static unsafe BuildingStateMemory.ResyncIdentity? ReadIdentity(int id, uint globalId)
        {
            var manager = GameBuildingManagerAPI.Instance;
            if (manager == null || !BuildingAccess.TryGetById(manager, id, out GameBuilding* building, out _) || building == null ||
                building->r_AliveState != AliveState.IsAlive || building->r_GlobalId != globalId)
                return null;
            return new BuildingStateMemory.ResyncIdentity(building->r_PlayerIdOwner, (int)building->r_BuildingType);
        }
    }

    internal sealed unsafe partial class BuildingStateMemory
    {
        internal readonly struct ResyncIdentity
        {
            internal ResyncIdentity(ushort owner, int type) { Owner = owner; Type = type; }
            internal readonly ushort Owner;
            internal readonly int Type;
        }

        // Each ascending row stores ID, GlobalId, owner, type and the declared value bytes.
        // Save records keep their existing format and older value-count compatibility.
        private int ResyncRowBytes => sizeof(ushort) + sizeof(uint) + sizeof(ushort) + sizeof(int) + ValueCount;
        internal int MaximumResyncBytes => sizeof(int) + (Capacity - 1) * ResyncRowBytes;

        internal byte[] CaptureResync(Func<int, uint, ResyncIdentity?> readIdentity)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(0);
            int count = 0;
            for (int id = 1; id < Capacity; id++)
            {
                byte* entry = entries + id * EntrySize;
                if (!IsUsed(entry)) continue;
                uint global = *(uint*)entry;
                if (global == 0 || readIdentity(id, global) is not ResyncIdentity identity) continue;
                writer.Write((ushort)id);
                writer.Write(global);
                writer.Write(identity.Owner);
                writer.Write(identity.Type);
                for (int index = 0; index < ValueCount; index++) writer.Write(entry[ValuesOffset + index]);
                count++;
            }
            if (count == 0) return Array.Empty<byte>();
            stream.Position = 0;
            writer.Write(count);
            return stream.ToArray();
        }

        internal byte[] PrepareResync(ReadOnlySpan<byte> bytes, Func<int, uint, ResyncIdentity?> readIdentity)
        {
            var replacement = new byte[Capacity * EntrySize];
            if (bytes.IsEmpty) return replacement;
            if (bytes.Length < sizeof(int)) throw new InvalidDataException("Short building state repair record.");
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (count < 1 || count >= Capacity || bytes.Length != sizeof(int) + count * ResyncRowBytes)
                throw new InvalidDataException("Invalid building state repair record size.");

            int previous = 0;
            for (int index = 0; index < count; index++)
            {
                var row = bytes.Slice(sizeof(int) + index * ResyncRowBytes, ResyncRowBytes);
                int id = BinaryPrimitives.ReadUInt16LittleEndian(row);
                uint global = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(2));
                ushort owner = BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(6));
                int type = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(8));
                var values = row.Slice(12);
                if (!IsValidId(id) || id <= previous || global == 0)
                    throw new InvalidDataException("Invalid building state repair identity.");
                previous = id;
                for (int value = 0; value < values.Length; value++)
                    if (values[value] > Maxima[value]) throw new InvalidDataException("Invalid building state repair value.");

                var identity = readIdentity(id, global);
                if (identity?.Owner != owner || identity?.Type != type) continue;
                var entry = replacement.AsSpan(id * EntrySize, EntrySize);
                BinaryPrimitives.WriteUInt32LittleEndian(entry, global);
                values.CopyTo(entry.Slice(ValuesOffset));
            }
            return replacement;
        }

        internal void CommitResync(byte[] replacement) =>
            replacement.AsSpan().CopyTo(new Span<byte>(entries, Capacity * EntrySize));
    }
}
