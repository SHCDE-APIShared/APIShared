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

    // Registrations, order and the refresh walk of the building panel extensions and recruitment-area claims,
    // independent of Noesis. Registration (any thread, under the lock) publishes ordered immutable snapshots;
    // Apply, Reset and ReleaseActive run on the Unity thread and keep per-panel state in the items. Apply
    // allocates nothing and touches the view only when a visibility changes; element creation and failure
    // reporting are the cold paths, kept in separate methods. Every consumer callback is isolated: a throw
    // disables only that item until RequestContentRefresh.
    //
    // Claims are exclusive per building type: TryClaim refuses any overlap, so at most one claim can own a
    // selection. An active claim hides Vanilla's recruitment panels with Collapsed and records the visibility
    // they had. While it stays active, a panel Vanilla rewrites is re-recorded and hidden again. Deactivation
    // restores a panel only if it still carries Collapsed from us, so a newer Vanilla write always wins.
    internal sealed class BuildingPanelExtensionRegistry<TElement> where TElement : class
    {
        private const BuildingPanelVisibility Hidden = BuildingPanelVisibility.Collapsed;
        private readonly object sync = new object();
        private readonly Dictionary<string, OwnerState> owners = new Dictionary<string, OwnerState>(StringComparer.Ordinal);
        private readonly Action<string, string, Exception> failure;
        private Entry[] snapshot = new Entry[0];
        private Claim[] claims = new Claim[0];
        // Per panel; Unity thread only.
        private bool hostShown;
        private Claim activeClaim;
        private BuildingPanelVisibility[] recorded;

        // failure receives the item key ("owner/id"), the phase ("factory", "filter", "update") and the exception.
        internal BuildingPanelExtensionRegistry(Action<string, string, Exception> failure) { this.failure = failure; }

        internal bool HasEntries => Volatile.Read(ref snapshot).Length != 0 || Volatile.Read(ref claims).Length != 0 || activeClaim != null;
        internal Entry[] Entries => Volatile.Read(ref snapshot);
        internal Claim ActiveClaim => activeClaim;

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
                entry = new Entry(owner, id, order, Owner(owner), factory, appliesTo, update);
                var next = new Entry[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = entry;
                Array.Sort(next, Compare);
                Volatile.Write(ref snapshot, next);
            }
            reason = null;
            return true;
        }

        // conflictOwner names the owner of the existing claim when the request overlaps one.
        internal bool TryClaim(string owner, string id, eStructs[] types, Func<object, TElement> factory,
            Func<BuildingPanelSelection, bool> appliesTo, Action<BuildingPanelSelection, TElement> update,
            out Claim claim, out string reason, out string conflictOwner)
        {
            claim = null; conflictOwner = null;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || factory == null || types == null || types.Length == 0)
            { reason = "An owner GUID, a claim ID, at least one building type and a factory are required."; return false; }
            var codes = new int[types.Length];
            for (int index = 0; index < types.Length; index++) codes[index] = (int)types[index];
            lock (sync)
            {
                Claim[] current = claims;
                for (int index = 0; index < current.Length; index++)
                {
                    Claim existing = current[index];
                    if (existing.Owner == owner && existing.Id == id)
                    { conflictOwner = owner; reason = "The owner already claimed this ID."; return false; }
                    for (int type = 0; type < types.Length; type++)
                        if (existing.Covers(codes[type]))
                        {
                            conflictOwner = existing.Owner;
                            reason = "Building type " + types[type] + " is already claimed by " + existing.Key + ".";
                            return false;
                        }
                }
                claim = new Claim(this, owner, id, codes, Owner(owner), factory, appliesTo, update);
                var next = new Claim[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = claim;
                Volatile.Write(ref claims, next);
            }
            reason = null;
            return true;
        }

        private void RemoveClaim(Claim claim)
        {
            lock (sync)
            {
                Claim[] current = claims;
                int at = Array.IndexOf(current, claim);
                if (at < 0) return;
                var next = new Claim[current.Length - 1];
                Array.Copy(current, 0, next, 0, at);
                Array.Copy(current, at + 1, next, at, current.Length - at - 1);
                Volatile.Write(ref claims, next);
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

        // The panel was replaced: forget everything created for the old one. The new panel starts as Vanilla
        // made it, so nothing is restored. Failures persist.
        internal void Reset()
        {
            Entry[] entries = Volatile.Read(ref snapshot);
            for (int index = 0; index < entries.Length; index++) { entries[index].Element = null; entries[index].Shown = false; }
            Claim[] current = Volatile.Read(ref claims);
            for (int index = 0; index < current.Length; index++) { current[index].Element = null; current[index].Shown = false; }
            if (activeClaim != null) { activeClaim.Element = null; activeClaim.Shown = false; }
            activeClaim = null;
            recorded = null;
            hostShown = false;
        }

        // Mission end: restore Vanilla's recruitment panels if a claim holds them.
        internal void ReleaseActive(IBuildingPanelView<TElement> view)
        {
            if (activeClaim != null) Deactivate(view);
        }

        // One refresh. context is passed to factories; selection is meaningful only when hasSelection is true.
        internal void Apply(IBuildingPanelView<TElement> view, object context, BuildingPanelSelection selection, bool hasSelection)
        {
            ApplyClaims(view, context, selection, hasSelection);
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

        private void ApplyClaims(IBuildingPanelView<TElement> view, object context, BuildingPanelSelection selection, bool hasSelection)
        {
            Claim[] current = Volatile.Read(ref claims);
            if (current.Length == 0 && activeClaim == null) return;
            Claim next = null;
            if (hasSelection)
            {
                int type = (int)selection.BuildingType;
                for (int index = 0; index < current.Length; index++)
                {
                    Claim claim = current[index];
                    if (!claim.Covers(type)) continue;
                    // Types are exclusive: no other claim can own this selection.
                    if (claim.OwnerState.Active && !claim.IsReleased)
                    {
                        if (claim.CreatedVersion != Volatile.Read(ref claim.ContentVersion)) DiscardClaim(claim, view);
                        if (!claim.Failed && ClaimApplies(claim, view, selection)) next = claim;
                    }
                    break;
                }
            }
            if (activeClaim != null && activeClaim != next) Deactivate(view);
            if (next == null || (next.Element == null && !CreateClaim(next, view, context))) return;
            if (activeClaim == null) Activate(next, view);
            else KeepVanillaHidden(view);
            if (next.Update == null) return;
            try { next.Update(selection, next.Element); }
            catch (Exception ex) { FailClaim(next, view, "update", ex); }
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

        // Cold path: the claim takes Vanilla's recruitment area.
        private void Activate(Claim claim, IBuildingPanelView<TElement> view)
        {
            int count = view.VanillaPanelCount;
            if (recorded == null || recorded.Length != count) recorded = new BuildingPanelVisibility[count];
            for (int index = 0; index < count; index++)
            {
                recorded[index] = view.GetVanilla(index);
                view.SetVanilla(index, Hidden);
            }
            view.SetShown(claim.Element, true);
            claim.Shown = true;
            activeClaim = claim;
        }

        // Cold path: hands the area back to Vanilla. A released claim also loses its element.
        private void Deactivate(IBuildingPanelView<TElement> view)
        {
            Claim claim = activeClaim;
            activeClaim = null;
            for (int index = 0; index < recorded.Length; index++)
                if (view.GetVanilla(index) == Hidden) view.SetVanilla(index, recorded[index]);
            if (claim.Element == null) return;
            if (claim.Shown) { view.SetShown(claim.Element, false); claim.Shown = false; }
            if (claim.IsReleased) { view.RemoveArea(claim.Element); claim.Element = null; }
        }

        private bool ClaimApplies(Claim claim, IBuildingPanelView<TElement> view, BuildingPanelSelection selection)
        {
            if (claim.AppliesTo == null) return true;
            try { return claim.AppliesTo(selection); }
            catch (Exception ex) { FailClaim(claim, view, "filter", ex); return false; }
        }

        // Cold path: creates the replacement once per panel, attached and hidden.
        private bool CreateClaim(Claim claim, IBuildingPanelView<TElement> view, object context)
        {
            TElement element;
            try
            {
                element = claim.Factory(context) ?? throw new InvalidOperationException("The factory returned null.");
                view.InsertArea(element);
                view.SetShown(element, false);
            }
            catch (Exception ex) { FailClaim(claim, view, "factory", ex); return false; }
            claim.Element = element;
            claim.Shown = false;
            return true;
        }

        // Cold path: RequestContentRefresh drops the replacement and the failure of this claim.
        private void DiscardClaim(Claim claim, IBuildingPanelView<TElement> view)
        {
            if (activeClaim == claim) Deactivate(view);
            if (claim.Element != null) { try { view.RemoveArea(claim.Element); } catch { } }
            claim.Element = null; claim.Shown = false; claim.Failed = false;
            claim.CreatedVersion = Volatile.Read(ref claim.ContentVersion);
        }

        // Cold path: disables the claim and returns the area to Vanilla.
        private void FailClaim(Claim claim, IBuildingPanelView<TElement> view, string phase, Exception error)
        {
            claim.Failed = true;
            if (activeClaim == claim) Deactivate(view);
            else if (claim.Element != null && claim.Shown) { view.SetShown(claim.Element, false); claim.Shown = false; }
            try { failure?.Invoke(claim.Key, phase, error); } catch { }
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

        internal sealed class Claim : IBuildingPanelRecruitmentAreaClaim
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

            internal Claim(BuildingPanelExtensionRegistry<TElement> registry, string owner, string id, int[] types,
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
            public void Release() { if (Interlocked.Exchange(ref released, 1) == 0) registry.RemoveClaim(this); }
            public void RequestContentRefresh() => Interlocked.Increment(ref ContentVersion);
        }
    }
}
