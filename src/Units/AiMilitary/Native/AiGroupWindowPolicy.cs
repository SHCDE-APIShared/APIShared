namespace APIShared.Units.AiMilitary.Native
{
    internal static class AiGroupWindowPolicy
    {
        internal static bool TryCount(int configured, int maximum, bool nonpositiveUsesBase, out int count)
        {
            count = 0;
            if (maximum < 1 || configured > maximum || (configured <= 0 && !nonpositiveUsesBase)) return false;
            // 2B8D0 takes its single BASE-slot branch for any configured count <=0.
            // Storm/patrol are additionally constrained by their later planner loops.
            count = configured <= 0 ? 1 : configured; return true;
        }
    }
}
