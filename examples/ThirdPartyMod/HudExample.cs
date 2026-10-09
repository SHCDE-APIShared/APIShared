using System;
using APIShared;
using Noesis;
using System.Windows.Input;

namespace ThirdPartyMod
{
    internal static class HudExample
    {
        // Load a mod-owned image through your normal asset pipeline on the Unity thread.
        // Null deliberately preserves the current image until an asset is assigned.
        internal static ImageSource Icon;

        internal static void RegisterSideButtons(ModApiClient client, Action<string> log)
        {
            if (!client.TryGetHudExtrasButtons(out var buttons, out var diagnostic)) { log(diagnostic.Reason); return; }
            var definition = new HudExtrasButtonDefinition("example-side-action", new ExampleCommand(log), "Example side action", context => {
                var button = new Button { Style = context.Hud.TryFindResource("BTN_Building") as Style };
                var normal = context.Hud.TryFindResource("UI-Buttons L009") as ImageSource;
                var highlight = context.Hud.TryFindResource("UI-Buttons L010") as ImageSource;
                CrusaderDE.PropEx.SetSprite1(button, normal); CrusaderDE.PropEx.SetSprite2(button, highlight);
                CrusaderDE.PropEx.SetSprite3(button, highlight); CrusaderDE.PropEx.SetSprite4(button, normal);
                return button;
            }, order: 100);
            if (!buttons.TryRegisterButton(definition, out _, out diagnostic)) log(diagnostic.Reason);
        }

        internal static void Register(IUnitHudPresentationCapability hud, Action<string> log)
        {
            var definition = new UnitHudImageOverrideDefinition("swordsman-icon", UnitHudImageSlot.UIButtonsK007);
            if (!hud.TryRegisterImageOverride(definition, ResolveIcon, out var diagnostic))
                log(diagnostic.Reason);
            if (hud is IUnitHudActionButtonsCapability buttons)
            {
                var action = new UnitHudActionButtonDefinition("example-action", new ExampleCommand(log),
                    "Example troop action", context => new TextBlock {
                        Text = "+", FontSize = 24, Width = 35, Height = 35,
                        TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }, order: 100);
                if (!buttons.TryRegisterActionButton(action, out _, out diagnostic)) log(diagnostic.Reason);
            }
        }

        private static ImageSource ResolveIcon(UnitHudImageOverrideContext context) => Icon;

        private sealed class ExampleCommand : ICommand
        {
            private readonly Action<string> log;
            internal ExampleCommand(Action<string> log) { this.log = log; }
            public event System.EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => log("Example HUD action invoked.");
        }
    }
}
