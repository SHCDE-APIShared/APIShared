using System;
using System.Collections.Generic;
using APIShared.Events;
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
            if (OrderedRegistrations.Contains(entries, AllyGoodsAdornmentEntry.Key, owner, id))
            { reason = "Duplicate ally-goods adornment: " + owner + "/" + id; return false; }
            entry = new AllyGoodsAdornmentEntry(owner, id, order, factory);
            entries = OrderedRegistrations.WithAdded(entries, entry, AllyGoodsAdornmentEntry.Key); reason = ""; return true;
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
        internal static readonly Func<AllyGoodsAdornmentEntry, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id, entry.Order);
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
