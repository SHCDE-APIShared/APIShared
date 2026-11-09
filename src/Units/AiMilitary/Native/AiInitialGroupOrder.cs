using SHCDESE.API;
using SHCDESE.Interop;
using APIShared.Units.AiMilitary.Orders;

namespace APIShared.Units.AiMilitary.Native
{
    internal sealed unsafe partial class AiMilitaryNativeBackend
    {
        internal bool TryGetInitialOrderForEmptyGroup(int unitId, ushort role, int detail, AiNativeGroup group,
            out AiCurrentOrder order, out string reason)
        {
            order = default(AiCurrentOrder); reason = string.Empty;
            if (!IsCurrent(group) || *(short*)(Tribe(group) + 0x5C) != 0)
            { reason = "Initial orders apply only to a current, empty group."; return false; }
            if (role != 1)
            {
                // Allocators create these pools without a command. There is no previous
                // member order to replay; the owner AI subsequently commands the pool.
                order = new AiCurrentOrder(AiOrderKind.Idle); return true;
            }
            if (!GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(group.Owner, out GamePlayerResources* resources) ||
                resources == null)
            { reason = "The defensive group's owner resources are unavailable."; return false; }
            if (!TryUnit(unitId, 0, out _, out reason)) return false;
            int cls = DefensiveClass(resources, unitId), ordinal = -1, tile = 0;
            if ((uint)cls >= 30) { reason = "Invalid defensive class."; return false; }
            var storage = ExpandedStorage;
            if (storage != null && storage.TryFindOrdinal(group, cls, out ordinal))
                storage.TryPosition(group.Owner, cls, ordinal, out tile);
            if (tile == 0)
            {
                int first = *(int*)((byte*)Module + 0x2CEE80 + cls * 4);
                int count = resources->r_AIDefensivePositionCountsByClass.r_Counts[cls];
                for (int i = 0; i < 10 && i < count; i++)
                    if (resources->r_AITribeIdsByStorageRole[first + i] == group.Id &&
                        resources->r_AITribeGlobalIdsByStorageRole[first + i] == group.GlobalId)
                    { ordinal = i; break; }
                if (ordinal >= 0)
                {
                    int populated = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        int candidate = (int)resources->r_AIDefensivePositionTileIdsByClass.r_TileIds[cls * 10 + i];
                        if (candidate != 0 && populated++ == ordinal) { tile = candidate; break; }
                    }
                }
            }
            if (TryTilePosition(tile, out int x, out int y))
            { order = new AiCurrentOrder(AiOrderKind.Hold, x, y); return true; }
            // Native 3E940 fallback: keep origin +(3,4), not the building template center.
            int keepId = (int)resources->r_KeepId;
            if (keepId > 0 && GameBuildingManagerAPI.Instance.TryGetBuildingById(keepId, out GameBuilding* keep) && keep != null)
            {
                x = keep->r_TilePositionXBegin + 3; y = keep->r_TilePositionYBegin + 4;
                if (IsMapPosition(x, y)) { order = new AiCurrentOrder(AiOrderKind.Hold, x, y); return true; }
            }
            reason = "The defensive group has no current AIV or keep destination."; return false;
        }
    }
}
