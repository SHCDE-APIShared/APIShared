using System;
using APIShared.Commands;
using APIShared.Presentation;

namespace ThirdPartyMod
{
    internal static class InterceptionExample
    {
        // A consumer owns activation/settings. Zero keeps this illustrative policy inactive.
        internal static int MaximumConcreteRequest = 0;

        internal static void Register(Action<string> log)
        {
            if (!GameActionEvents.TryRegister(ExamplePlugin.Guid, "RecruitmentCeiling", args => {
                int maximum = MaximumConcreteRequest;
                if (maximum <= 0 || args.SkipOriginalFunction || args.Command != Enums.GameActionCommand.MakeTroop) return;
                // This demonstrates a ceiling on the sent request, not an affordability calculation.
                if (args.StructureId > maximum) args.StructureId = maximum;
            }, args => {
                // Only this registration receives its own Pre State. Post does not confirm unit creation.
                if (args.Command == Enums.GameActionCommand.MakeTroop && args.WasSkipped)
                    log("Recruitment request vetoed by a participant.");
            }, out string reason)) log(reason);

            if (!PresentationEvents.TryRegister(PresentationOperation.RecruitmentEnter, ExamplePlugin.Guid,
                "RecruitmentTooltip", null, args => {
                    if (!args.OriginalCompleted) return;
                    // Extend only an explicitly owned text/control here. Do not retain args.ViewModel.
                }, out reason)) log(reason);
        }
    }
}
