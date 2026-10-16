using System;
using APIShared;
using APIShared.Units;
using SHCDESE.Interop;

namespace ThirdPartyMod
{
    // Composed maximum health: a Lord percentage and a recruit multiplier from two registrations, applied in
    // one write when the unit settles. Register during startup on every multiplayer peer, before a mission.
    internal static class UnitStatExample
    {
        // Integer settings that must be identical on all peers (for example host-synchronized).
        private static volatile int lordPercent = 150, recruitPerMille = 1250;
        private static UnitStatRegistration lord, recruits;

        internal static void Register(ModApiClient api, Action<string> logger)
        {
            if (!UnitStatModifiers.TryRegister(api.OwnerGuid, "lord-health", UnitStat.MaxHealth, UnitStatLayer.Multiplier, 0,
                    LordHealth, out lord, out string reason))
                logger(reason);
            if (!UnitStatModifiers.TryRegister(api.OwnerGuid, "recruit-health", UnitStat.MaxHealth, UnitStatLayer.Multiplier, 10,
                    RecruitHealth, out recruits, out reason))
                logger(reason);
        }

        // Settings change between matches; they are frozen during a match.
        internal static void Configure(int lord, int recruitsPerMille) { lordPercent = lord; recruitPerMille = recruitsPerMille; }

        // Simulation thread, for every settled unit: decide from the subject only, no logging or Unity calls.
        private static bool LordHealth(UnitStatSubject unit, out UnitStatContribution contribution)
        {
            contribution = UnitStatContribution.Percent(lordPercent);
            return unit.UnitType == eChimps.CHIMP_TYPE_LORD && lordPercent != 100;
        }

        private static bool RecruitHealth(UnitStatSubject unit, out UnitStatContribution contribution)
        {
            contribution = UnitStatContribution.Scale(recruitPerMille, 1000);
            return unit.Cause == UnitSettledCause.MercenaryOutpost || unit.Cause == UnitSettledCause.EuropeanBarracks;
        }

        internal static void Unregister()
        {
            lord?.Dispose();
            recruits?.Dispose();
        }
    }
}
