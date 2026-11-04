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
            TextBlock label = null;
            Action<AllyGoodsPanelContext> update = selected => {
                if (label != null) label.Text = selected.HasSelection ?
                    "Selected ally " + selected.RecipientPlayerId + ": " + selected.Amount + " " + selected.Good +
                    ". Selection is not a delivery acknowledgement." : "Select an ally, a good and a quantity.";
            };
            if (!AllyGoodsPanelExtensions.TryRegisterSection(owner, "quantity-example", context =>
            {
                label = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
                update(context.Selection); return label;
            }, out _, out string reason)) log(reason);
            if (!AllyGoodsPanelExtensions.TrySubscribeContext(owner, "quantity-example-context", update, out reason)) log(reason);
        }
    }
}
