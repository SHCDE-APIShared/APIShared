using Noesis;
using System.Runtime.CompilerServices;

namespace APIShared.ModSettings
{
    /// <summary>Opt-in shared popup sizing. Applies the established resolution presets on each opening,
    /// taking exclusive sizing ownership from the SE attached scaler for this tooltip only. UI thread only.</summary>
    public static class SharedToolTipSizing
    {
        private sealed class State { internal bool PreviousScaler; }
        private static readonly ConditionalWeakTable<ToolTip, State> states = new ConditionalWeakTable<ToolTip, State>();
        /// <summary>Attached opt-in; does not change unrelated or Vanilla tooltips.</summary>
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(SharedToolTipSizing), new PropertyMetadata(false, Changed));
        /// <summary>Reads the opt-in state.</summary>
        public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
        /// <summary>Changes sizing ownership for this popup.</summary>
        public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
        private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
        {
            if (!(value is ToolTip tip)) return;
            tip.Opened -= Opened;
            if ((bool)args.NewValue)
            {
                var state = states.GetValue(tip, _ => new State());
                state.PreviousScaler = SHCDESE.UI.ToolTipResolutionScale.GetEnabled(tip);
                SHCDESE.UI.ToolTipResolutionScale.SetEnabled(tip, false);
                tip.Opened += Opened;
                Apply(tip);
            }
            else if (states.TryGetValue(tip, out var previous))
            {
                SHCDESE.UI.ToolTipResolutionScale.SetEnabled(tip, previous.PreviousScaler);
                states.Remove(tip);
            }
        }
        private static void Opened(object sender, RoutedEventArgs args)
        {
            var tip = (ToolTip)sender;
            // A style may have applied SE's attached value after our own. Removing its handler
            // here also prevents a later opening from restoring the smaller SE preset.
            SHCDESE.UI.ToolTipResolutionScale.SetEnabled(tip, false);
            Apply(tip);
        }
        internal static void Apply(ToolTip tip)
        {
            tip.FontSize = ToolTipPresentation.AutomaticFontSize;
            tip.MaxWidth = ToolTipPresentation.AutomaticMaximumWidth;
            tip.Padding = Internal.ToolTipPresentation.AutomaticPadding;
            tip.BorderThickness = Internal.ToolTipPresentation.AutomaticBorderThickness;
        }
    }
}
