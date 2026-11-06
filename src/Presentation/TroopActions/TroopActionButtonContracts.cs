using System;
using System.Windows.Input;
using Noesis;

namespace APIShared.Presentation
{
    /// <summary>A process-lived action in Vanilla's troop-action field, with automatic HUD pages.</summary>
    public sealed class TroopActionButtonDefinition
    {
        /// <summary>Creates an action. Factories and callbacks run on Unity outside registry locks.
        /// Buttons start hidden; explicitly publish visibility after initializing consumer state.</summary>
        public TroopActionButtonDefinition(string buttonId, ICommand command, string tooltip,
            Func<UnitHudActionButtonContext, Button> buttonFactory, int priority = 0,
            Action<bool> contextChanged = null, Action<bool> hoverChanged = null)
        {
            ButtonId = buttonId; Command = command; Tooltip = tooltip; ButtonFactory = buttonFactory;
            Priority = priority; ContextChanged = contextChanged; HoverChanged = hoverChanged;
        }
        /// <summary>Stable owner-local ID. Duplicate owner/ID registrations are rejected.</summary>
        public string ButtonId { get; }
        /// <summary>Reauthorized against current HUD, selection and CanExecute immediately before execution.
        /// The parameter is the actual Button. Normal return acknowledges invocation, not native execution.</summary>
        public ICommand Command { get; }
        /// <summary>Nonempty localized Vanilla rollover text, used when HoverChanged is absent.</summary>
        public string Tooltip { get; }
        /// <summary>Returns a fresh unattached Button fitting 36 by 37 HUD units, with its own style/content.
        /// APIShared owns Command, Visibility and IsEnabled. Recreated on explicit refresh, HUD or player/colour change.
        /// Factory failure hides only this entry until refresh or context change. Do not retain the supplied HUD context.</summary>
        public Func<UnitHudActionButtonContext, Button> ButtonFactory { get; }
        /// <summary>Nonnegative ascending priority, followed by ordinal owner GUID and button ID.</summary>
        public int Priority { get; }
        /// <summary>Optional own-selection availability notification, independent of placement and the current HUD page.</summary>
        public Action<bool> ContextChanged { get; }
        /// <summary>Optional replacement for standard rollover handling, including disabled buttons.
        /// True shows consumer-owned Vanilla rollover; false must hide it. Hiding/resetting a hovered entry reports false.</summary>
        public Action<bool> HoverChanged { get; }
    }

    /// <summary>Thread-safe logical control of a permanent troop-action registration; no hook teardown.</summary>
    public interface ITroopActionButtonRegistration
    {
        /// <summary>Publishes visibility; defaults to false. Hidden entries occupy no slots.</summary>
        void SetVisible(bool visible);
        /// <summary>Publishes command availability; defaults to true. Disabled buttons retain hover text.</summary>
        void SetEnabled(bool enabled);
        /// <summary>Updates nonempty localized rollover text. Empty strings throw ArgumentException.</summary>
        void SetTooltip(string tooltip);
        /// <summary>Requests a fresh Button on Unity, also retrying a failed factory.</summary>
        void RequestButtonRefresh();
    }
}
