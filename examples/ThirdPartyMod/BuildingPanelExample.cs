using System;
using APIShared;
using Noesis;

namespace ThirdPartyMod
{
    // One text row in the selected building's panel, for buildings the local player owns. The update rewrites
    // the text only when another building is selected, so a steady refresh allocates nothing.
    internal static class BuildingPanelExample
    {
        internal static void Register(ModApiClient client, Action<string> log)
        {
            if (!client.TryGetBuildingPanelExtensions(out var panel, out var diagnostic)) { log(diagnostic.Reason); return; }
            var definition = new BuildingPanelExtensionDefinition("example-row",
                context => new TextBlock { FontSize = 14 },
                selection => selection.IsLocallyOwned,
                (selection, element) =>
                {
                    if (element.Tag is int shown && shown == selection.GlobalId) return;
                    element.Tag = selection.GlobalId;
                    ((TextBlock)element).Text = "Example row for building " + selection.BuildingId;
                },
                order: 100);
            if (!panel.TryRegisterExtension(definition, out _, out diagnostic)) log(diagnostic.Reason);
        }
    }
}
