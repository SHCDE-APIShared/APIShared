using System;
using System.Collections.Generic;
using Noesis;
using SHCDESE.Interop;

namespace APIShared.Presentation
{
    // Visual operations of one building HUD instance for the recruitment-area replacements. Production binds
    // Noesis in BuildingPanelSections; tests use a fake, so the decision and restore protocol runs without the
    // Noesis runtime. Vanilla visibilities are raw Noesis values: Collapsed 0, Hidden 1, Visible 2.
    internal interface IBuildingPanelRecruitmentAreaView
    {
        // Runs the entry's factory for this HUD instance and adds its control collapsed; throws on failure.
        object Create(BuildingPanelRecruitmentAreaEntry entry, long generation);
        void Remove(object element);
        void SetShown(object element, bool shown);
        int VanillaCount { get; }
        int GetVanilla(int index);
        void SetVanilla(int index, int visibility);
    }

    // Registrations, precedence and the per-check decision of the recruitment-area replacements. Unity thread
    // only (enforced by BuildingPanelSections). Registration publishes a precedence-ordered snapshot; the
    // per-check walk allocates nothing once each variant's control exists and each competing pair has been
    // observed. Consumer callbacks are isolated: a throwing selector or callback keeps that variant out of the
    // context in which it failed.
    //
    // Hiding protocol: MainViewModel.setUpInbuilding is the only game code that writes the visibility of the
    // recruitment panels and the full-panel click mask, and it writes only Hidden and Visible (Cecil scan of the
    // audited Assembly-CSharp; see BUILDING_PANELS.md). The shown replacement therefore hides them with
    // Collapsed, records what Vanilla had, re-records and re-hides any later Vanilla write while it stays
    // active, and on deactivation restores only panels that still carry its Collapsed, so a newer Vanilla
    // write always wins.
    internal sealed class BuildingPanelRecruitmentAreas
    {
        internal const int Collapsed = 0;
        private readonly Action<string, Exception> report;
        private readonly Action<BuildingPanelRecruitmentAreaEntry, BuildingPanelRecruitmentAreaEntry, BuildingPanelContext> overlap;
        private BuildingPanelRecruitmentAreaEntry[] entries = Array.Empty<BuildingPanelRecruitmentAreaEntry>();
        private BuildingPanelRecruitmentAreaEntry active;
        private int[] recorded = Array.Empty<int>();

        // report receives "phase/owner/variant" and the error; overlap the shown and the other variant, once per pair.
        internal BuildingPanelRecruitmentAreas(Action<string, Exception> report,
            Action<BuildingPanelRecruitmentAreaEntry, BuildingPanelRecruitmentAreaEntry, BuildingPanelContext> overlap)
        { this.report = report; this.overlap = overlap; }

        internal bool HasEntries => entries.Length != 0;
        internal BuildingPanelRecruitmentAreaEntry Active => active;
        internal BuildingPanelRecruitmentAreaEntry[] Entries => entries;

        // Only invalid options or a variant ID the owner already registered are refused; overlapping types are not.
        internal bool TryRegister(BuildingPanelRecruitmentAreaOptions options, out BuildingPanelRecruitmentAreaEntry entry, out string reason)
        {
            entry = null;
            if (options == null || string.IsNullOrWhiteSpace(options.OwnerGuid) || string.IsNullOrWhiteSpace(options.VariantId) ||
                options.Factory == null)
            { reason = "Options with a nonempty owner GUID, variant ID and factory are required."; return false; }
            if (options.BuildingTypes == null || options.BuildingTypes.Count == 0)
            { reason = "At least one building type is required."; return false; }
            foreach (BuildingPanelRecruitmentAreaEntry existing in entries)
                if (existing.Owner == options.OwnerGuid && existing.Id == options.VariantId)
                { reason = "Duplicate recruitment-area variant: " + options.OwnerGuid + "/" + options.VariantId; return false; }
            entry = new BuildingPanelRecruitmentAreaEntry(options);
            var next = new BuildingPanelRecruitmentAreaEntry[entries.Length + 1];
            Array.Copy(entries, next, entries.Length);
            next[entries.Length] = entry;
            Array.Sort(next, BuildingPanelRecruitmentAreaEntry.Compare);
            entries = next;
            reason = "";
            return true;
        }

        // One GUI check for the bound HUD instance. context is null when no building panel is open. Returns
        // whether a replacement is shown afterwards.
        internal bool Step(IBuildingPanelRecruitmentAreaView view, BuildingPanelContext context, long generation, bool mapEditor)
        {
            BuildingPanelRecruitmentAreaEntry next = null;
            // Nothing is recruited in the map editor, so no replacement applies there.
            if (context != null && !mapEditor)
            {
                foreach (BuildingPanelRecruitmentAreaEntry entry in entries)
                {
                    if (!entry.Enabled || !entry.Types.Contains(context.BuildingType)) continue;
                    if (entry.Faulted != null)
                    {
                        if (ReferenceEquals(entry.Faulted, context)) continue;
                        entry.Faulted = null;
                    }
                    if (entry.ElementGeneration == generation && entry.Element == null) continue;
                    if (!Selects(entry, context)) continue;
                    // The snapshot is in precedence order: the first usable match is shown, later ones compete.
                    if (next != null) { Observe(next, entry, context); continue; }
                    if (entry.ElementGeneration != generation) Create(view, entry, generation);
                    if (entry.Element != null) next = entry;
                }
            }
            if (active != null && active != next) Deactivate(view);
            if (next == null) return false;
            if (active == null) return Activate(view, next, context);
            KeepVanillaHidden(view);
            BuildingPanelSectionEventArgs shown = next.Shown;
            if (ReferenceEquals(shown.Context, context))
            {
                if (Invoke(next, next.Updated, shown, "updated")) return true;
                next.Faulted = context;
                Deactivate(view);
                return false;
            }
            // The same variant now serves another building, owner or viewpoint: a new showing.
            next.Shown = null;
            Invoke(next, next.Closed, shown, "closed");
            return Open(view, next, context);
        }

        // The HUD instance is being replaced: hand its area back to Vanilla, then forget every control created
        // for it. Observed overlaps and registrations persist.
        internal void Release(IBuildingPanelRecruitmentAreaView view)
        {
            if (active != null)
            {
                if (view != null) Deactivate(view);
                else
                {
                    // No bound view: nothing was hidden through it; only end the showing.
                    BuildingPanelRecruitmentAreaEntry ending = active;
                    active = null;
                    BuildingPanelSectionEventArgs args = ending.Shown;
                    ending.Shown = null;
                    if (args != null) Invoke(ending, ending.Closed, args, "closed");
                }
            }
            foreach (BuildingPanelRecruitmentAreaEntry entry in entries)
            {
                if (entry.Element != null && view != null)
                {
                    try { view.Remove(entry.Element); }
                    catch (Exception error) { report("release/" + entry.Owner + "/" + entry.Id, error); }
                }
                entry.Element = null;
                entry.ElementGeneration = -1;
                entry.Faulted = null;
            }
            recorded = Array.Empty<int>();
        }

        // Cold path, for settings presentation: allocates the immutable explanation; changes nothing.
        internal bool TryGetStatus(string owner, string variantId, out BuildingPanelRecruitmentAreaStatus status, out string reason)
        {
            status = null;
            BuildingPanelRecruitmentAreaEntry[] current = entries;
            int at = -1;
            for (int index = 0; index < current.Length; index++)
                if (current[index].Owner == owner && current[index].Id == variantId) { at = index; break; }
            if (at < 0) { reason = "No recruitment-area variant " + owner + "/" + variantId + " is registered."; return false; }
            BuildingPanelRecruitmentAreaEntry self = current[at];
            var rivals = new List<BuildingPanelRecruitmentAreaRival>();
            for (int index = 0; index < current.Length; index++)
            {
                if (index == at) continue;
                BuildingPanelRecruitmentAreaEntry other = current[index];
                var shared = new List<eStructs>();
                foreach (eStructs type in self.TypeList)
                    if (other.Types.Contains(type) && !shared.Contains(type)) shared.Add(type);
                if (shared.Count == 0) continue;
                rivals.Add(new BuildingPanelRecruitmentAreaRival(other.Owner, other.Id, other.Selects != null, other.Enabled,
                    shared.AsReadOnly(), index < at, self.Overlaps != null && self.Overlaps.Contains(other)));
            }
            status = new BuildingPanelRecruitmentAreaStatus(self.Owner, self.Id, self.Selects != null, self.Enabled,
                self.TypeList, ReferenceEquals(active, self), rivals.AsReadOnly());
            reason = "";
            return true;
        }

        private bool Selects(BuildingPanelRecruitmentAreaEntry entry, BuildingPanelContext context)
        {
            if (entry.Selects == null) return true;
            try { return entry.Selects(context); }
            catch (Exception error)
            {
                report("selector/" + entry.Owner + "/" + entry.Id, error);
                entry.Faulted = context;
                return false;
            }
        }

        // Cold path, once per HUD instance and variant; a failure skips the variant for this HUD instance.
        private void Create(IBuildingPanelRecruitmentAreaView view, BuildingPanelRecruitmentAreaEntry entry, long generation)
        {
            entry.ElementGeneration = generation;
            entry.Element = null;
            try
            {
                object element = view.Create(entry, generation);
                if (element == null) throw new InvalidOperationException("The factory returned no control.");
                entry.Element = element;
            }
            catch (Exception error) { report("factory/" + entry.Owner + "/" + entry.Id, error); }
        }

        // Cold path: the replacement takes Vanilla's recruitment area.
        private bool Activate(IBuildingPanelRecruitmentAreaView view, BuildingPanelRecruitmentAreaEntry entry, BuildingPanelContext context)
        {
            int count = view.VanillaCount;
            if (recorded.Length != count) recorded = new int[count];
            for (int index = 0; index < count; index++)
            {
                recorded[index] = view.GetVanilla(index);
                view.SetVanilla(index, Collapsed);
            }
            active = entry;
            return Open(view, entry, context);
        }

        private bool Open(IBuildingPanelRecruitmentAreaView view, BuildingPanelRecruitmentAreaEntry entry, BuildingPanelContext context)
        {
            var args = new BuildingPanelSectionEventArgs(context, entry.Element as FrameworkElement);
            entry.Shown = args;
            view.SetShown(entry.Element, true);
            if (Invoke(entry, entry.Opened, args, "opened")) return true;
            entry.Faulted = context;
            Deactivate(view);
            return false;
        }

        private void KeepVanillaHidden(IBuildingPanelRecruitmentAreaView view)
        {
            for (int index = 0; index < recorded.Length; index++)
            {
                int current = view.GetVanilla(index);
                if (current == Collapsed) continue;
                recorded[index] = current;
                view.SetVanilla(index, Collapsed);
            }
        }

        // Cold path: hands the area back to Vanilla and ends the showing.
        private void Deactivate(IBuildingPanelRecruitmentAreaView view)
        {
            BuildingPanelRecruitmentAreaEntry entry = active;
            active = null;
            for (int index = 0; index < recorded.Length; index++)
                if (view.GetVanilla(index) == Collapsed) view.SetVanilla(index, recorded[index]);
            if (entry.Element != null) view.SetShown(entry.Element, false);
            BuildingPanelSectionEventArgs args = entry.Shown;
            entry.Shown = null;
            if (args != null) Invoke(entry, entry.Closed, args, "closed");
        }

        // Cold path: a lower-precedence variant also selected the open building. Recorded once per pair, in both
        // directions, so a steady panel neither allocates nor logs again.
        private void Observe(BuildingPanelRecruitmentAreaEntry shown, BuildingPanelRecruitmentAreaEntry other, BuildingPanelContext context)
        {
            if (shown.Overlaps != null && shown.Overlaps.Contains(other)) return;
            (shown.Overlaps ?? (shown.Overlaps = new List<BuildingPanelRecruitmentAreaEntry>())).Add(other);
            (other.Overlaps ?? (other.Overlaps = new List<BuildingPanelRecruitmentAreaEntry>())).Add(shown);
            try { overlap?.Invoke(shown, other, context); } catch { }
        }

        private bool Invoke(BuildingPanelRecruitmentAreaEntry entry, Action<BuildingPanelSectionEventArgs> callback,
            BuildingPanelSectionEventArgs args, string phase)
        {
            if (callback == null) return true;
            try { callback(args); return true; }
            catch (Exception error) { report(phase + "/" + entry.Owner + "/" + entry.Id, error); return false; }
        }
    }

    // Unity-thread state of one recruitment-area variant.
    internal sealed class BuildingPanelRecruitmentAreaEntry
    {
        internal readonly string Owner, Id;
        internal readonly HashSet<eStructs> Types;
        internal readonly IReadOnlyList<eStructs> TypeList;
        internal readonly Func<BuildingPanelSectionFactoryContext, FrameworkElement> Factory;
        internal readonly Func<BuildingPanelContext, bool> Selects;
        internal readonly Action<BuildingPanelSectionEventArgs> Opened, Updated, Closed;
        internal volatile bool Enabled = true;
        internal object Element;
        internal long ElementGeneration = -1;
        internal BuildingPanelSectionEventArgs Shown;
        // The context in which a selector or callback failed; the variant stays out until the context changes.
        internal BuildingPanelContext Faulted;
        // Variants this one was seen competing with on one open building, in either direction.
        internal List<BuildingPanelRecruitmentAreaEntry> Overlaps;

        internal BuildingPanelRecruitmentAreaEntry(BuildingPanelRecruitmentAreaOptions options)
        {
            Owner = options.OwnerGuid; Id = options.VariantId;
            TypeList = new List<eStructs>(options.BuildingTypes).AsReadOnly();
            Types = new HashSet<eStructs>(options.BuildingTypes);
            Factory = options.Factory; Selects = options.Selects;
            Opened = options.Opened; Updated = options.Updated; Closed = options.Closed;
        }

        // Precedence where several variants select one building: a variant with a selector (the narrower claim)
        // before one for every building of its types, then ordinal owner GUID, then ordinal variant ID.
        internal static int Compare(BuildingPanelRecruitmentAreaEntry a, BuildingPanelRecruitmentAreaEntry b)
        {
            int result = (a.Selects == null).CompareTo(b.Selects == null);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.Id, b.Id);
        }
    }
}
