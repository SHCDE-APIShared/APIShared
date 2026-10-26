using System;
using System.Threading;

namespace APIShared.Events
{
    // Copy-on-write owner-bound registrations; callbacks execute outside the registration lock.
    internal sealed class OwnedCallbacks<T> where T : Delegate
    {
        internal sealed class Entry
        {
            internal readonly string Owner, Id;
            internal readonly T Callback;
            internal Entry(string owner, string id, T callback) { Owner = owner; Id = id; Callback = callback; }
        }
        private readonly object sync = new object();
        private Entry[] entries = Array.Empty<Entry>();
        internal Entry[] Snapshot => Volatile.Read(ref entries);
        internal bool TryAdd(string owner, string id, T callback, out string diagnostic)
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
                next[next.Length - 1] = new Entry(owner, id, callback);
                Array.Sort(next, (a, b) => {
                    int result = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
                    return result == 0 ? StringComparer.Ordinal.Compare(a.Id, b.Id) : result;
                });
                Volatile.Write(ref entries, next);
            }
            diagnostic = string.Empty;
            return true;
        }
    }
}
