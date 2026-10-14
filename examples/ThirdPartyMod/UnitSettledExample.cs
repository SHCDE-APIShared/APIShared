using System;
using APIShared;
using APIShared.Units;
using SHCDESE.API;

namespace ThirdPartyMod
{
    // Optional policy: recruited troops start with a configured maximum health. Register during
    // startup on every multiplayer peer; the callback runs inside the simulation tick.
    internal static class UnitSettledExample
    {
        // Must be identical on all peers (for example a host-synchronized setting).
        private static volatile float recruitHealthFactor = 1.5f;

        internal static void Register(ModApiClient api, Action<string> logger)
        {
            if (!UnitSettledEvents.TryRegister(api.OwnerGuid, "recruit-health", OnSettled, out string reason))
                logger(reason);
        }

        internal static void Configure(float factor) => recruitHealthFactor = factor;

        private static void OnSettled(UnitSettledNotification unit)
        {
            float factor = recruitHealthFactor;
            if (!unit.IsRecruitment || factor == 1f) return;
            // The new type's health was initialized at settlement; scale it once for this trigger.
            // Do not log, touch Unity/Noesis or read local-player state here.
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            int health = Math.Max(1, (int)(units.GetMaxHealth(unit.UnitId) * factor));
            units.SetMaxHealth(unit.UnitId, health);
            units.SetCurrentHealth(unit.UnitId, health);
        }
    }
}
