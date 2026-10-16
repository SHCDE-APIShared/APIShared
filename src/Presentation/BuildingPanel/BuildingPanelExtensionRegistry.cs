using SHCDESE.Interop;
using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared
{
    internal enum BuildingPanelVisibility { Visible, Hidden, Collapsed }

    // Visual operations the registry needs from the building panel. Production uses Noesis in
    // BuildingPanelExtensionsService; tests use a fake, so the refresh path runs without the Noesis runtime.
    internal interface IBuildingPanelView<TElement> where TElement : class
    {
        // The shared extension stack.
        void Insert(TElement element, int index);
        void Remove(TElement element);
        void SetShown(TElement element, bool shown);
        void SetHostShown(bool shown);
        // Recruitment-area replacements sit in the panel's root grid beside Vanilla's panels.
        void InsertArea(TElement element);
        void RemoveArea(TElement element);
        // Vanilla's recruitment panels (and the full-panel click mask), by index.
        int VanillaPanelCount { get; }
        BuildingPanelVisibility GetVanilla(int index);
        void SetVanilla(int index, BuildingPanelVisibility visibility);
    }

    // Registrations, order and the refresh walk of the building panel extensions and recruitment-area
    // replacements, independent of Noesis. Registration (any thread, under the lock) publishes ordered immutable
    // snapshots; Apply, Reset and ReleaseActive run on the Unity thread and keep per-panel state in the items.
    // Apply allocates nothing and touches the view only when a visibility changes; element creation and failure
    // and conflict reporting are the cold paths, kept in separate methods. Every consumer callback is isolated:
    // a throw disables only that item until RequestContentRefresh.
    //
    // Replacements are per building variant: any number of owners can register on the same native type, and
    // registration never refuses an overlap. At a refresh every replacement whose types and selector match the
    // selected building is a candidate; the snapshot is sorted by precedence (CompareAreas), so the first
    // candidate is shown and each further one is a conflict, reported once per pair. Registration order never
    // decides. The shown replacement hides Vanilla's recruitment panels with Collapsed and records the visibility
    // they had. While it stays active, a panel Vanilla rewrites is re-recorded and hidden again. Deactivation
    // restores a panel only if it still carries Collapsed from us, so a newer Vanilla write always wins.
    internal sealed class BuildingPanelExtensionRegistry<TElement> where TElement : class
    {
        private const BuildingPanelVisibility Hidden = BuildingPanelVisibility.Collapsed;
        private readonly object sync = new object();
        private readonly Dictionary<string, OwnerState> owners = new Dictionary<string, OwnerState>(StringComparer.Ordinal);
        private readonly Action<string, string, Exception> failure;
        private readonly Action<Area, Area, BuildingPanelSelection> conflict;
        private Entry[] snapshot = new Entry[0];
        private Area[] areas = new Area[0];
        // Released replacements whose element the Unity thread still has to remove.
        private Area[] retired = new Area[0];
        // Per panel; Unity thread only.
        private bool hostShown;
        private Area activeArea;
        private BuildingPanelVisibility[] recorded;

        // failure receives the item key ("owner/id"), the phase ("factory", "filter", "update") and the exception.
        // conflict receives the shown replacement, one that also matched and the selection, once per pair.
        internal BuildingPanelExtensionRegistry(Action<string, string, Exception> failure,
            Action<Area, Area, BuildingPanelSelection> conflict = null)
        { this.failure = failure; this.conflict = conflict; }

        internal bool HasEntries => Volatile.Read(ref snapshot).Length != 0 || Volatile.Read(ref areas).Length != 0 ||
            Volatile.Read(ref retired).Length != 0 || activeArea != null;
        internal Entry[] Entries => Volatile.Read(ref snapshot);
        internal Area ActiveArea => activeArea;

        internal bool TryAdd(string owner, string id, int order, Func<object, TElement> factory,
            Func<BuildingPanelSelection, bool> appliesTo, Action<BuildingPanelSelection, TElement> update,
            out Entry entry, out string reason, bool showInMapEditor = false)
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
                entry = new Entry(owner, id, order, showInMapEditor, Owner(owner), factory, appliesTo, update);
                var next = new Entry[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = entry;
                Array.Sort(next, Compare);
                Volatile.Write(ref snapshot, next);
            }
            reason = null;
            return true;
        }

        // Only an invalid request or a variant ID the owner already registered is refused; overlapping types are not.
        internal bool TryAddArea(string owner, string id, eStructs[] types, Func<object, TElement> factory,
            Func<BuildingPanelSelection, bool> appliesTo, Action<BuildingPanelSelection, TElement> update,
            out Area area, out string reason)
        {
            area = null;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || factory == null || types == null || types.Length == 0)
            { reason = "An owner GUID, a variant ID, at least one building type and a factory are required."; return false; }
            var codes = new int[types.Length];
            for (int index = 0; index < types.Length; index++) codes[index] = (int)types[index];
            lock (sync)
            {
                Area[] current = areas;
                for (int index = 0; index < current.Length; index++)
                    if (current[index].Owner == owner && current[index].Id == id)
                    { reason = "The owner already registered this variant ID."; return false; }
                area = new Area(this, owner, id, codes, Owner(owner), factory, appliesTo, update);
                var next = new Area[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = area;
                Array.Sort(next, CompareAreas);
                Volatile.Write(ref areas, next);
            }
            reason = null;
            return true;
        }

        private void RemoveArea(Area area)
        {
            lock (sync)
            {
                Area[] current = areas;
                int at = Array.IndexOf(current, area);
                if (at < 0) return;
                var next = new Area[current.Length - 1];
                Array.Copy(current, 0, next, 0, at);
                Array.Copy(current, at + 1, next, at, current.Length - at - 1);
                Volatile.Write(ref areas, next);
                var gone = new Area[retired.Length + 1];
                Array.Copy(retired, gone, retired.Length);
                gone[retired.Length] = area;
                Volatile.Write(ref retired, gone);
            }
        }

        // Cold path: removes the elements of released replacements, shown or not.
        private void Retire(IBuildingPanelView<TElement> view)
        {
            Area[] gone;
            lock (sync) { gone = retired; retired = new Area[0]; }
            for (int index = 0; index < gone.Length; index++)
            {
                Area area = gone[index];
                if (activeArea == area) Deactivate(view);
                if (area.Element != null) { try { view.RemoveArea(area.Element); } catch { } }
                area.Element = null; area.Shown = false;
            }
        }

        internal void SetOwnerActive(string owner, bool active)
        {
            lock (sync) Owner(owner).Active = active;
        }

        private OwnerState Owner(string owner)
        {
            if (!owners.TryGetValue(owner, out OwnerState state)) owners[owner] = state = new OwnerState();
            return state;
        }

        // Order, then ordinal owner GUID, then ordinal extension ID.
        internal static int Compare(Entry a, Entry b)
        {
            int result = a.Order.CompareTo(b.Order);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.Id, b.Id);
        }

        // Precedence when several replacements match one building: a variant with a selector before one for every
        // building of its types (the narrower claim is the more specific variant), then ordinal owner GUID, then
        // ordinal variant ID.
        internal static int CompareAreas(Area a, Area b)
        {
            int result = (a.AppliesTo == null).CompareTo(b.AppliesTo == null);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.Id, b.Id);
        }

        // The panel was replaced: forget everything created for the old one. The new panel starts as Vanilla
        // made it, so nothing is restored. Failures and reported conflicts persist.
        internal void Reset()
        {
            Entry[] entries = Volatile.Read(ref snapshot);
            for (int index = 0; index < entries.Length; index++) { entries[index].Element = null; entries[index].Shown = false; }
            Area[] current = Volatile.Read(ref areas);
            for (int index = 0; index < current.Length; index++) { current[index].Element = null; current[index].Shown = false; }
            if (activeArea != null) { activeArea.Element = null; activeArea.Shown = false; }
            lock (sync)
            {
                for (int index = 0; index < retired.Length; index++) { retired[index].Element = null; retired[index].Shown = false; }
                retired = new Area[0];
            }
            activeArea = null;
            recorded = null;
            hostShown = false;
        }

        // Mission end: restore Vanilla's recruitment panels if a replacement holds them.
        internal void ReleaseActive(IBuildingPanelView<TElement> view)
        {
            if (activeArea != null) Deactivate(view);
        }

        // One refresh. context is passed to factories; selection is meaningful only when hasSelection is true.
        internal void Apply(IBuildingPanelView<TElement> view, object context, BuildingPanelSelection selection, bool hasSelection)
        {
            ApplyAreas(view, context, selection, hasSelection);
            Entry[] entries = Volatile.Read(ref snapshot);
            bool anyShown = false;
            for (int index = 0; index < entries.Length; index++)
            {
                Entry entry = entries[index];
                bool show = false;
                if (hasSelection && entry.Visible && entry.OwnerState.Active && (entry.ShowInMapEditor || !selection.IsMapEditor))
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

        private void ApplyAreas(IBuildingPanelView<TElement> view, object context, BuildingPanelSelection selection, bool hasSelection)
        {
            if (Volatile.Read(ref retired).Length != 0) Retire(view);
            Area[] current = Volatile.Read(ref areas);
            if (current.Length == 0 && activeArea == null) return;
            Area next = null;
            // Nothing is recruited in the map editor, so no replacement applies there.
            if (hasSelection && !selection.IsMapEditor)
            {
                int type = (int)selection.BuildingType;
                for (int index = 0; index < current.Length; index++)
                {
                    Area area = current[index];
                    if (!area.Covers(type) || !area.OwnerState.Active || area.IsReleased) continue;
                    if (area.CreatedVersion != Volatile.Read(ref area.ContentVersion)) DiscardArea(area, view);
                    if (area.Failed || !AreaApplies(area, view, selection)) continue;
                    // The snapshot is in precedence order: the first match is shown, any later one conflicts.
                    if (next == null) next = area;
                    else if (area.ShownInstead != next) Conflict(next, area, selection);
                }
            }
            if (activeArea != null && activeArea != next) Deactivate(view);
            if (next == null || (next.Element == null && !CreateArea(next, view, context))) return;
            if (activeArea == null) Activate(next, view);
            else KeepVanillaHidden(view);
            if (next.Update == null) return;
            try { next.Update(selection, next.Element); }
            catch (Exception ex) { FailArea(next, view, "update", ex); }
        }

        private void KeepVanillaHidden(IBuildingPanelView<TElement> view)
        {
            for (int index = 0; index < recorded.Length; index++)
            {
                BuildingPanelVisibility current = view.GetVanilla(index);
                if (current == Hidden) continue;
                recorded[index] = current;
                view.SetVanilla(index, Hidden);
            }
        }

        // Cold path: the replacement takes Vanilla's recruitment area.
        private void Activate(Area area, IBuildingPanelView<TElement> view)
        {
            int count = view.VanillaPanelCount;
            if (recorded == null || recorded.Length != count) recorded = new BuildingPanelVisibility[count];
            for (int index = 0; index < count; index++)
            {
                recorded[index] = view.GetVanilla(index);
                view.SetVanilla(index, Hidden);
            }
            view.SetShown(area.Element, true);
            area.Shown = true;
            activeArea = area;
        }

        // Cold path: hands the area back to Vanilla. A released replacement also loses its element.
        private void Deactivate(IBuildingPanelView<TElement> view)
        {
            Area area = activeArea;
            activeArea = null;
            for (int index = 0; index < recorded.Length; index++)
                if (view.GetVanilla(index) == Hidden) view.SetVanilla(index, recorded[index]);
            if (area.Element == null) return;
            if (area.Shown) { view.SetShown(area.Element, false); area.Shown = false; }
            if (area.IsReleased) { view.RemoveArea(area.Element); area.Element = null; }
        }

        private bool AreaApplies(Area area, IBuildingPanelView<TElement> view, BuildingPanelSelection selection)
        {
            if (area.AppliesTo == null) return true;
            try { return area.AppliesTo(selection); }
            catch (Exception ex) { FailArea(area, view, "filter", ex); return false; }
        }

        // Cold path: a lower-precedence replacement also matched the selected building. Reported once for each
        // replacement shown instead of it, so a steady refresh of the same building reports nothing.
        private void Conflict(Area shown, Area other, BuildingPanelSelection selection)
        {
            other.ShownInstead = shown;
            try { conflict?.Invoke(shown, other, selection); } catch { }
        }

        // Cold path: creates the replacement once per panel, attached and hidden.
        private bool CreateArea(Area area, IBuildingPanelView<TElement> view, object context)
        {
            TElement element;
            try
            {
                element = area.Factory(context) ?? throw new InvalidOperationException("The factory returned null.");
                view.InsertArea(element);
                view.SetShown(element, false);
            }
            catch (Exception ex) { FailArea(area, view, "factory", ex); return false; }
            area.Element = element;
            area.Shown = false;
            return true;
        }

        // Cold path: RequestContentRefresh drops the replacement and the failure of this variant.
        private void DiscardArea(Area area, IBuildingPanelView<TElement> view)
        {
            if (activeArea == area) Deactivate(view);
            if (area.Element != null) { try { view.RemoveArea(area.Element); } catch { } }
            area.Element = null; area.Shown = false; area.Failed = false;
            area.CreatedVersion = Volatile.Read(ref area.ContentVersion);
        }

        // Cold path: disables the replacement and returns the area to Vanilla.
        private void FailArea(Area area, IBuildingPanelView<TElement> view, string phase, Exception error)
        {
            area.Failed = true;
            if (activeArea == area) Deactivate(view);
            else if (area.Element != null && area.Shown) { view.SetShown(area.Element, false); area.Shown = false; }
            try { failure?.Invoke(area.Key, phase, error); } catch { }
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

        // Cold path: creates the element once per panel and inserts it at its ordered position, hidden.
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
            try { failure?.Invoke(entry.Key, phase, error); } catch { }
        }

        internal sealed class OwnerState
        {
            internal volatile bool Active = true;
        }

        internal sealed class Entry : IBuildingPanelExtensionRegistration
        {
            internal readonly string Owner, Id;
            internal readonly int Order;
            internal readonly bool ShowInMapEditor;
            internal readonly OwnerState OwnerState;
            internal readonly Func<object, TElement> Factory;
            internal readonly Func<BuildingPanelSelection, bool> AppliesTo;
            internal readonly Action<BuildingPanelSelection, TElement> Update;
            internal volatile bool Visible = true;
            internal int ContentVersion;
            // Per-panel state; Unity thread only.
            internal TElement Element;
            internal bool Shown, Failed;
            internal int CreatedVersion;

            internal Entry(string owner, string id, int order, bool showInMapEditor, OwnerState ownerState,
                Func<object, TElement> factory, Func<BuildingPanelSelection, bool> appliesTo,
                Action<BuildingPanelSelection, TElement> update)
            {
                Owner = owner; Id = id; Order = order; ShowInMapEditor = showInMapEditor; OwnerState = ownerState;
                Factory = factory; AppliesTo = appliesTo; Update = update;
            }

            internal string Key => Owner + "/" + Id;
            public void SetVisible(bool visible) => Visible = visible;
            public void RequestContentRefresh() => Interlocked.Increment(ref ContentVersion);
        }

        internal sealed class Area : IBuildingPanelRecruitmentAreaRegistration
        {
            private readonly BuildingPanelExtensionRegistry<TElement> registry;
            internal readonly string Owner, Id;
            internal readonly int[] Types;
            internal readonly OwnerState OwnerState;
            internal readonly Func<object, TElement> Factory;
            internal readonly Func<BuildingPanelSelection, bool> AppliesTo;
            internal readonly Action<BuildingPanelSelection, TElement> Update;
            internal int ContentVersion;
            private int released;
            // Per-panel state; Unity thread only.
            internal TElement Element;
            internal bool Shown, Failed;
            internal int CreatedVersion;
            // The replacement last shown in this one's place on a building both matched; Unity thread only.
            internal Area ShownInstead;

            internal Area(BuildingPanelExtensionRegistry<TElement> registry, string owner, string id, int[] types,
                OwnerState ownerState, Func<object, TElement> factory, Func<BuildingPanelSelection, bool> appliesTo,
                Action<BuildingPanelSelection, TElement> update)
            {
                this.registry = registry; Owner = owner; Id = id; Types = types; OwnerState = ownerState;
                Factory = factory; AppliesTo = appliesTo; Update = update;
            }

            internal string Key => Owner + "/" + Id;
            internal bool Covers(int type)
            {
                for (int index = 0; index < Types.Length; index++) if (Types[index] == type) return true;
                return false;
            }
            public bool IsReleased => Volatile.Read(ref released) != 0;
            public void Release() { if (Interlocked.Exchange(ref released, 1) == 0) registry.RemoveArea(this); }
            public void RequestContentRefresh() => Interlocked.Increment(ref ContentVersion);
        }
    }
}
