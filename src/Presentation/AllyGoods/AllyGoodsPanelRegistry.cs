using System;
using APIShared.Events;
using Noesis;

namespace APIShared.Presentation
{
    // Metadata registry is independent of Noesis construction and uses immutable ordered snapshots.
    internal sealed class AllyGoodsSectionRegistry
    {
        private AllyGoodsSectionEntry[] entries = Array.Empty<AllyGoodsSectionEntry>();
        internal AllyGoodsSectionEntry[] Entries => entries;
        internal bool TryRegister(string owner, string id, Func<AllyGoodsSectionContext, FrameworkElement> factory,
            int order, out AllyGoodsSectionEntry entry, out string reason)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || factory == null)
            { reason = "A nonempty owner GUID, section ID and factory are required."; return false; }
            if (OrderedRegistrations.Contains(entries, AllyGoodsSectionEntry.Key, owner, id))
            { reason = "Duplicate ally-goods section: " + owner + "/" + id; return false; }
            entry = new AllyGoodsSectionEntry(owner, id, order, factory);
            entries = OrderedRegistrations.WithAdded(entries, entry, AllyGoodsSectionEntry.Key); reason = ""; return true;
        }
    }
    internal sealed class AllyGoodsSectionEntry
    {
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal readonly Func<AllyGoodsSectionContext, FrameworkElement> Factory;
        internal bool Visible = true, Enabled = true;
        internal int Revision;
        internal AllyGoodsSectionEntry(string owner, string id, int order, Func<AllyGoodsSectionContext, FrameworkElement> factory)
        { Owner = owner; Id = id; Order = order; Factory = factory; }
        internal static readonly Func<AllyGoodsSectionEntry, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id, entry.Order);
    }
    internal static class AllyGoodsContextPolicy
    {
        internal static int Recipient(int selected, int count, int[] allies)
        {
            if (allies == null || selected < 0 || selected >= count || selected >= allies.Length) return 0;
            int value = allies[selected]; return value >= 1 && value <= 8 ? value : 0;
        }
        internal static SHCDESE.Interop.eGoods? Good(int value) => value > (int)SHCDESE.Interop.eGoods.STORED_NULL &&
            value < (int)SHCDESE.Interop.eGoods.Count ? (SHCDESE.Interop.eGoods?)value : null;
        internal static int? Amount(int value) => value >= 0 ? (int?)value : null;
        internal static float ExtensionHeight(float desired, float available) =>
            Math.Max(0, Math.Min(Math.Min(float.IsNaN(desired) || desired <= 0 ? 128 : desired, 260), available));
        internal static float AvailableHeight(float viewportHeight, float viewportTop, float scaleY, float baseHeight)
        {
            if (!Finite(viewportHeight) || !Finite(viewportTop) || !Finite(scaleY) || !Finite(baseHeight) ||
                viewportHeight <= 0 || scaleY <= 0 || baseHeight < 0) return 0;
            // Viewbox scaling affects the transformed top and viewport units, but our
            // root/extension Height remains in local XAML units. Convert before subtraction.
            float available = (viewportHeight - viewportTop) / scaleY - baseHeight - 8;
            return Finite(available) ? Math.Max(0, available) : 0;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
