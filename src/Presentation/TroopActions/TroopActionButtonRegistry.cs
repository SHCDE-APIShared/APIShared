using System;
using System.Collections.Generic;
using APIShared.Events;

namespace APIShared.Presentation
{
    // Immutable invocation views isolate a render from reentrant setters in consumer callbacks.
    internal sealed class TroopActionButtonRegistry
    {
        private readonly object gate = new object();
        private readonly List<Entry> entries = new List<Entry>();
        private readonly Action changed;
        private State[] snapshot;
        private long layoutRevision;
        internal TroopActionButtonRegistry(Action changed) { this.changed = changed; }
        internal bool TryRegister(string owner, TroopActionButtonDefinition definition,
            out ITroopActionButtonRegistration registration, out string reason)
        {
            registration = null;
            if (string.IsNullOrWhiteSpace(owner) || definition == null || string.IsNullOrWhiteSpace(definition.ButtonId) ||
                definition.Command == null || string.IsNullOrWhiteSpace(definition.Tooltip) ||
                definition.ButtonFactory == null || definition.Priority < 0)
            { reason = "Owner, ID, command, tooltip, button factory and nonnegative priority are required."; return false; }
            lock (gate)
            {
                foreach (Entry existing in entries)
                    if (existing.Owner == owner && existing.Definition.ButtonId == definition.ButtonId)
                    { reason = "This owner already registered that button ID."; return false; }
                var entry = new Entry(this, owner, definition);
                entries.Add(entry);
                entries.Sort((a, b) => RegistrationKey.Compare(Entry.Key(a), Entry.Key(b)));
                layoutRevision++;
                snapshot = null; registration = entry;
            }
            reason = string.Empty;
            changed?.Invoke();
            return true;
        }
        internal State[] Capture() => Capture(out _);
        internal State[] Capture(out long revision)
        {
            lock (gate)
            {
                revision = layoutRevision;
                if (snapshot != null) return snapshot;
                var result = new State[entries.Count];
                for (int i = 0; i < result.Length; i++) result[i] = new State(entries[i]);
                return snapshot = result;
            }
        }
        internal bool IsExecutable(Entry entry) { lock (gate) return entry.Visible && entry.Enabled; }
        // Cosmetic changes and another action's availability do not move page entries.
        // Recheck this action's live availability together with the rendered membership.
        internal bool IsExecutable(Entry entry, long revision)
        { lock (gate) return revision == layoutRevision && entry.Visible && entry.Enabled; }
        internal sealed class State
        {
            internal readonly Entry Entry;
            internal readonly bool Visible, Enabled;
            internal readonly string Tooltip;
            internal readonly int Version;
            internal State(Entry entry)
            { Entry = entry; Visible = entry.Visible; Enabled = entry.Enabled; Tooltip = entry.Tooltip; Version = entry.Version; }
        }
        internal sealed class Entry : ITroopActionButtonRegistration
        {
            private readonly TroopActionButtonRegistry registry;
            internal readonly string Owner;
            internal readonly TroopActionButtonDefinition Definition;
            internal bool Visible, Enabled = true;
            internal string Tooltip;
            internal int Version;
            internal Entry(TroopActionButtonRegistry registry, string owner, TroopActionButtonDefinition definition)
            { this.registry = registry; Owner = owner; Definition = definition; Tooltip = definition.Tooltip; }
            // Priority is the registration order and the button ID is the registration ID.
            internal static readonly Func<Entry, RegistrationKey> Key =
                entry => new RegistrationKey(entry.Owner, entry.Definition.ButtonId, entry.Definition.Priority);
            public void SetVisible(bool value)
            {
                lock (registry.gate) { if (Visible == value) return; Visible = value; registry.layoutRevision++; registry.snapshot = null; }
                registry.changed?.Invoke();
            }
            public void SetEnabled(bool value)
            {
                lock (registry.gate) { if (Enabled == value) return; Enabled = value; registry.snapshot = null; }
                registry.changed?.Invoke();
            }
            public void SetTooltip(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Nonempty tooltip required.", nameof(value));
                lock (registry.gate) { if (Tooltip == value) return; Tooltip = value; registry.snapshot = null; }
                registry.changed?.Invoke();
            }
            public void RequestButtonRefresh()
            {
                lock (registry.gate) { Version++; registry.layoutRevision++; registry.snapshot = null; }
                registry.changed?.Invoke();
            }
        }
    }
}
