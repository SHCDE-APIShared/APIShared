using System;
using System.Threading;

namespace APIShared.Events
{
    // Copy-on-write owner-bound registrations, ordered by order, ordinal owner GUID and ordinal ID;
    // callbacks execute outside the registration lock.
    internal sealed class OwnedCallbacks<T> where T : Delegate
    {
        internal sealed class Entry
        {
            internal readonly string Owner, Id;
            internal readonly T Callback;
            internal readonly int Order;
            internal Entry(string owner, string id, T callback, int order) { Owner = owner; Id = id; Callback = callback; Order = order; }
        }
        private readonly object sync = new object();
        private Entry[] entries = Array.Empty<Entry>();
        internal Entry[] Snapshot => Volatile.Read(ref entries);
        internal bool TryAdd(string owner, string id, T callback, out string diagnostic, int order = 0)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || callback == null)
            { diagnostic = "Owner GUID, registration ID and callback are required."; return false; }
            lock (sync)
            {
                foreach (Entry entry in entries)
                    if (entry.Owner == owner && entry.Id == id)
                    { diagnostic = "This owner already registered that ID."; return false; }
                var next = new Entry[entries.Length + 1];
                Array.Copy(entries, next, entries.Length);
                next[next.Length - 1] = new Entry(owner, id, callback, order);
                Array.Sort(next, (a, b) => {
                    int result = a.Order.CompareTo(b.Order);
                    if (result == 0) result = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
                    return result == 0 ? StringComparer.Ordinal.Compare(a.Id, b.Id) : result;
                });
                Volatile.Write(ref entries, next);
            }
            diagnostic = string.Empty;
            return true;
        }
    }
}
