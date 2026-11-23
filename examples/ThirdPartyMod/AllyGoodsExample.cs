using System;
using APIShared.Presentation;
using Noesis;

namespace ThirdPartyMod
{
    // Any independent plugin GUID may register a section. APIShared owns layout;
    // this example only reports the selected amount, without issuing a game command.
    internal static class AllyGoodsExample
    {
        internal static void Register(string owner, Action<string> log)
        {
            // Presentation state belongs to the consumer, outside controls that may
            // unload when the HUD is rebuilt or footer overflow reparents a launcher.
            bool expanded = false;
            TextBlock label = null;
            Button launcher = null;
            Action<AllyGoodsPanelContext> update = selected => {
                if (label != null) label.Text = SelectionText(selected);
            };
            if (!AllyGoodsPanelExtensions.TryRegisterSection(owner, "quantity-details", context =>
            {
                label = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
                update(context.Selection); return label;
            }, AllyGoodsSectionPlacement.GoodsDialogBottom, out var section, out string reason))
            { log(reason); return; }
            section.SetVisible(false);

            Action updateLauncher = () => {
                bool available = section.TryGetLayoutAvailability(out _);
                if (launcher == null) return;
                launcher.IsEnabled = available;
                launcher.Content = expanded ? "Hide details" : "Details";
            };
            // Notifications do not replay. Read once after subscribing as well.
            section.LayoutAvailabilityChanged += updateLauncher;
            updateLauncher();
            if (!AllyGoodsPanelExtensions.TryRegisterFooterControl(owner, "quantity-launcher", context =>
            {
                launcher = new Button { FontSize = 12, Height = 18, MinHeight = 0, MinWidth = 0,
                    Padding = new Thickness(4, 0, 4, 0) };
                launcher.Click += (sender, args) => {
                    // Re-read both context and availability at command time. This is
                    // only a local disclosure; it never submits a gameplay command.
                    if (!AllyGoodsPanelExtensions.TryGetContext(out var current) || !current.IsSendMode ||
                        !section.TryGetLayoutAvailability(out _)) return;
                    expanded = !expanded; section.SetVisible(expanded);
                    update(current); updateLauncher();
                };
                AttachLauncherTooltip(launcher);
                updateLauncher(); return launcher;
            }, out _, out reason)) log(reason);
            if (!AllyGoodsPanelExtensions.TrySubscribeContext(owner, "quantity-details-context", update, out reason)) log(reason);
            ObserveConfirmation(owner, log);
        }

        // Alternative for existing consumers: the unchanged overload places the
        // section below the entire original menu root, including a taller foreign root.
        internal static void RegisterLegacy(string owner, Action<string> log)
        {
            TextBlock label = null;
            Action<AllyGoodsPanelContext> update = selected => {
                if (label != null) label.Text = SelectionText(selected);
            };
            if (!AllyGoodsPanelExtensions.TryRegisterSection(owner, "quantity-example", context =>
            {
                label = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
                update(context.Selection); return label;
            }, out _, out string reason)) log(reason);
            if (!AllyGoodsPanelExtensions.TrySubscribeContext(owner, "quantity-example-context", update, out reason)) log(reason);
        }

        private static string SelectionText(AllyGoodsPanelContext selected) => selected.HasSelection ?
            "Selected ally " + selected.RecipientPlayerId + ": " + selected.Amount + " " + selected.Good +
            ". Selection is not a delivery acknowledgement." : "Select an ally, a good and a quantity.";

        private static void ObserveConfirmation(string owner, Action<string> log)
        {
            if (!AllyPanelButtonEvents.TryRegister(owner, "observe-goods-confirmation", null, completed =>
            {
                if (completed.Button == "ConfirmGoods" && completed.OriginalCompleted)
                    log("The original panel command completed. Check transfer events for delivery.");
            }, out string reason)) log(reason);
        }

        private static void AttachLauncherTooltip(Button launcher)
        {
            // Example English text: real consumers supply their own localized copy.
            var style = GUI.ParseXaml(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ToolTip}'>
<Setter Property='Background' Value='#FF1D1710'/><Setter Property='Foreground' Value='White'/><Setter Property='BorderBrush' Value='#FFF2D48A'/><Setter Property='BorderThickness' Value='1'/><Setter Property='FontSize' Value='14'/><Setter Property='MaxWidth' Value='420'/>
<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ToolTip}'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}' MaxWidth='{TemplateBinding MaxWidth}'><TextBlock Text='{TemplateBinding Content}' Foreground='{TemplateBinding Foreground}' FontSize='{TemplateBinding FontSize}' TextWrapping='Wrap'/></Border></ControlTemplate></Setter.Value></Setter></Style>") as Style;
            var tip = new ToolTip { Style = style,
                Content = "Show or hide selection details. Unavailable when the dialog layout cannot host this section.",
                IsHitTestVisible = false };
            APIShared.ModSettings.SharedToolTipSizing.SetEnabled(tip, true);
            APIShared.ModSettings.SharedToolTipSizing.SetReferenceElement(tip, launcher);
            APIShared.ModSettings.SharedToolTipSizing.SetReferenceFontSize(tip, 12);
            ToolTipService.SetToolTip(launcher, tip);
            ToolTipService.SetShowDuration(launcher, 60000); ToolTipService.SetShowOnDisabled(launcher, true);
        }
    }
}
