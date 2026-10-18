using System;
using System.Collections.Generic;
using APIShared;
using Noesis;
using SHCDESE.Interop;

namespace ThirdPartyMod
{
    // One text row in the selected building's panel, for buildings the local player owns. The update rewrites
    // the text only when another building is selected, so a steady refresh allocates nothing.
    // Then a custom barracks variant built on the native stone barracks: its own recruitment panel for the
    // buildings this mod marked as the variant. Other mods can register their own stone-barracks variants.
    internal static class BuildingPanelExample
    {
        // Building IDs this mod made its variant; a real mod keeps this with its save data.
        private static readonly HashSet<int> GuardHouses = new HashSet<int>();

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

            var variant = new BuildingPanelRecruitmentAreaDefinition("guard-house", new[] { eStructs.STRUCT_BARRACKS_STONE },
                context => new TextBlock { Text = "Guard house recruitment", FontSize = 16 },
                selection => selection.IsLocallyOwned && GuardHouses.Contains(selection.BuildingId));
            if (!panel.TryRegisterRecruitmentArea(variant, out _, out diagnostic)) log(diagnostic.Reason);
        }
    }
}
