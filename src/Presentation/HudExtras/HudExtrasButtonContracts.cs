using Noesis;
using System;
using System.Windows.Input;

namespace APIShared
{
    /// <summary>Unity-thread context for a fresh side-HUD button or tooltip.</summary>
    public sealed class HudExtrasButtonContext
    {
        internal HudExtrasButtonContext(FrameworkElement hud) { Hud = hud; }
        /// <summary>The current host and its inherited Vanilla resources; do not retain it across HUD recreation.</summary>
        public FrameworkElement Hud { get; }
    }

    /// <summary>A permanent contribution to the bottom-to-top side-HUD list.</summary>
    public sealed class HudExtrasButtonDefinition
    {
        /// <summary>Creates a definition. Factories run on the Unity thread outside registry locks.</summary>
        public HudExtrasButtonDefinition(string buttonId, ICommand command, string tooltip,
            Func<HudExtrasButtonContext, Button> buttonFactory, int order = 0,
            Func<HudExtrasButtonContext, ToolTip> tooltipFactory = null)
        {
            ButtonId = buttonId; Command = command; Tooltip = tooltip ?? string.Empty;
            ButtonFactory = buttonFactory; Order = order; TooltipFactory = tooltipFactory;
        }
        /// <summary>Stable owner-local ID; duplicates are rejected.</summary>
        public string ButtonId { get; }
        /// <summary>Command rechecked before execution; its parameter is the actual Button.</summary>
        public ICommand Command { get; }
        /// <summary>Localized popup text. Empty text suppresses the popup.</summary>
        public string Tooltip { get; }
        /// <summary>Creates a fresh unattached Button. APIShared owns its size, placement, command and availability.</summary>
        public Func<HudExtrasButtonContext, Button> ButtonFactory { get; }
        /// <summary>Ascending order, followed by ordinal mod GUID and button ID. Defaults to zero.</summary>
        public int Order { get; }
        /// <summary>Optional fresh tooltip with an explicit Style or Template; Content is set to the current tooltip text. Null uses the modoptions style.</summary>
        public Func<HudExtrasButtonContext, ToolTip> TooltipFactory { get; }
    }

    /// <summary>Logical control of a process-lived registration; no hook disposal is exposed.</summary>
    public interface IHudExtrasButtonRegistration
    {
        /// <summary>Hidden entries occupy no slots. Defaults to true.</summary>
        void SetVisible(bool visible);
        /// <summary>Sets command availability. Defaults to true.</summary>
        void SetEnabled(bool enabled);
        /// <summary>Updates localized popup text; null or empty suppresses the popup.</summary>
        void SetTooltip(string tooltip);
        /// <summary>Recreates the button and tooltip, including retrying failed factories.</summary>
        void RequestContentRefresh();
    }

    /// <summary>Owner-bound access to shared side-HUD buttons, independent of troop selection.</summary>
    /// <remarks>Visual factories, CanExecute and Execute run on the Unity thread outside registry locks. Failed entries are isolated and can be retried by refresh. Handles update logical state; no unregistration, replay or hook teardown is provided. Your command owns multiplayer/gameplay authorization.</remarks>
    public interface IHudExtrasButtonsCapability
    {
        /// <summary>Registers a process-lived owner-local button without coordinates. Returns false with null registration and a ValidationFailed diagnostic for missing ID/command/factory or a duplicate owner-local ID. At most five visible entries appear per page, bottom to top.</summary>
        bool TryRegisterButton(HudExtrasButtonDefinition definition, out IHudExtrasButtonRegistration registration,
            out NativeCapabilityDiagnostic diagnostic);
        /// <summary>Logically activates all this owner's entries without uninstalling callbacks. Defaults to true.</summary>
        void SetOwnerActive(bool active);
    }
}
