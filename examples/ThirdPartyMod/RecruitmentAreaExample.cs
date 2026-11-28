using APIShared.Presentation;
using Noesis;
using SHCDESE.Interop;
using System;
using System.Collections.Generic;
using System.Text;

namespace ThirdPartyMod
{
    // A building variant on the native stone barracks with its own recruitment area. Opt-in: call Register
    // from initialized Unity startup. The selector and control are placeholders for the consumer's own logic.
    internal static class RecruitmentAreaExample
    {
        private static readonly HashSet<uint> variantBuildings = new HashSet<uint>();
        private static BuildingPanelRecruitmentAreaRegistration area;

        internal static void Register(Action<string> log)
        {
            var options = new BuildingPanelRecruitmentAreaOptions(ExamplePlugin.Guid, "example-garrison",
                new[] { eStructs.STRUCT_BARRACKS_STONE }, context => new TextBlock { Text = "Example recruits" })
            { Selects = context => variantBuildings.Contains(context.GlobalId) };
            if (!BuildingPanelSections.TryRegisterRecruitmentArea(options, out area, out string reason))
                log("Recruitment area registration failed: " + reason);
        }

        // For the tooltip of the setting that enables the variant: who is shown where several variants
        // select one building. Reading changes nothing; the consumer localizes its own text.
        internal static string DescribeEffectiveOwner()
        {
            if (area == null || !area.TryGetStatus(out var status, out _)) return string.Empty;
            var text = new StringBuilder();
            foreach (BuildingPanelRecruitmentAreaRival rival in status.Rivals)
                text.Append(rival.Precedes ? rival.OwnerGuid + " is shown where both select a building"
                    : "This variant is shown over " + rival.OwnerGuid).Append(rival.ObservedOverlap ? " (seen)." : ".").AppendLine();
            return text.ToString();
        }
    }
}
