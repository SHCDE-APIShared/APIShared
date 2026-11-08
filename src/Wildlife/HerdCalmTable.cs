using System;
using System.Collections.Generic;

namespace APIShared.Wildlife
{
    // Calm marks of all herd flight rules. A herd's native byte is 1 while at least one rule marks it, so
    // rules never undo each other. Not thread-safe; HerdRuntime serializes every call.
    internal sealed unsafe class HerdCalmTable
    {
        private readonly byte* native;
        private readonly int capacity;
        private readonly ushort[] counts;
        private readonly List<byte[]> marks = new List<byte[]>();

        internal HerdCalmTable(byte* native, int capacity)
        {
            this.native = native;
            this.capacity = capacity;
            counts = new ushort[capacity];
            new Span<byte>(native, capacity).Clear();
        }

        internal int RuleCount => marks.Count;

        internal int AddRule()
        {
            marks.Add(new byte[capacity]);
            return marks.Count - 1;
        }

        internal bool Set(int rule, int herd, bool calm)
        {
            if ((uint)rule >= (uint)marks.Count || herd < 1 || herd >= capacity) return false;
            byte[] own = marks[rule];
            byte wanted = calm ? (byte)1 : (byte)0;
            if (own[herd] == wanted) return true;
            own[herd] = wanted;
            counts[herd] = (ushort)(counts[herd] + (calm ? 1 : -1));
            native[herd] = counts[herd] != 0 ? (byte)1 : (byte)0;
            return true;
        }

        internal void Clear(int rule)
        {
            if ((uint)rule >= (uint)marks.Count) return;
            byte[] own = marks[rule];
            for (int herd = 1; herd < capacity; herd++)
            {
                if (own[herd] == 0) continue;
                own[herd] = 0;
                counts[herd]--;
                native[herd] = counts[herd] != 0 ? (byte)1 : (byte)0;
            }
        }

        internal void ClearAll()
        {
            foreach (byte[] own in marks) Array.Clear(own, 0, own.Length);
            Array.Clear(counts, 0, counts.Length);
            new Span<byte>(native, capacity).Clear();
        }
    }
}
