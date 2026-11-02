using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;

namespace APIShared.PlayerControl
{
    // Internal wire format, independent of MessagePack/transport fragmentation. Native
    // opcodes remain private: only the audited backend can interpret/authorize them.
    internal sealed class ControlNativeCommand
    {
        private readonly byte[] payload;
        internal ControlNativeCommand(int owner, byte opcode, byte[] payload)
        {
            if (owner < 1 || owner > 8) throw new ArgumentOutOfRangeException(nameof(owner));
            if (payload == null || payload.Length > ControlCommandBatch.MaximumCommandBytes)
                throw new ArgumentOutOfRangeException(nameof(payload));
            Owner = owner; Opcode = opcode; this.payload = (byte[])payload.Clone();
        }
        internal int Owner { get; }
        internal byte Opcode { get; }
        internal int Length => payload.Length;
        internal byte Read(int index) => payload[index];
        internal byte[] CopyPayload() => (byte[])payload.Clone();
        internal void WriteTo(BinaryWriter writer) => writer.Write(payload);
    }

    internal sealed class ControlCommandBatch
    {
        // At most all 9,999 units plus buildings; actual table bounds belong to the
        // native resolver. The byte budget is shared by identities and command bodies.
        internal const int MaximumObjects = 16000, MaximumCommands = 256;
        internal const int MaximumCommandBytes = 1260;
        internal const int MaximumBytes = ControlBatchAssembler.ChunkBytes * ControlBatchAssembler.MaximumParts;
        private const uint Magic = 0x314C4350; // PCL1, little endian
        internal const ushort WireVersion = 1;
        internal ReadOnlyCollection<PlayerControlObject> Objects { get; }
        internal ReadOnlyCollection<ControlNativeCommand> Commands { get; }

        internal ControlCommandBatch(IList<PlayerControlObject> objects, IList<ControlNativeCommand> commands)
        {
            if (objects == null || objects.Count > MaximumObjects) throw new ArgumentOutOfRangeException(nameof(objects));
            if (commands == null || commands.Count < 1 || commands.Count > MaximumCommands)
                throw new ArgumentOutOfRangeException(nameof(commands));
            var capturedObjects = new PlayerControlObject[objects.Count];
            objects.CopyTo(capturedObjects, 0);
            var capturedCommands = new ControlNativeCommand[commands.Count];
            for (int i = 0; i < capturedCommands.Length; i++)
                capturedCommands[i] = commands[i] ?? throw new ArgumentException("Null native command.", nameof(commands));
            Objects = Array.AsReadOnly(capturedObjects); Commands = Array.AsReadOnly(capturedCommands);
        }

        internal byte[] Encode()
        {
            int length = 10 + Objects.Count * 8;
            foreach (var command in Commands) length = checked(length + 4 + command.Length);
            if (length > MaximumBytes) throw new InvalidOperationException("Control command exceeds the complete transaction budget.");
            using (var stream = new MemoryStream(length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic); writer.Write(WireVersion);
                writer.Write((ushort)Objects.Count); writer.Write((ushort)Commands.Count);
                foreach (var item in Objects)
                {
                    if (item.Slot < 1 || item.Slot > ushort.MaxValue || item.GlobalId == 0 ||
                        item.OwnerPlayerId < 1 || item.OwnerPlayerId > 8 ||
                        (item.Kind != PlayerControlObjectKind.Unit && item.Kind != PlayerControlObjectKind.Building))
                        throw new InvalidOperationException("Invalid object identity.");
                    writer.Write((byte)item.Kind); writer.Write((byte)item.OwnerPlayerId);
                    writer.Write((ushort)item.Slot); writer.Write(item.GlobalId);
                }
                foreach (var command in Commands)
                {
                    writer.Write((byte)command.Owner); writer.Write(command.Opcode);
                    writer.Write((ushort)command.Length); command.WriteTo(writer);
                }
                return stream.ToArray();
            }
        }

        internal byte[] ComputeDigest()
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(Encode());
        }

        // Decode only after reassembly. Counts are bounded before allocations, every
        // range is checked, and trailing bytes are rejected rather than ignored.
        internal static bool TryDecode(byte[] bytes, out ControlCommandBatch batch)
        {
            batch = null;
            if (bytes == null || bytes.Length < 14 || bytes.Length > MaximumBytes) return false;
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != WireVersion) return false;
                int count = reader.ReadUInt16(), commandCount = reader.ReadUInt16();
                if (count > MaximumObjects || commandCount < 1 || commandCount > MaximumCommands ||
                    (long)count * 8 + (long)commandCount * 4 > stream.Length - stream.Position) return false;
                var objects = new PlayerControlObject[count];
                for (int i = 0; i < count; i++)
                {
                    var kind = (PlayerControlObjectKind)reader.ReadByte();
                    int owner = reader.ReadByte(), slot = reader.ReadUInt16();
                    uint globalId = reader.ReadUInt32();
                    if ((kind != PlayerControlObjectKind.Unit && kind != PlayerControlObjectKind.Building) ||
                        owner < 1 || owner > 8 || slot == 0 || globalId == 0) return false;
                    objects[i] = new PlayerControlObject(kind, slot, globalId, owner);
                }
                var commands = new ControlNativeCommand[commandCount];
                for (int i = 0; i < commands.Length; i++)
                {
                    if (stream.Length - stream.Position < 4) return false;
                    int owner = reader.ReadByte(); byte opcode = reader.ReadByte(); int size = reader.ReadUInt16();
                    if (owner < 1 || owner > 8 || size > MaximumCommandBytes || size > stream.Length - stream.Position) return false;
                    commands[i] = new ControlNativeCommand(owner, opcode, reader.ReadBytes(size));
                }
                if (stream.Position != stream.Length) return false;
                batch = new ControlCommandBatch(objects, commands);
                return true;
            }
        }
    }
}
