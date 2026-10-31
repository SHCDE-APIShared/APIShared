using System;
using System.Collections.Generic;

namespace APIShared.Presentation
{
    // Pure production registry: no Unity objects, scheduler, polling or game lookups.
    // Mutations/evaluation are serialized by the Unity-facing owner. Queries use cached states only.
    internal sealed class IngameMenuRegistry
    {
        internal const string VanillaOwner = "APIShared.Vanilla";
        internal sealed class Entry
        {
            internal string Owner, Id;
            internal int Order;
            internal long Sequence;
            internal bool Legacy, Vanilla, Known, Dirty = true, FailureReported;
            internal IngameMenuActionOptions Options;
            internal Func<IngameMenuActionState> Provider;
            internal Action Execute;
            internal IngameMenuActionState State;
        }
        internal sealed class Layout
        {
            internal readonly Entry[,] Slots = new Entry[9, 2];
            internal readonly List<Entry> Overflow = new List<Entry>();
        }
        private readonly List<Entry> entries = new List<Entry>();
        private readonly Entry[] vanilla = new Entry[9];
        private long sequence;
        internal bool Evaluating { get; private set; }
        internal IngameMenuRegistry()
        {
            for (int i = 0; i < vanilla.Length; i++)
                vanilla[i] = new Entry { Owner = VanillaOwner, Id = ((IngameMenuRow)i).ToString(), Vanilla = true,
                    Options = new IngameMenuActionOptions((IngameMenuRow)i, IngameMenuSlot.Right), Dirty = false };
        }
        internal Entry[] Entries => entries.ToArray();
        internal static bool ValidRow(IngameMenuRow row) => row >= IngameMenuRow.Save && row <= IngameMenuRow.Resume;
        internal bool CanRegister(string owner, string id, Func<IngameMenuActionState> provider, Action execute,
            IngameMenuActionOptions options, bool legacy, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(owner) || owner == VanillaOwner || string.IsNullOrWhiteSpace(id) || provider == null || execute == null)
            { reason = "A non-reserved owner GUID, action ID, state provider and command are required."; return false; }
            if (Evaluating) { reason = "Register actions outside a state-provider callback."; return false; }
            foreach (var entry in entries)
                if (entry.Owner == owner && entry.Id == id) { reason = "Duplicate menu action: " + owner + "/" + id; return false; }
            if (!legacy && options == null) { reason = "Placement options are required."; return false; }
            if (!legacy && options.Row.HasValue)
            {
                if (!ValidRow(options.Row.Value) || (options.Slot != IngameMenuSlot.Left && options.Slot != IngameMenuSlot.Right))
                { reason = "Unknown menu row or slot."; return false; }
                if (options.Slot == IngameMenuSlot.Right && !options.ReplaceVanilla)
                { reason = "Replacing Vanilla requires replaceVanilla: true."; return false; }
                if (!options.ReplaceExisting)
                    foreach (var entry in entries)
                        if ((!entry.Legacy && entry.Options.Row == options.Row && entry.Options.Slot == options.Slot) ||
                            (entry.Legacy && options.Row == IngameMenuRow.QuitMission && options.Slot == IngameMenuSlot.Left))
                        { reason = "Menu position already claimed by " + entry.Owner + "/" + entry.Id + "; use replaceExisting explicitly."; return false; }
            }
            return true;
        }
        internal Entry Register(string owner, string id, Func<IngameMenuActionState> provider, Action execute,
            IngameMenuActionOptions options, bool legacy, int order)
        {
            var entry = new Entry { Owner = owner, Id = id, Provider = provider, Execute = execute,
                Options = options, Legacy = legacy, Order = order, Sequence = ++sequence };
            entries.Add(entry);
            return entry;
        }
        internal void Invalidate(string owner, string id = null)
        {
            foreach (var entry in entries) if (entry.Owner == owner && (id == null || entry.Id == id)) entry.Dirty = true;
        }
        internal void Evaluate(bool all, Action<Entry, Exception> failure)
        {
            if (Evaluating) return;
            Evaluating = true;
            try
            {
                foreach (var entry in entries)
                {
                    if (!all && !entry.Dirty) continue;
                    // Clear before callback: a new invalidation during evaluation remains pending for the next event.
                    entry.Dirty = false;
                    try { entry.State = entry.Provider(); }
                    catch (Exception error) { entry.State = null; failure?.Invoke(entry, error); }
                    entry.Known = true;
                }
            }
            finally { Evaluating = false; }
        }
        internal void SetVanilla(IngameMenuRow row, IngameMenuActionState state)
        { vanilla[(int)row].State = state; vanilla[(int)row].Known = true; }
        internal Entry Vanilla(IngameMenuRow row) => vanilla[(int)row];
        internal static bool Visible(Entry entry) => entry?.State?.Visible == true;
        private static int Compare(Entry a, Entry b)
        {
            int n = a.Order.CompareTo(b.Order);
            if (n == 0) n = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
            return n != 0 ? n : StringComparer.Ordinal.Compare(a.Id, b.Id);
        }
        internal Layout Resolve()
        {
            var result = new Layout();
            for (int i = 0; i < vanilla.Length; i++) if (Visible(vanilla[i])) result.Slots[i, 1] = vanilla[i];
            Entry legacy = null;
            foreach (var entry in entries)
            {
                if (!Visible(entry)) continue;
                if (entry.Legacy)
                { if (legacy == null || Compare(entry, legacy) < 0) legacy = entry; continue; }
                if (!entry.Options.Row.HasValue) continue;
                int row = (int)entry.Options.Row.Value, slot = (int)entry.Options.Slot;
                Entry previous = result.Slots[row, slot];
                if (previous == null || previous.Vanilla || entry.Sequence > previous.Sequence) result.Slots[row, slot] = entry;
            }
            if (result.Slots[(int)IngameMenuRow.QuitMission, 0] == null)
                result.Slots[(int)IngameMenuRow.QuitMission, 0] = legacy;
            var direct = new HashSet<Entry>();
            foreach (var entry in result.Slots) if (entry != null) direct.Add(entry);
            foreach (var entry in entries) if (Visible(entry) && !direct.Contains(entry)) result.Overflow.Add(entry);
            foreach (var entry in vanilla) if (Visible(entry) && !direct.Contains(entry)) result.Overflow.Add(entry);
            result.Overflow.Sort(Compare);
            return result;
        }
        internal bool CanExecute(Entry entry, bool additional)
        {
            if (!Visible(entry) || !entry.State.Enabled) return false;
            var layout = Resolve();
            if (additional) return layout.Overflow.Contains(entry);
            foreach (var occupant in layout.Slots) if (ReferenceEquals(occupant, entry)) return true;
            return false;
        }
        internal IngameMenuRowState GetRow(IngameMenuRow row)
        {
            if (!ValidRow(row)) throw new ArgumentOutOfRangeException(nameof(row));
            var layout = Resolve();
            return new IngameMenuRowState(row, GetSlot(row, IngameMenuSlot.Left, layout), GetSlot(row, IngameMenuSlot.Right, layout));
        }
        private IngameMenuSlotState GetSlot(IngameMenuRow row, IngameMenuSlot slot, Layout layout)
        {
            var claims = new List<Entry>();
            var old = new List<Entry>();
            foreach (var entry in entries)
                if (entry.Legacy)
                { if (row == IngameMenuRow.QuitMission && slot == IngameMenuSlot.Left) old.Add(entry); }
                else if (entry.Options.Row == row && entry.Options.Slot == slot) claims.Add(entry);
            claims.Sort((a, b) => b.Sequence.CompareTo(a.Sequence));
            old.Sort(Compare); claims.AddRange(old);
            if (slot == IngameMenuSlot.Right) claims.Add(vanilla[(int)row]);
            Entry current = layout.Slots[(int)row, (int)slot];
            var snapshots = new IngameMenuActionInfo[claims.Count];
            IngameMenuActionInfo effective = null;
            for (int i = 0; i < snapshots.Length; i++)
            {
                Entry entry = claims[i];
                snapshots[i] = new IngameMenuActionInfo(entry.Owner, entry.Id, entry.Vanilla, entry.Known, entry.State, ReferenceEquals(entry, current));
                if (ReferenceEquals(entry, current)) effective = snapshots[i];
            }
            return new IngameMenuSlotState(slot, effective, snapshots);
        }
    }
}
