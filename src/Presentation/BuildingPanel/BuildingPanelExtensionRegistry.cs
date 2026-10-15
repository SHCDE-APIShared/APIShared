using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared
{
    // Visual operations the registry needs from the extension host. Production uses the Noesis stack in
    // BuildingPanelExtensionsService; tests use a fake, so the refresh path runs without the Noesis runtime.
    internal interface IBuildingPanelView<TElement> where TElement : class
    {
        void Insert(TElement element, int index);
        void Remove(TElement element);
        void SetShown(TElement element, bool shown);
        void SetHostShown(bool shown);
    }

    // Registrations, order and the refresh walk of the building panel extensions, independent of Noesis.
    // Registration (any thread, under the lock) publishes an ordered immutable snapshot; Apply and Reset run on
    // the Unity thread, read the snapshot without locks and keep per-host state in the entries. Apply allocates
    // nothing and touches the view only when an element's visibility changes; element creation and failure
    // reporting are the cold paths, kept in separate methods. Every consumer callback is isolated: a throw
    // disables only that entry until RequestContentRefresh.
    internal sealed class BuildingPanelExtensionRegistry<TElement> where TElement : class
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, OwnerState> owners = new Dictionary<string, OwnerState>(StringComparer.Ordinal);
        private readonly Action<Entry, string, Exception> failure;
        private Entry[] snapshot = new Entry[0];
        private bool hostShown;

        // failure receives the entry, the phase ("factory", "filter", "update") and the exception, once per failure.
        internal BuildingPanelExtensionRegistry(Action<Entry, string, Exception> failure) { this.failure = failure; }

        internal bool HasEntries => Volatile.Read(ref snapshot).Length != 0;
        internal Entry[] Entries => Volatile.Read(ref snapshot);

        internal bool TryAdd(string owner, string id, int order, Func<object, TElement> factory,
            Func<BuildingPanelSelection, bool> appliesTo, Action<BuildingPanelSelection, TElement> update,
            out Entry entry, out string reason)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || factory == null)
            { reason = "An owner GUID, an extension ID and a factory are required."; return false; }
            lock (sync)
            {
                Entry[] current = snapshot;
                for (int index = 0; index < current.Length; index++)
                    if (current[index].Owner == owner && current[index].Id == id)
                    { reason = "The owner already registered this extension ID."; return false; }
                if (!owners.TryGetValue(owner, out OwnerState state)) owners[owner] = state = new OwnerState();
                entry = new Entry(owner, id, order, state, factory, appliesTo, update);
                var next = new Entry[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = entry;
                Array.Sort(next, Compare);
                Volatile.Write(ref snapshot, next);
            }
            reason = null;
            return true;
        }

        internal void SetOwnerActive(string owner, bool active)
        {
            lock (sync)
            {
                if (!owners.TryGetValue(owner, out OwnerState state)) owners[owner] = state = new OwnerState();
                state.Active = active;
            }
        }

        // Order, then ordinal owner GUID, then ordinal extension ID.
        internal static int Compare(Entry a, Entry b)
        {
            int result = a.Order.CompareTo(b.Order);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.Id, b.Id);
        }

        // The host was replaced: forget elements created for the old one. Failures persist.
        internal void Reset()
        {
            Entry[] entries = Volatile.Read(ref snapshot);
            for (int index = 0; index < entries.Length; index++) { entries[index].Element = null; entries[index].Shown = false; }
            hostShown = false;
        }

        // One refresh. context is passed to factories; selection is meaningful only when hasSelection is true.
        internal void Apply(IBuildingPanelView<TElement> view, object context, BuildingPanelSelection selection, bool hasSelection)
        {
            Entry[] entries = Volatile.Read(ref snapshot);
            bool anyShown = false;
            for (int index = 0; index < entries.Length; index++)
            {
                Entry entry = entries[index];
                bool show = false;
                if (hasSelection && entry.Visible && entry.OwnerState.Active)
                {
                    if (entry.CreatedVersion != Volatile.Read(ref entry.ContentVersion)) Discard(entry, view);
                    if (!entry.Failed && Applies(entry, view, selection) &&
                        (entry.Element != null || Create(entry, view, context, entries, index)))
                        show = Run(entry, view, selection);
                }
                if (entry.Element != null && entry.Shown != show) { view.SetShown(entry.Element, show); entry.Shown = show; }
                anyShown |= show;
            }
            if (hostShown != anyShown) { view.SetHostShown(anyShown); hostShown = anyShown; }
        }

        private bool Applies(Entry entry, IBuildingPanelView<TElement> view, BuildingPanelSelection selection)
        {
            if (entry.AppliesTo == null) return true;
            try { return entry.AppliesTo(selection); }
            catch (Exception ex) { Fail(entry, view, "filter", ex); return false; }
        }

        private bool Run(Entry entry, IBuildingPanelView<TElement> view, BuildingPanelSelection selection)
        {
            if (entry.Update == null) return true;
            try { entry.Update(selection, entry.Element); return true; }
            catch (Exception ex) { Fail(entry, view, "update", ex); return false; }
        }

        // Cold path: creates the element once per host and inserts it at its ordered position, hidden.
        private bool Create(Entry entry, IBuildingPanelView<TElement> view, object context, Entry[] entries, int index)
        {
            TElement element;
            try { element = entry.Factory(context) ?? throw new InvalidOperationException("The factory returned null."); }
            catch (Exception ex) { Fail(entry, view, "factory", ex); return false; }
            int position = 0;
            for (int before = 0; before < index; before++) if (entries[before].Element != null) position++;
            try { view.Insert(element, position); view.SetShown(element, false); }
            catch (Exception ex) { Fail(entry, view, "factory", ex); return false; }
            entry.Element = element;
            entry.Shown = false;
            return true;
        }

        // Cold path: RequestContentRefresh drops the element and the failure of this entry.
        private static void Discard(Entry entry, IBuildingPanelView<TElement> view)
        {
            if (entry.Element != null) { try { view.Remove(entry.Element); } catch { } }
            entry.Element = null; entry.Shown = false; entry.Failed = false;
            entry.CreatedVersion = Volatile.Read(ref entry.ContentVersion);
        }

        // Cold path: disables the entry and hides its element; the owner of the failure callback logs it.
        private void Fail(Entry entry, IBuildingPanelView<TElement> view, string phase, Exception error)
        {
            entry.Failed = true;
            if (entry.Element != null && entry.Shown) { try { view.SetShown(entry.Element, false); } catch { } entry.Shown = false; }
            try { failure?.Invoke(entry, phase, error); } catch { }
        }

        internal sealed class OwnerState
        {
            internal volatile bool Active = true;
        }

        internal sealed class Entry : IBuildingPanelExtensionRegistration
        {
            internal readonly string Owner, Id;
            internal readonly int Order;
            internal readonly OwnerState OwnerState;
            internal readonly Func<object, TElement> Factory;
            internal readonly Func<BuildingPanelSelection, bool> AppliesTo;
            internal readonly Action<BuildingPanelSelection, TElement> Update;
            internal volatile bool Visible = true;
            internal int ContentVersion;
            // Per-host state; Unity thread only.
            internal TElement Element;
            internal bool Shown, Failed;
            internal int CreatedVersion;

            internal Entry(string owner, string id, int order, OwnerState ownerState, Func<object, TElement> factory,
                Func<BuildingPanelSelection, bool> appliesTo, Action<BuildingPanelSelection, TElement> update)
            {
                Owner = owner; Id = id; Order = order; OwnerState = ownerState;
                Factory = factory; AppliesTo = appliesTo; Update = update;
            }

            internal string Key => Owner + "/" + Id;
            public void SetVisible(bool visible) => Visible = visible;
            public void RequestContentRefresh() => Interlocked.Increment(ref ContentVersion);
        }
    }
}
