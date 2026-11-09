using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Tribes;
using SHCDESE.EventAPI.Units;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using APIShared.Units.AiMilitary.Native;
using APIShared.Units.AiMilitary.Orders;
using APIShared.Units.AiMilitary.Quota;
using APIShared.Units.AiMilitary.Engineering;

namespace APIShared.Units.AiMilitary
{
    internal sealed unsafe partial class AiMilitaryService
    {
        private void ObserveMove(int tribe, int x, int y, short patrol, bool fresh, int moveType, long result)
        {
            if (Volatile.Read(ref observing) != 0 && TryGroup(tribe, out var group))
                orders.ObserveMove(generation, group, x, y, patrol, fresh, moveType, result);
        }
        private void ObserveTarget(int tribe, int command, int arg1, int arg2, int arg3, long result)
        {
            if (Volatile.Read(ref observing) == 0 || !TryGroup(tribe, out var group)) return;
            if (command == 4 && arg1 > 0 && UnitAccess.TryGetById(arg1, out GameUnit* target, out _) &&
                target->r_GlobalId == unchecked((uint)arg2) && UnitAccess.IsReallyAlive(target))
                orders.ObserveAccepted(generation, group, new AiCurrentOrder(AiOrderKind.AttackUnit,
                    command: command, targetId: arg1, targetGlobalId: unchecked((uint)arg2), extra: arg3), result);
            else if ((command == 9 || command == 36 || command == 38) && arg1 > 0 &&
                GameBuildingManagerAPI.Instance.TryGetBuildingById(arg1, out GameBuilding* building) &&
                building->r_GlobalId == unchecked((uint)arg2) && building->r_AliveState == AliveState.IsAlive)
                orders.ObserveAccepted(generation, group, new AiCurrentOrder(AiOrderKind.AttackBuilding,
                    command: command, targetId: arg1, targetGlobalId: unchecked((uint)arg2), extra: arg3), result);
            else if (command == 5 || command == 22)
                orders.ObserveAccepted(generation, group, new AiCurrentOrder(AiOrderKind.AttackTile,
                    arg1, arg2, command, extra: arg3), result);
            else if (command == 6)
                orders.ObserveAccepted(generation, group, new AiCurrentOrder(AiOrderKind.DigMoat,
                    arg1, arg2, command, extra: arg3), result);
            else orders.ObserveTarget(generation, group, command, arg1, arg2, arg3, result);
        }
        private void ObserveStrategic(int tribe, int action, long result)
        {
            if (Volatile.Read(ref observing) != 0 && TryGroup(tribe, out var group))
                orders.ObserveStrategic(generation, group, action, result);
        }
        private bool TryGroup(int id, out AiNativeGroup group)
        {
            group = default;
            return backend != null && id > 0 && id < AiMilitaryNativeBackend.TribeCapacity && GameTribeManagerAPI.Instance.TryGetTribeById(id, out GameTribe* tribe) &&
                backend.TryGetGroup(id, tribe->r_PlayerIdOwner, out group);
        }
        private void InvalidateIndividualMove(int id)
        {
            if (Volatile.Read(ref observing) != 0 && UnitAccess.TryGetById(id, out GameUnit* unit, out _) &&
                TryGroup(unit->r_TribeId, out var group)) orders.Invalidate(group);
        }
    }
}
