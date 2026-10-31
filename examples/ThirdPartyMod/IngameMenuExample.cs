using System;
using APIShared.Presentation;

namespace ThirdPartyMod
{
    // Optional example; call Register on Unity during startup. No modpack or Shared source link is needed.
    internal static class IngameMenuExample
    {
        private static IngameMenuActionRegistration registration;
        private static bool available;
        internal static void Register(string ownerGuid, Action open, Action<string> log)
        {
            var position = IngameMenuActions.GetRowState(IngameMenuRow.Help).Left;
            // Hidden registered claims still need explicit takeover permission. This example chooses the extra page instead.
            var options = position.Claims.Count == 0
                ? new IngameMenuActionOptions(IngameMenuRow.Help, IngameMenuSlot.Left)
                : new IngameMenuActionOptions();
            if (!IngameMenuActions.TryRegister(ownerGuid, "inspector",
                () => available ? new IngameMenuActionState("Inspect map", true, true,
                    "Open the map inspector.") : null, // Supply localized text; omit the fourth argument for no popup.
                () => { if (available) open(); }, options, out registration, out string reason)) log(reason);
        }
        // Invoke from the mod's actual state-change event, not an Update/OnTick loop.
        internal static void OnAvailabilityChanged(bool value)
        {
            if (available == value) return;
            available = value;
            registration?.Invalidate();
        }
    }
}
