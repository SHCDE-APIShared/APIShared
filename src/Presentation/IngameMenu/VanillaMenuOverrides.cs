using System;
using Noesis;

namespace APIShared.Presentation
{
    // Unity-owned overlay on original buttons; deliberately separate from placement claims.
    internal sealed class VanillaMenuOverrides
    {
        internal sealed class Entry
        {
            internal string Owner, Id;
            internal IngameMenuRow Row;
            internal Func<bool?> Enabled;
            internal Action Execute;
            internal bool FailureReported, Executing;
            internal Button AppliedButton;
            internal object PreviousEnabled;
            internal BindingBase PreviousBinding;
            internal bool AppliedEnabled;
        }
        private readonly Entry[] entries = new Entry[9];
        internal bool Evaluating { get; private set; }
        internal bool HasIdentity(string owner, string id)
        {
            foreach (var entry in entries) if (entry != null && entry.Owner == owner && entry.Id == id) return true;
            return false;
        }
        internal bool CanRegister(string owner, string id, IngameMenuRow row, Func<bool?> enabled, Action execute, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(owner) || owner == IngameMenuRegistry.VanillaOwner ||
                string.IsNullOrWhiteSpace(id) || enabled == null || execute == null)
            { reason = "A non-reserved owner GUID, action ID, enabled provider and command are required."; return false; }
            if (Evaluating) { reason = "Register overrides outside an enabled-provider callback."; return false; }
            if (!IngameMenuRegistry.ValidRow(row)) { reason = "Unknown Vanilla menu row."; return false; }
            if (HasIdentity(owner, id)) { reason = "Duplicate menu action: " + owner + "/" + id; return false; }
            var previous = entries[(int)row];
            if (previous != null)
            { reason = "Vanilla menu action already overridden by " + previous.Owner + "/" + previous.Id + "."; return false; }
            return true;
        }
        internal void Register(string owner, string id, IngameMenuRow row, Func<bool?> enabled, Action execute) =>
            entries[(int)row] = new Entry { Owner = owner, Id = id, Row = row, Enabled = enabled, Execute = execute };
        internal Entry Get(int function)
        {
            IngameMenuRow row;
            switch (function)
            {
                case 1: row = IngameMenuRow.Save; break;
                case 2: row = IngameMenuRow.Load; break;
                case 99: row = IngameMenuRow.Workshop; break;
                case 3: row = IngameMenuRow.Options; break;
                case 4: row = IngameMenuRow.Help; break;
                case 5: row = IngameMenuRow.RestartMission; break;
                case 6: row = IngameMenuRow.QuitMission; break;
                case 7: row = IngameMenuRow.ExitStronghold; break;
                case 8: row = IngameMenuRow.Resume; break;
                default: return null;
            }
            return entries[(int)row];
        }
        private bool? Evaluate(Entry entry, Action<Entry, Exception> failure)
        {
            if (Evaluating) return false;
            Evaluating = true;
            try { return entry.Enabled(); }
            catch (Exception error) { ReportFailure(entry, error, failure); return false; }
            finally { Evaluating = false; }
        }
        internal static void ReportFailure(Entry entry, Exception error, Action<Entry, Exception> failure)
        {
            if (entry.FailureReported) return;
            entry.FailureReported = true;
            // A failed diagnostic sink must not stop restoring other original controls.
            try { failure?.Invoke(entry, error); } catch { }
        }
        internal void RestoreApplied(Action<Entry, Exception> failure)
        {
            // Restore before Vanilla Init: some modes intentionally leave an existing
            // button's enabled value alone. Its subsequent mode-specific writes win.
            foreach (var entry in entries)
                if (entry?.AppliedButton != null)
                {
                    try
                    {
                        var button = entry.AppliedButton;
                        if (button.ReadLocalValue(UIElement.IsEnabledProperty) is bool local && local == entry.AppliedEnabled)
                        {
                            if (entry.PreviousBinding != null) BindingOperations.SetBinding(button, UIElement.IsEnabledProperty, entry.PreviousBinding);
                            else if (ReferenceEquals(entry.PreviousEnabled, DependencyProperty.UnsetValue)) button.ClearValue(UIElement.IsEnabledProperty);
                            else button.SetValue(UIElement.IsEnabledProperty, entry.PreviousEnabled);
                        }
                    }
                    catch (Exception error) { ReportFailure(entry, error, failure); }
                    finally { entry.AppliedButton = null; entry.PreviousEnabled = null; entry.PreviousBinding = null; }
                }
        }
        internal void Apply(Button[] buttons, Action<Entry, Exception> failure)
        {
            RestoreApplied(failure);
            foreach (var entry in entries)
                if (entry != null)
                {
                    var enabled = Evaluate(entry, failure);
                    var button = buttons[(int)entry.Row];
                    if (!enabled.HasValue || button == null) continue;
                    // IsEnabled is coerced by disabled ancestors. Ownership and restoration
                    // use the local value, so a hidden/disabled parent cannot leave our true
                    // value behind when the provider returns to Vanilla.
                    entry.PreviousEnabled = button.ReadLocalValue(UIElement.IsEnabledProperty);
                    entry.PreviousBinding = BindingOperations.GetBindingBase(button, UIElement.IsEnabledProperty);
                    entry.AppliedEnabled = enabled.Value;
                    entry.AppliedButton = button; button.IsEnabled = enabled.Value;
                }
        }
        // False means ordinary Vanilla dispatch. Both disabled and failed providers
        // consume the command, and a command failure never falls through to Vanilla.
        internal bool TryExecute(Entry entry, Action<Entry, Exception> failure, Func<bool> canExecute = null)
        {
            if (entry == null) return false;
            if (Evaluating || entry.Executing) return true;
            var enabled = Evaluate(entry, failure);
            if (!enabled.HasValue) return false;
            if (!enabled.Value) return true;
            entry.Executing = true;
            try { if (canExecute == null || canExecute()) entry.Execute(); }
            catch (Exception error) { ReportFailure(entry, error, failure); }
            finally { entry.Executing = false; }
            return true;
        }
    }
}
