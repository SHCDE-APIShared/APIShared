using Noesis;
using System;
using System.Windows.Input;

namespace APIShared
{
    /// <summary>Unity-thread context for creating content in the locally controlled troop HUD.</summary>
    public sealed class UnitHudActionButtonContext
    {
        internal UnitHudActionButtonContext(FrameworkElement hud, int playerId, int colour)
        { Hud = hud; PlayerId = playerId; PlayerColour = colour; }
        /// <summary>The current HUD; use its resources without retaining it beyond this content instance.</summary>
        public FrameworkElement Hud { get; }
        /// <summary>The one-based locally controlled player ID.</summary>
        public int PlayerId { get; }
        /// <summary>The Vanilla colour index after multiplayer remapping (1..8).</summary>
        public int PlayerColour { get; }
    }

    /// <summary>A process-lived contribution to the own-troop action strip.</summary>
    public sealed class UnitHudActionButtonDefinition
    {
        /// <summary>Creates a contribution. Factory and optional callbacks run on the Unity thread, outside registration locks.</summary>
        public UnitHudActionButtonDefinition(string buttonId, ICommand command, string tooltip,
            Func<UnitHudActionButtonContext, FrameworkElement> contentFactory, int order = 0,
            Action<bool> contextChanged = null, Action<bool> hoverChanged = null)
        {
            ButtonId = buttonId; Command = command; Tooltip = tooltip; ContentFactory = contentFactory;
            Order = order; ContextChanged = contextChanged; HoverChanged = hoverChanged;
        }
        /// <summary>Stable owner-local ID; duplicates are rejected.</summary>
        public string ButtonId { get; }
        /// <summary>Command rechecked against the current own selection immediately before execution.</summary>
        public ICommand Command { get; }
        /// <summary>Nonempty localized tooltip, displayed for 60000 ms.</summary>
        public string Tooltip { get; }
        /// <summary>Creates fresh unattached content, at most 35 by 35 HUD units. Exceptions isolate this entry.</summary>
        public Func<UnitHudActionButtonContext, FrameworkElement> ContentFactory { get; }
        /// <summary>Ascending sort order, followed by ordinal owner GUID and button ID.</summary>
        public int Order { get; }
        /// <summary>Optional notification when a visible, active contribution gains or loses the own-selection context; page changes do not change context.</summary>
        public Action<bool> ContextChanged { get; }
        /// <summary>Optional mouse-enter/leave notification; hiding a hovered button also reports false.</summary>
        public Action<bool> HoverChanged { get; }
    }

    /// <summary>Logical control of a permanent registration; this handle does not own or dispose hooks.</summary>
    public interface IUnitHudActionButtonRegistration
    {
        /// <summary>Sets visibility. Hidden entries occupy no slots. Defaults to true.</summary>
        void SetVisible(bool visible);
        /// <summary>Sets command availability. Defaults to true.</summary>
        void SetEnabled(bool enabled);
        /// <summary>Updates the localized tooltip. Empty strings are rejected.</summary>
        void SetTooltip(string tooltip);
        /// <summary>Recreates content on the Unity thread, including retrying a failed factory.</summary>
        void RequestContentRefresh();
    }

    /// <summary>Optional extension implemented by the owner-bound Unit HUD capability; older providers may not implement it.</summary>
    public interface IUnitHudActionButtonsCapability
    {
        /// <summary>Registers a permanent owner-local own-troop action button without specifying coordinates.</summary>
        bool TryRegisterActionButton(UnitHudActionButtonDefinition definition,
            out IUnitHudActionButtonRegistration registration, out NativeCapabilityDiagnostic diagnostic);
    }
}
