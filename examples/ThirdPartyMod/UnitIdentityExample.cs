using System;
using APIShared;
using APIShared.Units;
using SHCDESE.Interop;

namespace ThirdPartyMod
{
    // Definitions are shared; the real consumer still owns native placement/purchase proof and art.
    internal static class UnitIdentityExample
    {
        private static UnitVariantRegistration ranger;
        internal static void Register(ModApiClient client, Action<string> log)
        {
            if (!client.UnitIdentities.TryRegisterVariant("ranger", (int)eChimps.CHIMP_TYPE_ARCHER, out ranger, out string reason)) log(reason);
        }
        // Invoke only with an exact native result captured by the consumer, while holding the
        // engine monitor; multiplayer additionally needs a shared tick/authenticated Chore scope.
        internal static bool MarkConfirmedReceipt(int unitId, uint capturedGlobalId, out string reason)
        {
            if (ranger == null) { reason = "Variant registration was unavailable."; return false; }
            return ranger.TryMark(unitId, capturedGlobalId, out reason);
        }
    }
}
