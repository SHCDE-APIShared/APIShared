namespace APIShared.Units.AiMilitary.Orders
{
    // Admission of a recruit whose group has no observed current order (for example after a
    // save load, or once an earlier order was invalidated). Hash-bound static audit of
    // CrusaderDE.dll FBCB9319..31E2 (2026-11-17/18, baseline AI_MILITARY_ADMISSION.md, "Controller
    // handoff"): every Vanilla recruit classifier (291F0, 292F0, 29360, 29430, 294B0) and the
    // role-6/7 regrouping (303E0, 30310) writes only the unit role and the membership (11D370).
    // None of them gives the recruit an order. For the roles below, the owner's regular AI pass
    // (2AE40) then commands the group as a whole from its own controller state, so no stored
    // group intent is needed and the recruit is treated exactly like a Vanilla recruit:
    //  - native 1 castle defense: 2FE20 every pass -> 3D9E0/3DEE0/3DB30/3DD60/3E6C0/3EA10 -> 11DD10;
    //  - native 4 defense patrol: 3EA50 every defense_patrol_delay passes -> 11DD10 or 3E9A0;
    //  - native 5 home moat: 3E700 every pass -> 11E960 command 6 or 3E940;
    //  - native 6/7 economy protection/intervention: 3E070/3D840 every pass -> 126020 + 11B520,
    //    or 3E940. Below the AIC strength threshold Vanilla deliberately issues no order.
    //  - native 2 harassment: 3E200 every pass for the six harassment slots; every order it gives
    //    is group-wide (11DD10, 3E9A0, or the targeted group attack 199C70). Between orders (interval
    //    counter, combat gate 2DC30, the 100-pass target cache) it gives none, so a recruit waits
    //    for the group's next order exactly like a 292F0 recruit.
    //  - native 10-20 siege roles while the AI siege state (r_AISiegeState) is 0: 3C2E0 every
    //    pass either sends every slot of every siege window (table 2C8040) home via 3D560 ->
    //    3E940/3E9A0 -> 11DD10, or, starting a siege or harassment dispatch, moves every member of
    //    every siege group with 3D6E0/3D020 -> 11B520.
    //  - active siege (audit 2026-11-18, AI_MILITARY_ADMISSION.md "Active siege controller"): Vanilla
    //    stops siege recruitment (40740) once r_AISiegeState != 0. 3C2E0 reaches an idle joined member
    //    within a few passes for storm (3CE40 group move every pass) in states 3-5, shock (3CDC0 ->
    //    122B40 every pass) in 4-5, and in state 5 the every-4th-pass dispatch of moat/assassins/
    //    diversion/reserve/wall (122B40 picks idle members) and cover (3CCD0 rally move). In every
    //    other state the next group-wide order reaches all members at a later transition: 3D260/
    //    2B340/3C150/3B8D0/2B2C0 at 2->3, 3->4 and 4->5, the retreat move 3D3E0 at ->9, and 3D560 home
    //    every pass once state 0 returns (at most 20 passes after 9). States 1 and 8 never persist past
    //    the pass that sets them. User decision 2026-11-18: such recruits join and wait for that phase.
    // 11DD10 marks members that are already near the target (or otherwise excluded) and its
    // 11B520 call skips marked members, so existing members at their post get no new order.
    // Harassment engineers (22), siege engineers and None never reach this policy: Apply admits
    // them idle without a ledger order (their pools are not commanded as a group).
    internal static class AiControllerHandoffPolicy
    {
        internal const string AppliedDetail =
            "Joined like a Vanilla recruit; the owner's next regular AI pass gives the group order.";
        internal const string SiegeWaitDetail =
            "Joined like a Vanilla recruit; the AI commands the group at its next siege phase or retreat, the recruit waits until then.";

        internal static bool CommandsJoinedMembers(AiMilitaryRole role) => CommandsJoinedMembers(role, -1);

        // siegeState: the owner's r_AISiegeState, or -1 when unavailable (siege roles then stay explicit).
        internal static bool CommandsJoinedMembers(AiMilitaryRole role, int siegeState)
        {
            switch (role)
            {
                case AiMilitaryRole.CastleDefense:
                case AiMilitaryRole.DefensePatrol:
                case AiMilitaryRole.HomeMoat:
                case AiMilitaryRole.EconomyProtection:
                case AiMilitaryRole.Intervention:
                case AiMilitaryRole.Harassment:
                    return true;
                default:
                    return IsSiegeRole(role) && siegeState >= 0 && siegeState <= 9;
            }
        }

        // Detail for an admitted recruit: whether it is ordered within a few passes or only at the
        // owner's next siege transition (see the audit above).
        internal static string Detail(AiMilitaryRole role, int siegeState) =>
            IsSiegeRole(role) && !SiegeControllerReachesIdleMember(role, siegeState) ? SiegeWaitDetail : AppliedDetail;

        internal static bool SiegeControllerReachesIdleMember(AiMilitaryRole role, int siegeState)
        {
            if (siegeState == 0) return true;
            switch (role)
            {
                case AiMilitaryRole.SiegeStorm: return siegeState >= 3 && siegeState <= 5;
                case AiMilitaryRole.SiegeShock: return siegeState == 4 || siegeState == 5;
                case AiMilitaryRole.SiegeMoat:
                case AiMilitaryRole.SiegeAssassins:
                case AiMilitaryRole.SiegeDiversion:
                case AiMilitaryRole.SiegeCover:
                case AiMilitaryRole.SiegeReserve:
                case AiMilitaryRole.SiegeWall:
                    return siegeState == 5;
                default: return false;
            }
        }

        private static bool IsSiegeRole(AiMilitaryRole role)
        {
            switch (role)
            {
                case AiMilitaryRole.SiegeEngineers: case AiMilitaryRole.SiegeMoat: case AiMilitaryRole.SiegeAssassins:
                case AiMilitaryRole.SiegeDiversion: case AiMilitaryRole.SiegeLadders: case AiMilitaryRole.SiegeTunnelers:
                case AiMilitaryRole.SiegeStorm: case AiMilitaryRole.SiegeCover: case AiMilitaryRole.SiegeShock:
                case AiMilitaryRole.SiegeReserve: case AiMilitaryRole.SiegeWall:
                    return true;
                default: return false;
            }
        }
    }
}
