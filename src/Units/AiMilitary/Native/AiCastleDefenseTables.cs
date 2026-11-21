namespace APIShared.Units.AiMilitary.Native
{
    // Hash-bound castle-defense class data of CrusaderDE.dll FBCB9319 (audit 17.11.2026,
    // AI_MILITARY_ADMISSION.md "Castle-defense recruit allocation"). Free of game types so that
    // AiMilitary.NativeTests can compare it with the canonical DLL tables and instructions.
    internal static class AiCastleDefenseTables
    {
        // Controllers called by 2FE20 for the class windows: 1 = 3D9E0 (table 2CF020),
        // 2 = 3DEE0 (2CF060), 3 = 3DB30 (2CF0A8); 0 = no window controller.
        internal static int ControllerFamily(int defensiveClass)
        {
            switch (defensiveClass)
            {
                case 6: case 7: case 14: case 16: case 19: case 25: case 26: return 1;
                case 9: case 11: case 12: case 13: case 15: case 18: case 23: case 24: case 27: return 2;
                case 8: case 10: case 17: case 22: case 28: case 29: return 3;
                default: return 0;
            }
        }

        // 2BC40 cuts only classes 8, 10 and 17 to max_mobile_groups. 2D760 maps exactly the
        // unit types 24, 26 and 74 to them (2CEE00), and none of these is in its class-13 ranged
        // set, so the class of these types does not depend on the unit or player state.
        internal static bool TryGetMobileWindowClass(int unitType, out int defensiveClass)
        {
            switch (unitType)
            {
                case 24: defensiveClass = 8; return true;
                case 26: defensiveClass = 10; return true;
                case 74: defensiveClass = 17; return true;
                default: defensiveClass = -1; return false;
            }
        }

        // 2BC40 returns tribe 0 for a positive position count (the class-window branch of 291F0)
        // when the window, cut to max_mobile_groups because the count exceeds it, is not positive.
        internal static bool IsDefenseWindowClosed(int positionCount, int maxMobileGroups) =>
            positionCount > 0 && maxMobileGroups <= 0;
    }
}
