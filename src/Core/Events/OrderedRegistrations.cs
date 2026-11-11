using System;

namespace APIShared.Events
{
    // Identity and position of one owner-bound registration. Brokers order registrations by
    // ascending Order, then ordinal owner GUID, then ordinal registration ID; brokers without
    // an order use 0, which leaves the owner/ID order unchanged.
    internal readonly struct RegistrationKey
    {
        internal RegistrationKey(string owner, string id, int order = 0) { Owner = owner; Id = id; Order = order; }
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal bool Matches(string owner, string id) => Owner == owner && Id == id;

        internal static int Compare(in RegistrationKey a, in RegistrationKey b)
        {
            int result = a.Order.CompareTo(b.Order);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.Id, b.Id);
        }
    }

    // Copy-on-write helpers for registration arrays that are read without a lock. The caller
    // serializes writes and publishes the returned array; existing arrays are never modified.
    internal static class OrderedRegistrations
    {
        internal static bool Contains<T>(T[] entries, Func<T, RegistrationKey> key, string owner, string id)
        {
            foreach (T entry in entries)
                if (key(entry).Matches(owner, id)) return true;
            return false;
        }

        internal static T[] WithAdded<T>(T[] entries, T item, Func<T, RegistrationKey> key)
        {
            var next = new T[entries.Length + 1];
            Array.Copy(entries, next, entries.Length);
            next[entries.Length] = item;
            // Keys are unique per broker, so the unstable sort still yields one deterministic order.
            Array.Sort(next, (a, b) => RegistrationKey.Compare(key(a), key(b)));
            return next;
        }
    }
}
