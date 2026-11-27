using System;
using System.Collections.Generic;
using APIShared.Presentation;
using Noesis;

namespace ThirdPartyMod
{
    // Arbitrary owner/ID, no native reads, private members or mod-pack dependencies.
    internal static class TooltipExample
    {
        internal static void Register(string ownerGuid, Func<TooltipContext, string> localizedMessage,
            Func<TooltipContext, ImageSource> symbol, Action<string> log)
        {
            if (!TooltipPresentation.TryRegisterText(ownerGuid, "Information",
                TooltipTarget.BuildingCompact | TooltipTarget.BuildingDetailed,
                TooltipPlacement.BeforeMainLine, context => context.IsActive ? localizedMessage(context) : null,
                out string reason)) log(reason);

            IReadOnlyList<TooltipLine> previous = Array.Empty<TooltipLine>();
            string lastText = null; ImageSource lastImage = null;
            if (!TooltipPresentation.TryRegisterContent(ownerGuid, "SymbolInformation", TooltipTarget.Recruitment,
                TooltipPlacement.InlineAfterCosts, context => {
                    if (!context.IsActive) return Array.Empty<TooltipLine>();
                    string text = localizedMessage(context); ImageSource image = symbol(context);
                    if (text != lastText || !ReferenceEquals(image, lastImage))
                    {
                        lastText = text; lastImage = image;
                        previous = new[] { new TooltipLine(new TooltipText(text ?? string.Empty), new TooltipSymbol(image, "symbol")) };
                    }
                    return previous;
                }, out reason, order: 10)) log(reason);
        }
    }
}
