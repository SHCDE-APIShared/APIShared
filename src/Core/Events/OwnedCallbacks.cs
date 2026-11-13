using System;
using System.Threading;

namespace APIShared.Events
{
    // Copy-on-write owner-bound registrations in RegistrationKey order;
    // callbacks execute outside the registration lock.
    internal sealed class OwnedCallbacks<T> where T : Delegate
    {
        internal sealed class Entry
        {
            internal readonly string Owner, Id;
            internal readonly T Callback;
            internal readonly int Order;
            internal Entry(string owner, string id, T callback, int order) { Owner = owner; Id = id; Callback = callback; Order = order; }
            internal static readonly Func<Entry, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id, entry.Order);
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
                if (OrderedRegistrations.Contains(entries, Entry.Key, owner, id))
                { diagnostic = "This owner already registered that ID."; return false; }
                Volatile.Write(ref entries, OrderedRegistrations.WithAdded(entries, new Entry(owner, id, callback, order), Entry.Key));
            }
            diagnostic = string.Empty;
            return true;
        }
    }
}
