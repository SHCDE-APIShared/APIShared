using System;
using System.Collections.Generic;

namespace APIShared.ScriptExtenderFixes
{
    // Unity-thread-owned, bounded copy of only Vanilla's pre-game handshake. No game state writes.
    internal sealed class LobbyStartPacketQueue
    {
        internal sealed class Packet
        {
            internal readonly short Type;
            internal readonly ulong Sender;
            internal readonly byte[] Bytes;
            internal Packet(short type, ulong sender, byte[] bytes) { Type = type; Sender = sender; Bytes = bytes; }
        }
        private readonly Queue<Packet> packets = new Queue<Packet>();
        private readonly int capacity;
        internal LobbyStartPacketQueue(int capacity = 64)
        { if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity)); this.capacity = capacity; }
        internal int Count => packets.Count;
        internal long Generation { get; private set; }
        internal bool Poisoned { get; private set; }
        internal void Reset() { packets.Clear(); Poisoned = false; Generation++; }
        internal void Discard(long generation) { if (generation == Generation) Reset(); }
        internal static bool IsHandshake(short type) => type == 2 || type == 3 || type == 4 || type == 9;
        internal bool Add(short type, byte[] bytes, int offset, int count, ulong sender,
            ulong local, ulong host, IReadOnlyList<ulong> humans)
        {
            if (Poisoned || !IsHandshake(type) || bytes == null || offset < 0 || count < 0 ||
                offset > bytes.Length || count > bytes.Length - offset || sender == 0 || sender == local) return false;
            bool human = false;
            for (int i = 0; humans != null && i < humans.Count; i++) if (humans[i] == sender) human = true;
            if (!human) return false;
            if (type == 2 || type == 4)
            { if (local == host || sender != host || (type == 2 ? count != 4 : count != 0 && count != 36)) return false; }
            else
            { if (local != host || sender == host || (type == 3 ? count != 0 : count != 8)) return false; }
            // Vanilla indexes both the nine-entry source slot and destination player domains.
            if (type == 9 && (BitConverter.ToInt32(bytes, offset) < 0 || BitConverter.ToInt32(bytes, offset) >= 9 ||
                BitConverter.ToInt32(bytes, offset + 4) < 0 || BitConverter.ToInt32(bytes, offset + 4) >= 9)) return false;
            if (packets.Count == capacity) { packets.Clear(); Poisoned = true; return false; }
            var owned = new byte[count];
            Array.Copy(bytes, offset, owned, 0, count);
            packets.Enqueue(new Packet(type, sender, owned));
            return true;
        }
        internal Packet Take() => packets.Count == 0 ? null : packets.Dequeue();
    }
}
