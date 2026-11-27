using System;
using System.Collections.Generic;
using APIShared.Events;

namespace APIShared.Presentation
{
    // Pure production registry: no Unity objects, scheduler, polling or game lookups.
    // Mutations/evaluation are serialized by the Unity-facing owner. Queries use cached states only.
    internal sealed class IngameMenuRegistry
    {
        internal const string VanillaOwner = "APIShared.Vanilla";
        internal abstract class Registration
        {
            internal string Owner, Id;
            internal bool Dirty = true, FailureReported;
        }
        internal sealed class Entry : Registration
        {
            internal int Order;
            internal long Sequence;
            internal bool Vanilla, Known, ContentUnavailable;
            internal IngameMenuActionOptions Options;
            internal Func<IngameMenuActionState> Provider;
            internal Action Execute;
            internal Func<IngameMenuContentContext, Noesis.Control> CreateContent;
            internal IngameMenuActionState State;
            internal static readonly Func<Entry, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id, entry.Order);
        }
        private sealed class Suppression : Registration
        {
            internal IngameMenuRow Row;
            internal Func<bool> ShouldSuppress;
            internal bool Active;
        }
        internal sealed class Layout
        {
            internal readonly Entry[,] Slots = new Entry[9, 2];
            internal readonly List<Entry> Overflow = new List<Entry>();
        }
        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<Suppression> suppressions = new List<Suppression>();
        private readonly Entry[] vanilla = new Entry[9];
        private readonly bool[] suppressed = new bool[(int)IngameMenuRow.Resume + 1];
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
        internal bool HasIdentity(string owner, string id)
        {
            foreach (var entry in entries) if (entry.Owner == owner && entry.Id == id) return true;
            foreach (var rule in suppressions) if (rule.Owner == owner && rule.Id == id) return true;
            return false;
        }
        internal bool CanRegister(string owner, string id, Func<IngameMenuActionState> provider, Action execute,
            IngameMenuActionOptions options, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(owner) || owner == VanillaOwner || string.IsNullOrWhiteSpace(id) || provider == null || execute == null)
            { reason = "A non-reserved owner GUID, action ID, state provider and command are required."; return false; }
            if (Evaluating) { reason = "Register actions outside a state-provider callback."; return false; }
            if (HasIdentity(owner, id)) { reason = "Duplicate menu action: " + owner + "/" + id; return false; }
            if (options == null) { reason = "Placement options are required."; return false; }
            if (options.Row.HasValue)
            {
                if (!ValidRow(options.Row.Value) || (options.Slot != IngameMenuSlot.Left && options.Slot != IngameMenuSlot.Right))
                { reason = "Unknown menu row or slot."; return false; }
                if (options.Slot == IngameMenuSlot.Right && !options.ReplaceVanilla)
                { reason = "Replacing Vanilla requires replaceVanilla: true."; return false; }
                if (!options.ReplaceExisting)
                    foreach (var entry in entries)
                        if (entry.Options.Row == options.Row && entry.Options.Slot == options.Slot)
                        { reason = "Menu position already claimed by " + entry.Owner + "/" + entry.Id + "; use replaceExisting explicitly."; return false; }
            }
            return true;
        }
        internal Entry Register(string owner, string id, Func<IngameMenuActionState> provider, Action execute,
            IngameMenuActionOptions options, int order)
        {
            var entry = new Entry { Owner = owner, Id = id, Provider = provider, Execute = execute,
                Options = options, Order = order, Sequence = ++sequence };
            entries.Add(entry);
            return entry;
        }
        internal bool CanRegisterSuppression(string owner, string id, IngameMenuRow row, Func<bool> shouldSuppress, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(owner) || owner == VanillaOwner || string.IsNullOrWhiteSpace(id) || shouldSuppress == null)
            { reason = "A non-reserved owner GUID, action ID and suppression provider are required."; return false; }
            if (!ValidRow(row)) { reason = "Unknown Vanilla menu row."; return false; }
            if (Evaluating) { reason = "Register suppression outside a state-provider callback."; return false; }
            if (HasIdentity(owner, id)) { reason = "Duplicate menu action: " + owner + "/" + id; return false; }
            return true;
        }
        internal void RegisterSuppression(string owner, string id, IngameMenuRow row, Func<bool> shouldSuppress) =>
            suppressions.Add(new Suppression { Owner = owner, Id = id, Row = row, ShouldSuppress = shouldSuppress });

        internal void Invalidate(string owner, string id = null)
        {
            foreach (var entry in entries) if (entry.Owner == owner && (id == null || entry.Id == id)) entry.Dirty = true;
            foreach (var rule in suppressions) if (rule.Owner == owner && (id == null || rule.Id == id)) rule.Dirty = true;
        }
        internal void Evaluate(bool all, Action<Registration, Exception> failure)
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
                EvaluateSuppressions(all, failure);
            }
            finally { Evaluating = false; }
        }
        private void EvaluateSuppressions(bool all, Action<Registration, Exception> failure)
        {
            if (suppressions.Count == 0) return;
            foreach (var rule in suppressions)
            {
                if (!all && !rule.Dirty) continue;
                rule.Dirty = false;
                try { rule.Active = rule.ShouldSuppress(); }
                catch (Exception error) { rule.Active = false; failure?.Invoke(rule, error); }
            }
            // Publish the combined result after every provider has returned. Cached queries
            // made inside a provider therefore see the previous complete suppression state.
            Array.Clear(suppressed, 0, suppressed.Length);
            foreach (var rule in suppressions)
                if (rule.Active) suppressed[(int)rule.Row] = true;
        }
        internal void SetVanilla(IngameMenuRow row, IngameMenuActionState state)
        { vanilla[(int)row].State = state; vanilla[(int)row].Known = true; }
        internal Entry Vanilla(IngameMenuRow row) => vanilla[(int)row];
        internal static bool Visible(Entry entry) => entry?.State?.Visible == true && !entry.ContentUnavailable;
        private bool IsSuppressed(Entry entry) => entry.Vanilla && suppressed[(int)entry.Options.Row.Value];
        private static int Compare(Entry a, Entry b) => RegistrationKey.Compare(Entry.Key(a), Entry.Key(b));
        internal Layout Resolve()
        {
            var result = new Layout();
            for (int i = 0; i < vanilla.Length; i++)
                if (Visible(vanilla[i]) && !IsSuppressed(vanilla[i])) result.Slots[i, 1] = vanilla[i];
            foreach (var entry in entries)
            {
                if (!Visible(entry) || !entry.Options.Row.HasValue) continue;
                int row = (int)entry.Options.Row.Value, slot = (int)entry.Options.Slot;
                Entry previous = result.Slots[row, slot];
                if (previous == null || previous.Vanilla || entry.Sequence > previous.Sequence) result.Slots[row, slot] = entry;
            }
            var direct = new HashSet<Entry>();
            foreach (var entry in result.Slots) if (entry != null) direct.Add(entry);
            foreach (var entry in entries) if (Visible(entry) && !direct.Contains(entry)) result.Overflow.Add(entry);
            foreach (var entry in vanilla)
                if (Visible(entry) && !IsSuppressed(entry) && !direct.Contains(entry)) result.Overflow.Add(entry);
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
            foreach (var entry in entries)
                if (entry.Options.Row == row && entry.Options.Slot == slot) claims.Add(entry);
            claims.Sort((a, b) => b.Sequence.CompareTo(a.Sequence));
            if (slot == IngameMenuSlot.Right) claims.Add(vanilla[(int)row]);
            Entry current = layout.Slots[(int)row, (int)slot];
            var snapshots = new IngameMenuActionInfo[claims.Count];
            IngameMenuActionInfo effective = null;
            for (int i = 0; i < snapshots.Length; i++)
            {
                Entry entry = claims[i];
                snapshots[i] = new IngameMenuActionInfo(entry.Owner, entry.Id, entry.Vanilla, entry.Known, entry.State,
                    ReferenceEquals(entry, current), IsSuppressed(entry));
                if (ReferenceEquals(entry, current)) effective = snapshots[i];
            }
            return new IngameMenuSlotState(slot, effective, snapshots);
        }
    }
}
