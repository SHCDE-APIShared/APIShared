using System;
using System.Collections;
using System.Collections.Generic;

namespace APIShared.Units
{
    // Immutable published identity view. Readers on any thread retain whole instances; nothing reachable from a
    // published instance is ever written again. Slots map to fixed 128-entry pages, so one mark or unmark copies
    // the root and one page of the slot map and of its variant's member set, independent of how many units are
    // marked. A full rebuild remains for world binding, restore and broad reconciliation.
    internal sealed class UnitIdentityPublication
    {
        internal const int PageBits = 7, PageSize = 1 << PageBits, PageMask = PageSize - 1;
        private static readonly UnitIdentitySnapshot[][] NoPages = Array.Empty<UnitIdentitySnapshot[]>();
        internal static readonly UnitIdentityPublication Empty =
            new UnitIdentityPublication(null, NoPages, new Dictionary<string, UnitIdentityMembers>(StringComparer.Ordinal));

        internal readonly object World;
        private readonly UnitIdentitySnapshot[][] pages;
        private readonly Dictionary<string, UnitIdentityMembers> members;

        private UnitIdentityPublication(object world, UnitIdentitySnapshot[][] pages, Dictionary<string, UnitIdentityMembers> members)
        { World = world; this.pages = pages; this.members = members; }

        // Allocation-free. A published mark always has a nonzero GlobalId; default entries are absent.
        internal bool TryGetMark(int slot, out UnitIdentitySnapshot value)
        {
            value = default;
            if (slot <= 0) return false;
            int page = slot >> PageBits;
            if (page >= pages.Length || pages[page] == null) return false;
            value = pages[page][slot & PageMask];
            return value.GlobalId != 0;
        }

        internal bool TryGetMembers(string key, out UnitIdentityMembers result) => members.TryGetValue(key, out result);

        // Complete rebuild from the visible rows; callers filter dormant rows.
        internal static UnitIdentityPublication Build(object world, IEnumerable<KeyValuePair<string, UnitIdentitySnapshot>> visible)
        {
            UnitIdentitySnapshot[][] slotPages = NoPages;
            var groups = new Dictionary<string, List<UnitIdentitySnapshot>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, UnitIdentitySnapshot> item in visible)
            {
                slotPages = Write(slotPages, item.Value.UnitId, item.Value, inPlace: true);
                if (!groups.TryGetValue(item.Key, out var list)) groups.Add(item.Key, list = new List<UnitIdentitySnapshot>());
                list.Add(item.Value);
            }
            var sets = new Dictionary<string, UnitIdentityMembers>(StringComparer.Ordinal);
            foreach (var group in groups) sets.Add(group.Key, UnitIdentityMembers.Build(group.Value));
            return new UnitIdentityPublication(world, slotPages, sets);
        }

        // One visible mark added or replaced at its slot. previousKey names the variant whose visible entry the slot
        // held before (null when none), so a stale replacement also leaves that variant's member set.
        internal UnitIdentityPublication With(object world, string key, UnitIdentitySnapshot value, string previousKey)
        {
            var next = new Dictionary<string, UnitIdentityMembers>(members, StringComparer.Ordinal);
            if (previousKey != null && previousKey != key) Remove(next, previousKey, value.UnitId);
            next.TryGetValue(key, out var set);
            next[key] = (set ?? UnitIdentityMembers.Empty).With(value);
            return new UnitIdentityPublication(world, Write(pages, value.UnitId, value, inPlace: false), next);
        }

        // One visible mark removed; key names its variant.
        internal UnitIdentityPublication Without(object world, string key, int slot)
        {
            var next = new Dictionary<string, UnitIdentityMembers>(members, StringComparer.Ordinal);
            Remove(next, key, slot);
            return new UnitIdentityPublication(world, Write(pages, slot, default, inPlace: false), next);
        }

        private static void Remove(Dictionary<string, UnitIdentityMembers> sets, string key, int slot)
        {
            if (!sets.TryGetValue(key, out var set)) return;
            UnitIdentityMembers remaining = set.Without(slot);
            if (remaining.Count == 0) sets.Remove(key); else sets[key] = remaining;
        }

        // Copy-on-write of the root and one page; inPlace only while building an unpublished instance.
        internal static UnitIdentitySnapshot[][] Write(UnitIdentitySnapshot[][] source, int slot, UnitIdentitySnapshot value, bool inPlace)
        {
            int page = slot >> PageBits;
            UnitIdentitySnapshot[][] root = source;
            if (page >= root.Length)
            {
                if (value.GlobalId == 0) return source;
                root = new UnitIdentitySnapshot[page + 1][];
                Array.Copy(source, root, source.Length);
            }
            else if (!inPlace) root = (UnitIdentitySnapshot[][])source.Clone();
            UnitIdentitySnapshot[] entries = root[page];
            if (entries == null)
            {
                if (value.GlobalId == 0) return source;
                entries = new UnitIdentitySnapshot[PageSize];
            }
            else if (!inPlace) entries = (UnitIdentitySnapshot[])entries.Clone();
            entries[slot & PageMask] = value;
            root[page] = entries;
            return root;
        }
    }

    // Immutable, retainable member snapshot of one variant, ordered by unit slot. Stored in the same 128-slot pages
    // with per-page prefix counts, so an update copies one page instead of the variant's whole list.
    internal sealed class UnitIdentityMembers : IReadOnlyList<UnitIdentitySnapshot>
    {
        internal static readonly UnitIdentityMembers Empty = new UnitIdentityMembers(Array.Empty<UnitIdentitySnapshot[]>(), new[] { 0 });
        private readonly UnitIdentitySnapshot[][] pages;
        // before[p] = members in pages below p; before[pages.Length] = Count.
        private readonly int[] before;

        private UnitIdentityMembers(UnitIdentitySnapshot[][] pages, int[] before) { this.pages = pages; this.before = before; }

        public int Count => before[pages.Length];

        public UnitIdentitySnapshot this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                // Largest page whose prefix does not exceed the index; that page holds the member.
                int low = 0, high = pages.Length - 1;
                while (low < high)
                {
                    int middle = (low + high + 1) >> 1;
                    if (before[middle] <= index) low = middle; else high = middle - 1;
                }
                int rank = index - before[low];
                UnitIdentitySnapshot[] entries = pages[low];
                for (int offset = 0; offset < entries.Length; offset++)
                    if (entries[offset].GlobalId != 0 && rank-- == 0) return entries[offset];
                throw new InvalidOperationException("The member pages are inconsistent.");
            }
        }

        public IEnumerator<UnitIdentitySnapshot> GetEnumerator()
        {
            foreach (UnitIdentitySnapshot[] entries in pages)
            {
                if (entries == null) continue;
                foreach (UnitIdentitySnapshot entry in entries) if (entry.GlobalId != 0) yield return entry;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal static UnitIdentityMembers Build(List<UnitIdentitySnapshot> values)
        {
            UnitIdentitySnapshot[][] built = Array.Empty<UnitIdentitySnapshot[]>();
            foreach (UnitIdentitySnapshot value in values)
                built = UnitIdentityPublication.Write(built, value.UnitId, value, inPlace: true);
            return new UnitIdentityMembers(built, Prefix(built));
        }

        internal UnitIdentityMembers With(UnitIdentitySnapshot value) =>
            Rebase(UnitIdentityPublication.Write(pages, value.UnitId, value, inPlace: false), value.UnitId, Contains(value.UnitId) ? 0 : 1);

        internal UnitIdentityMembers Without(int slot) =>
            Contains(slot) ? Rebase(UnitIdentityPublication.Write(pages, slot, default, inPlace: false), slot, -1) : this;

        private bool Contains(int slot)
        {
            int page = slot >> UnitIdentityPublication.PageBits;
            return page < pages.Length && pages[page] != null && pages[page][slot & UnitIdentityPublication.PageMask].GlobalId != 0;
        }

        // Prefix counts change only after the written page: O(pages), not a scan of every slot.
        private UnitIdentityMembers Rebase(UnitIdentitySnapshot[][] next, int slot, int delta)
        {
            var counts = new int[next.Length + 1];
            Array.Copy(before, counts, before.Length);
            for (int page = before.Length; page < counts.Length; page++) counts[page] = Count;
            for (int page = (slot >> UnitIdentityPublication.PageBits) + 1; page < counts.Length; page++) counts[page] += delta;
            return new UnitIdentityMembers(next, counts);
        }

        // Cold path for a complete build.
        private static int[] Prefix(UnitIdentitySnapshot[][] next)
        {
            var result = new int[next.Length + 1];
            for (int page = 0; page < next.Length; page++)
            {
                int count = 0;
                UnitIdentitySnapshot[] entries = next[page];
                if (entries != null) foreach (UnitIdentitySnapshot entry in entries) if (entry.GlobalId != 0) count++;
                result[page + 1] = result[page] + count;
            }
            return result;
        }
    }
}
