using APIShared.Presentation;
using System;
using System.Collections.Generic;

namespace ThirdPartyMod
{
    // No native reads, workspace links, private reflection or known consumer GUIDs.
    internal static class BuildingTooltipExample
    {
        private static readonly IReadOnlyList<BuildingTooltipLine> message = Array.AsReadOnly(new[] {
            new BuildingTooltipLine(new BuildingTooltipText("Example contribution", 0xFFEFF3C6))
        });
        internal static void Register(Action<string> log)
        {
            if (!BuildingTooltipPresentation.TryRegister(ExamplePlugin.Guid, "BuildingInformation",
                context => context.BuildingType == 24 ? message : Array.Empty<BuildingTooltipLine>(), out string reason))
                log("Building tooltip registration failed: " + reason);
        }
    }
}
