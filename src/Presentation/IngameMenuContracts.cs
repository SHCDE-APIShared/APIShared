using System;
using System.Collections.Generic;

namespace APIShared.Presentation
{
    /// <summary>Stable Vanilla row identities, independent of mode-specific visibility or visual indices.</summary>
    public enum IngameMenuRow
    {
        /// <summary>Save game or map.</summary>
        Save,
        /// <summary>Load game or map.</summary>
        Load,
        /// <summary>Map editor Workshop upload.</summary>
        Workshop,
        /// <summary>Game options.</summary>
        Options,
        /// <summary>Help.</summary>
        Help,
        /// <summary>Restart the mission.</summary>
        RestartMission,
        /// <summary>Leave the mission or editor.</summary>
        QuitMission,
        /// <summary>Exit the application.</summary>
        ExitStronghold,
        /// <summary>Resume play.</summary>
        Resume
    }

    /// <summary>Logical positions; a single visible occupant spans the normal full button width.</summary>
    public enum IngameMenuSlot
    {
        /// <summary>Additional position to the left of Vanilla.</summary>
        Left,
        /// <summary>Vanilla's original position.</summary>
        Right
    }

    /// <summary>Immutable placement. The parameterless constructor selects only the additional-actions page.</summary>
    public sealed class IngameMenuActionOptions
    {
        /// <summary>Registers for the additional-actions page without requesting a fixed position.</summary>
        public IngameMenuActionOptions() { }
        /// <summary>Requests a fixed position. Replacing a mod claim and replacing Vanilla are separate explicit permissions.
        /// Even hidden registered mod claims require replaceExisting; a query is not a reservation.</summary>
        public IngameMenuActionOptions(IngameMenuRow row, IngameMenuSlot slot,
            bool replaceExisting = false, bool replaceVanilla = false)
        { Row = row; Slot = slot; ReplaceExisting = replaceExisting; ReplaceVanilla = replaceVanilla; }
        /// <summary>Requested row, or null for the additional-actions page.</summary>
        public IngameMenuRow? Row { get; }
        /// <summary>Requested position when Row is present.</summary>
        public IngameMenuSlot Slot { get; }
        /// <summary>Allows a later accepted claim to take precedence over previously registered mod claims.</summary>
        public bool ReplaceExisting { get; }
        /// <summary>Explicitly allows replacing the original right-hand Vanilla action.</summary>
        public bool ReplaceVanilla { get; }
    }

    /// <summary>Localized presentation evaluated on Unity at opening, invalidation and command validation; null also hides an action.</summary>
    public sealed class IngameMenuActionState
    {
        /// <summary>Creates a presentation without a tooltip; hidden entries occupy neither a slot nor an overflow position.</summary>
        public IngameMenuActionState(string text, bool visible, bool enabled)
            : this(text, visible, enabled, null) { }
        /// <summary>Creates a presentation with an explicitly supplied localized tooltip.
        /// Null, empty or whitespace-only tooltip text disables the popup. Labels and owner IDs are never substituted.</summary>
        public IngameMenuActionState(string text, bool visible, bool enabled, string tooltip)
        {
            Text = text ?? string.Empty; Visible = visible; Enabled = enabled;
            Tooltip = string.IsNullOrWhiteSpace(tooltip) ? null : tooltip;
        }
        /// <summary>Localized button label.</summary>
        public string Text { get; }
        /// <summary>Explicit localized tooltip, or null for no popup. Updated through the ordinary state provider/invalidation path.</summary>
        public string Tooltip { get; }
        /// <summary>Whether this action participates in layout.</summary>
        public bool Visible { get; }
        /// <summary>Whether the visible action can execute. Disabled visible actions still occupy their slot.</summary>
        public bool Enabled { get; }
    }

    /// <summary>Process-lifetime action handle. It never uninstalls hooks or unregisters a claim.</summary>
    public sealed class IngameMenuActionRegistration
    {
        private readonly Action invalidate;
        internal IngameMenuActionRegistration(Action invalidate) { this.invalidate = invalidate; }
        /// <summary>Requests reevaluation on Unity, marshalled from other threads. Closed menus defer evaluation until opening.
        /// Notify only after a relevant change, never from a periodic tick/frame callback. Repeated pending requests coalesce.</summary>
        public void Invalidate() => invalidate();
    }

    /// <summary>Immutable last-observed action information; queries never invoke consumer code.</summary>
    public sealed class IngameMenuActionInfo
    {
        internal IngameMenuActionInfo(string owner, string id, bool vanilla, bool known,
            IngameMenuActionState state, bool effective)
        { OwnerGuid = owner; Id = id; IsVanilla = vanilla; HasState = known; State = state; IsEffective = effective; }
        /// <summary>Owning mod GUID, or the reserved APIShared.Vanilla identity.</summary>
        public string OwnerGuid { get; }
        /// <summary>Owner-local action identity.</summary>
        public string Id { get; }
        /// <summary>Whether this is the original game action.</summary>
        public bool IsVanilla { get; }
        /// <summary>Whether a state has been observed. Before the first opening it may be unknown.</summary>
        public bool HasState { get; }
        /// <summary>Last presentation, or null when unknown or explicitly empty.</summary>
        public IngameMenuActionState State { get; }
        /// <summary>Whether this claim currently wins its requested slot.</summary>
        public bool IsEffective { get; }
    }

    /// <summary>Immutable slot snapshot with registered claims in precedence order, including hidden claims.</summary>
    public sealed class IngameMenuSlotState
    {
        internal IngameMenuSlotState(IngameMenuSlot slot, IngameMenuActionInfo current, IngameMenuActionInfo[] claims)
        { Slot = slot; Current = current; Claims = Array.AsReadOnly(claims); }
        /// <summary>Logical position.</summary>
        public IngameMenuSlot Slot { get; }
        /// <summary>Effective visible occupant, or null for an empty/unknown position.</summary>
        public IngameMenuActionInfo Current { get; }
        /// <summary>Accepted claims, including the Vanilla baseline on Right and legacy candidates on QuitMission/Left.</summary>
        public IReadOnlyList<IngameMenuActionInfo> Claims { get; }
    }

    /// <summary>Immutable occupancy of one stable row.</summary>
    public sealed class IngameMenuRowState
    {
        internal IngameMenuRowState(IngameMenuRow row, IngameMenuSlotState left, IngameMenuSlotState right)
        { Row = row; Left = left; Right = right; }
        /// <summary>Stable row identity.</summary>
        public IngameMenuRow Row { get; }
        /// <summary>Left position.</summary>
        public IngameMenuSlotState Left { get; }
        /// <summary>Right position and Vanilla fallback.</summary>
        public IngameMenuSlotState Right { get; }
    }
}
