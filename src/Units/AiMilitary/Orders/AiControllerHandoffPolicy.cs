namespace APIShared.Units.AiMilitary.Orders
{
    // Admission of a recruit whose group has no observed current order (for example after a
    // save load, or once an earlier order was invalidated). Hash-bound static audit of
    // CrusaderDE.dll FBCB9319..31E2 (2026-11-17, baseline AI_MILITARY_ADMISSION.md, "Controller
    // handoff"): every Vanilla recruit classifier (291F0, 292F0, 29360, 29430, 294B0) and the
    // role-6/7 regrouping (303E0, 30310) writes only the unit role and the membership (11D370).
    // None of them gives the recruit an order. For the roles below, the owner's regular AI pass
    // (2AE40 -> 2FE20/3E070/3D840) then commands the current members again from its own
    // controller state, so no stored group intent is needed:
    //  - native 1 castle defense: 3D9E0/3DEE0/3DB30/3DD60 every pass -> 3E850/3E940/3E9A0 -> 11DD10;
    //  - native 4 defense patrol: 3EA50 every defense_patrol_delay passes -> 11DD10 or 3E9A0;
    //  - native 5 home moat: 3E700 every pass -> 11E960 command 6 or 3E940;
    //  - native 6/7 economy protection/intervention: 3E070/3D840 every pass -> 126020 + 11B520,
    //    or 3E940. Below the AIC strength threshold Vanilla deliberately issues no order.
    // 11DD10 marks members that are already near the target (or otherwise excluded) and its
    // 11B520 call skips marked members, so existing members at their post get no new order.
    // Harassment and siege roles are excluded: their controllers cache targets or dispatch
    // strategic work once, so a natural recommand of a newcomer is not proven there.
    internal static class AiControllerHandoffPolicy
    {
        internal const string AppliedDetail =
            "Joined like a Vanilla recruit; the owner's next regular AI pass gives the group order.";

        internal static bool CommandsJoinedMembers(AiMilitaryRole role)
        {
            switch (role)
            {
                case AiMilitaryRole.CastleDefense:
                case AiMilitaryRole.DefensePatrol:
                case AiMilitaryRole.HomeMoat:
                case AiMilitaryRole.EconomyProtection:
                case AiMilitaryRole.Intervention:
                    return true;
                default:
                    return false;
            }
        }
    }
}
