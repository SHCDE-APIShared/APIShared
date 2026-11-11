using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Units.AiMilitary
{
    // Pending native initialization is save-relevant. Dying/corpse records must not
    // be resurrected as tracked assignments merely because their slot still exists.
    internal static class AiMilitarySaveLifetime
    {
        internal static bool CanCapture(in GameUnit unit) => UnitAccess.IsReallyAlive(in unit) || unit.r_AliveState == AliveState.NeedsInit;
    }
}
