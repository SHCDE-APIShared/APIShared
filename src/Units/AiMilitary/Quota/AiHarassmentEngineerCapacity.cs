using System;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Units.AiMilitary.Quota
{
    internal static unsafe partial class AiMilitaryQuotaRuntime
    {
        // Called only for an actual quiescent admission, never from a tick/unit hook
        // or menu enumeration. Current records include uncounted freshly admitted
        // engineers and regular peasant recruitment reservations, unlike E79C's fold.
        internal static bool TryCheckHarassmentEngineerCapacity(AiUnitRef candidate, int owner, bool additional, out string reason)
        {
            reason = string.Empty;
            if (module == IntPtr.Zero || owner < 1 || owner > 8 || candidate.UnitId < 1 || candidate.UnitId >= 10000 || candidate.GlobalId == 0)
            { reason = "The physical harassment engineer capacity cannot be read."; return false; }
            byte* recruit = (byte*)(module + AiQuotaNativeContract.UnitManager + candidate.UnitId * AiQuotaNativeContract.UnitStride);
            if (*(uint*)(recruit + 0x6F0) != candidate.GlobalId || *(ushort*)(recruit + 0x6EE) != owner ||
                *(ushort*)(recruit + 0x6E6) != (ushort)eChimps.CHIMP_TYPE_ENGINEER || !UnitAccess.IsReallyAlive((GameUnit*)(recruit + 0x65C)))
            { reason = "The engineer identity/owner/life state changed before capacity validation."; return false; }
            const int role = 22, cap = 20, regularDemand = 2;
            int physical = 0; bool alreadyCounted = false;
            int high = *(int*)(module + AiQuotaNativeContract.UnitManager);
            if (high < 1 || high > 10000) { reason = "The native unit-array bound is invalid."; return false; }
            for (int id = 1; id < high; id++)
            {
                byte* raw = (byte*)(module + AiQuotaNativeContract.UnitManager + id * AiQuotaNativeContract.UnitStride);
                ushort life = *(ushort*)(raw + 0x6E4), type = *(ushort*)(raw + 0x6E6);
                if ((life != (ushort)AliveState.IsAlive && life != (ushort)AliveState.NeedsInit) || *(ushort*)(raw + 0x8F8) != 0 ||
                    *(ushort*)(raw + 0x6EE) != owner || *(ushort*)(raw + 0xA82) != role ||
                    (type != (ushort)eChimps.CHIMP_TYPE_ENGINEER && type != (ushort)eChimps.CHIMP_TYPE_PEASANT)) continue;
                physical++; if (id == candidate.UnitId) alreadyCounted = true;
            }
            int poolRegular = 0; bool inPool = false;
            int player = owner * AiQuotaNativeContract.PlayerStride;
            int tribe = *(short*)(module + 0x379F7BA + player); uint global = *(uint*)(module + 0x379FB5C + player);
            if (tribe > 0 && tribe < 4500 && global != 0)
            {
                byte* group = (byte*)(module + 0x7CC6720 + tribe * 0x688);
                if (*(uint*)(group + 0x34) == global && *(int*)(group + 0x2C) == owner)
                {
                    poolRegular = RegularGroup(tribe, group, *(short*)(group + 0x5C));
                    inPool = *(ushort*)(recruit + 0x930) == tribe && *(uint*)(recruit + 0x93C) == global &&
                        (*(ushort*)(group + 0x60 + (candidate.UnitId >> 4) * 2) & (1 << (candidate.UnitId & 15))) != 0;
                }
            }
            if (inPool) { if (isAdditional(candidate) != additional) poolRegular += additional ? -1 : 1; }
            else if (!additional) poolRegular++;
            int after = physical + (alreadyCounted ? 0 : 1), missingRegular = Math.Max(0, regularDemand - poolRegular);
            if (after > cap)
            { reason = "The physical native limit of 20 harassment engineers would be exceeded."; return false; }
            // Native 2ECD0 requires E79C < 20 for both recruitment AND building.
            // Merely ending at 20 would strand even a complete regular two-man pool.
            if (after + missingRegular >= cap)
            { reason = "Admission needs room for " + missingRegular + " missing regular pool engineers and one native production slot below the physical limit of 20."; return false; }
            return true;
        }
    }
}
