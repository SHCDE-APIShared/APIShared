using System;
using APIShared.Commands;
using APIShared.Presentation;

namespace ThirdPartyMod
{
    internal static class InterceptionExample
    {
        // A consumer owns activation/settings. Zero keeps this illustrative policy inactive.
        internal static int MaximumConcreteRequest = 0;

        // Call once during Unity startup only when the consumer supplies an owned policy.
        // ownsRequest must filter both logical activation and the current panel/button context.
        internal static void RegisterMarketReplacement(Func<PresentationPreEventArgs, bool> ownsRequest,
            Action<object> replace, Action<string> log)
        {
            if (!PresentationEvents.TryRegister(PresentationOperation.MarketBuySell, ExamplePlugin.Guid,
                "OwnedMarketAction", args => {
                    if (args.SkipOriginalFunction || !ownsRequest(args)) return;
                    // Defer effects: another Pre can veto; replace receives the final Parameter.
                    // Do not retain args.ViewModel or put consumer IDs into native goods arrays.
                    args.Replacement = replace;
                }, null, out string reason)) log(reason);
        }

        internal static void Register(Action<string> log)
        {
            if (!GameActionEvents.TryRegisterWithFinalValidation(ExamplePlugin.Guid, "RecruitmentCeiling", args => {
                int maximum = MaximumConcreteRequest;
                if (maximum <= 0 || args.SkipOriginalFunction || args.Command != Enums.GameActionCommand.MakeTroop) return;
                // This demonstrates a ceiling on the sent request, not an affordability calculation.
                if (args.StructureId > maximum) args.StructureId = maximum;
                args.State = maximum;
            }, args => {
                // Only this registration receives its own Pre State. Post does not confirm unit creation.
                if (args.Command == Enums.GameActionCommand.MakeTroop && args.WasSkipped)
                    log("Recruitment request vetoed by a participant.");
            }, args => !(args.State is int maximum) || args.StructureId <= maximum, out string reason)) log(reason);

            if (!PresentationEvents.TryRegister(PresentationOperation.RecruitmentEnter, ExamplePlugin.Guid,
                "RecruitmentTooltip", null, args => {
                    if (!args.OriginalCompleted) return;
                    // Extend only an explicitly owned text/control here. Do not retain args.ViewModel.
                }, out reason)) log(reason);
        }
    }
}
