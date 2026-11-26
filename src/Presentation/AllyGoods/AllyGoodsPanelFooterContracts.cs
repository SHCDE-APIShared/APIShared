using System;
using System.Collections.Generic;

namespace APIShared.Presentation
{
    /// <summary>Process-lifetime registration for a compact control in the goods dialog footer.
    /// All methods require the initialized Unity thread. Hidden controls remain cached, and
    /// disabling affects only this control; consumer commands still authorize themselves.</summary>
    public sealed class AllyGoodsFooterRegistration
    {
        private readonly AllyGoodsSectionEntry entry;
        private readonly Action invalidate;
        internal AllyGoodsFooterRegistration(AllyGoodsSectionEntry entry, Action invalidate)
        { this.entry = entry; this.invalidate = invalidate; }
        /// <summary>Shows or hides this control without recreating it; initially visible.</summary>
        public void SetVisible(bool visible)
        { AllyGoodsPanelExtensions.RequireUnity(); if (entry.Visible == visible) return; entry.Visible = visible; invalidate(); }
        /// <summary>Changes interaction availability of this control; initially enabled.</summary>
        public void SetEnabled(bool enabled)
        { AllyGoodsPanelExtensions.RequireUnity(); if (entry.Enabled == enabled) return; entry.Enabled = enabled; invalidate(); }
        /// <summary>Recreates this control and retries a failed factory on the next open-dialog update.</summary>
        public void RequestContentRefresh()
        { AllyGoodsPanelExtensions.RequireUnity(); entry.Revision++; invalidate(); }
    }

    // The same ordered metadata contract as sections, with an independent ID namespace.
    // Cache mechanics are independent of Noesis so host/refresh/error lifetimes are testable.
    internal sealed class AllyGoodsFooterCache<T> where T : class
    {
        private readonly Dictionary<AllyGoodsSectionEntry, Item> entries = new Dictionary<AllyGoodsSectionEntry, Item>();
        internal T GetOrCreate(AllyGoodsSectionEntry entry, Func<T> create, Action<Exception> failed, Action<T> release)
        {
            if (entries.TryGetValue(entry, out var item))
            {
                if (item.Revision == entry.Revision) return item.Value;
                if (item.Value != null) release(item.Value);
            }
            int revision = entry.Revision;
            T value = null;
            try { value = create(); }
            catch (Exception error) { failed(error); }
            entries[entry] = new Item(revision, value);
            return value;
        }
        internal void Clear(Action<T> release)
        { foreach (var item in entries.Values) if (item.Value != null) release(item.Value); entries.Clear(); }
        private sealed class Item
        {
            internal readonly int Revision;
            internal readonly T Value;
            internal Item(int revision, T value) { Revision = revision; Value = value; }
        }
    }

    internal static class AllyGoodsFooterPolicy
    {
        internal const float Height = 18, Bottom = 4, Inset = 16;
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool HasStrip(float width, float height) => Finite(width) && Finite(height) &&
            width > Inset * 2 && height >= Height + Bottom;
        internal static bool Overlaps(float x, float y, float width, float height, float footerWidth, float footerHeight)
        {
            if (!Finite(x) || !Finite(y) || !Finite(width) || !Finite(height)) return true;
            return width > 0 && height > 0 && x < footerWidth - Inset && x + width > Inset &&
                y < footerHeight - Bottom && y + height > footerHeight - Bottom - Height;
        }
        internal static bool Fits(float width, float available) => Finite(width) && Finite(available) && width <= available + 0.1f;
        internal static bool IsMarkedBackground(object element, object background) =>
            background != null && ReferenceEquals(element, background);
    }
}
