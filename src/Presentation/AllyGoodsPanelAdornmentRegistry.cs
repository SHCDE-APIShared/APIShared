using System;
using System.Collections.Generic;
using Noesis;
using SHCDESE.Interop;

namespace APIShared.Presentation
{
    internal sealed class AllyGoodsAdornmentRegistry
    {
        private AllyGoodsAdornmentEntry[] entries = Array.Empty<AllyGoodsAdornmentEntry>();
        internal AllyGoodsAdornmentEntry[] Entries => entries;
        internal bool TryRegister(string owner, string id, Func<AllyGoodsSectionContext, eGoods, FrameworkElement> factory,
            int order, out AllyGoodsAdornmentEntry entry, out string reason)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || factory == null)
            { reason = "A nonempty owner GUID, adornment ID and factory are required."; return false; }
            foreach (var existing in entries)
                if (existing.Owner == owner && existing.Id == id)
                { reason = "Duplicate ally-goods adornment: " + owner + "/" + id; return false; }
            entry = new AllyGoodsAdornmentEntry(owner, id, order, factory);
            var next = new AllyGoodsAdornmentEntry[entries.Length + 1];
            Array.Copy(entries, next, entries.Length); next[next.Length - 1] = entry;
            Array.Sort(next, (a, b) =>
            {
                int result = a.Order.CompareTo(b.Order);
                if (result == 0) result = string.CompareOrdinal(a.Owner, b.Owner);
                return result == 0 ? string.CompareOrdinal(a.Id, b.Id) : result;
            });
            entries = next; reason = ""; return true;
        }
    }

    internal sealed class AllyGoodsAdornmentEntry
    {
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal readonly Func<AllyGoodsSectionContext, eGoods, FrameworkElement> Factory;
        internal int Revision;
        internal AllyGoodsAdornmentEntry(string owner, string id, int order,
            Func<AllyGoodsSectionContext, eGoods, FrameworkElement> factory)
        { Owner = owner; Id = id; Order = order; Factory = factory; }
    }

    // Production cache policy is testable without constructing native Noesis objects.
    // Successful views survive every revision; failures retry only on explicit refresh.
    internal sealed class AllyGoodsAdornmentCache<T> where T : class
    {
        private readonly Dictionary<AllyGoodsAdornmentEntry, Dictionary<eGoods, Item>> entries =
            new Dictionary<AllyGoodsAdornmentEntry, Dictionary<eGoods, Item>>();
        internal T GetOrCreate(AllyGoodsAdornmentEntry entry, eGoods good, Func<T> create, Action<Exception> failed)
        {
            if (!entries.TryGetValue(entry, out var goods))
                entries.Add(entry, goods = new Dictionary<eGoods, Item>());
            if (goods.TryGetValue(good, out var cached) && (cached.Value != null || cached.Revision == entry.Revision))
                return cached.Value;
            int revision = entry.Revision;
            T value = null;
            try { value = create(); }
            catch (Exception error) { failed(error); }
            goods[good] = new Item(revision, value); return value;
        }
        internal void Clear() => entries.Clear();
        private sealed class Item
        {
            internal readonly int Revision;
            internal readonly T Value;
            internal Item(int revision, T value) { Revision = revision; Value = value; }
        }
    }
}
