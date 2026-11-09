namespace APIShared.Units.AiMilitary.Orders
{
    // Ongoing planner intent, not a promise that a historical per-unit task is still
    // executing. The recruit gets a newly selected job from the current native list.
    internal static class AiStrategicIntentPolicy
    {
        internal static int RoleForStorage(int storage)
        {
            if (storage == 17) return 11;
            if (storage == 11) return 12;
            if (storage == 12) return 13;
            if (storage == 13) return 14;
            if (storage == 14) return 15;
            if (storage == 15 || storage == 16) return 16;
            if (storage >= 186 && storage <= 188) return 17;
            if (storage == 189) return 18;
            if (storage == 190 || storage == 191) return 19;
            if (storage >= 192 && storage <= 199) return 20;
            return 0;
        }
        internal static bool Allows(int role, int action, int phase, bool enhancedAttack)
        {
            // 3C2E0: phase5 repeats 3B980/3C9C0/3BFA0/3C0A0/3B820.
            // 2B2C0 broadcasts ladder work at 4->5; 3CDC0 repeats shock in4/5.
            // Phase6 has different building-attack/advance planners. No old strategic
            // intent is assumed across that transition or retreat phases7..9.
            if (role == 11) return action == 0x3F7 && phase == 5;
            if (role == 14) return action == 0x3F4 && phase == 5;
            if (role == 18) return action == 0x3FB && (phase == 4 || phase == 5);
            if (role == 12 || role == 13 || role == 19 || role == 20)
                return phase == 5 && action == (enhancedAttack ? 0x420 : 0x3F2);
            return false;
        }
    }
}
