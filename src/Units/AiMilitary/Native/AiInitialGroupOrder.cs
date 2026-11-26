using System;
using SHCDESE.API;
using SHCDESE.Interop;
using APIShared.Units.AiMilitary.Orders;

namespace APIShared.Units.AiMilitary.Native
{
    internal sealed unsafe partial class AiMilitaryNativeBackend
    {
        // Player-record fields read by the castle-defense controller (FBCB9319, player stride 0x583C,
        // base 379ADD0): +0x2B54 mobile rotation counter (379D924, advanced by 2FE20), +0x38F0
        // defensive alert (379E6C0). The Extender has no names for them; see UpdateToNewDLL.md.
        private const int MobileRotationOffset = 0x2B54, DefensiveAlertOffset = 0x38F0;

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
            return TryCastleDefenseTarget(resources, unitId, group, out order, out reason);
        }

        // Role 1: the target that the owner's next controller pass 2FE20 gives this group, chosen
        // like Vanilla (audit 17.11.2026, AI_MILITARY_ADMISSION.md "Castle-defense recruit allocation"):
        //  storage slot 0 -> 3E6C0 -> 3E940 keep origin +3/+4;
        //  storage slot 1 -> 3EA10 -> 3E9A0 keep door, only while its building ID is positive;
        //  class windows   -> 3D9E0/3DEE0/3DB30 -> 3E850: the populated AIV position if 3E650 finds
        //                     it reachable from the leader's tile, otherwise 3E940.
        // The recruit becomes the leader of the empty group, so reachability starts at its tile.
        // Returns false whenever that pass would issue no order now (alert branches, mobile slots
        // outside the 3DB30 window, no keep/door): the recruit then joins like a Vanilla recruit.
        private bool TryCastleDefenseTarget(GamePlayerResources* resources, int unitId, AiNativeGroup group,
            out AiCurrentOrder order, out string reason)
        {
            order = default(AiCurrentOrder);
            if (InStorage(resources, 0, group)) return TryKeepOrder(resources, out order, out reason);
            if (InStorage(resources, 1, group)) return TryKeepDoorOrder(resources, out order, out reason);
            int cls = DefensiveClass(resources, unitId);
            if ((uint)cls >= 30) { reason = "Invalid defensive class."; return false; }
            int fromTile = *(int*)(NativeRawUnit(unitId) + 0x72C);
            int tile = 0;
            var storage = ExpandedStorage;
            if (storage != null && storage.TryFindOrdinal(group, cls, out int expanded) && expanded >= 10)
            {
                // Slots >= 10 belong to Fixes' controller, which applies the same leader-tile
                // reachability (ExcludeLadderClimb) before its keep retreat.
                storage.TryPosition(group.Owner, cls, expanded, out tile);
                return TryDefensePositionOrder(resources, group.Owner, fromTile, tile, out order, out reason);
            }
            int first = *(int*)((byte*)Module + 0x2CEE80 + cls * 4);
            int count = resources->r_AIDefensivePositionCountsByClass.r_Counts[cls], slot = -1;
            for (int i = 0; i < 10 && i < count; i++)
                if (InStorage(resources, first + i, group)) { slot = i; break; }
            if (slot < 0) { reason = "The defensive group is in no storage slot that the AI controller commands."; return false; }
            bool alert = *(int*)((byte*)resources + DefensiveAlertOffset) > 0;
            int ordinal;
            switch (AiCastleDefenseTables.ControllerFamily(cls))
            {
                case 1: // 3D9E0 (wall classes)
                case 2: // 3DEE0 (castle classes)
                    // Under alert both depend on the leader's live position or native CF4A0; the
                    // controller decides on its next pass, as for any Vanilla recruit.
                    if (alert) { reason = "Defensive alert: the AI controller chooses this group's position itself."; return false; }
                    ordinal = slot; break;
                case 3: // 3DB30 (mobile classes)
                {
                    if (alert) { reason = "Defensive alert: the AI controller sends mobile groups itself."; return false; }
                    int window = Math.Min(count, ((InternalAIC*)LordManager)[resources->r_AILordMinusOne - 1].max_mobile_groups);
                    if (slot >= window) { reason = "Vanilla's mobile controller does not command this group slot."; return false; }
                    int step = Math.Max(1, count / window);
                    ordinal = (int)(((long)*(int*)((byte*)resources + MobileRotationOffset) + (long)slot * step) % count);
                    break;
                }
                default: reason = "No Vanilla castle-defense controller commands this class."; return false;
            }
            // 3E850's scan: each nonzero tile decrements the ordinal; the first one taking it below
            // zero wins (a negative ordinal therefore selects the first populated tile).
            int remaining = ordinal;
            for (int i = 0; i < 10; i++)
            {
                int candidate = (int)resources->r_AIDefensivePositionTileIdsByClass.r_TileIds[cls * 10 + i];
                if (candidate != 0 && --remaining < 0) { tile = candidate; break; }
            }
            return TryDefensePositionOrder(resources, group.Owner, fromTile, tile, out order, out reason);
        }

        // 3E850 after the ordinal: reachable position -> 11DD10 there, otherwise 3E940.
        private bool TryDefensePositionOrder(GamePlayerResources* resources, int owner, int fromTile, int tile,
            out AiCurrentOrder order, out string reason)
        {
            if (IsDefenseTargetReachable(owner, fromTile, tile) && TryTilePosition(tile, out int x, out int y))
            { order = new AiCurrentOrder(AiOrderKind.Hold, x, y); reason = string.Empty; return true; }
            return TryKeepOrder(resources, out order, out reason);
        }

        // 3E940: keep origin +(3,4) while the keep building ID is positive, not the template center.
        private bool TryKeepOrder(GamePlayerResources* resources, out AiCurrentOrder order, out string reason)
        {
            order = default(AiCurrentOrder);
            int keepId = (int)resources->r_KeepId;
            if (keepId > 0 && BuildingAccess.TryGetById(GameBuildingManagerAPI.Instance, keepId, out GameBuilding* keep, out _) && keep != null)
            {
                int x = keep->r_TilePositionXBegin + 3, y = keep->r_TilePositionYBegin + 4;
                if (IsMapPosition(x, y)) { order = new AiCurrentOrder(AiOrderKind.Hold, x, y); reason = string.Empty; return true; }
            }
            reason = "The defensive group has no current keep destination."; return false;
        }

        // 3E9A0: the stored keep-door tile (+0x1D8/+0x1DC) while the door building ID (+0x1D4) is positive.
        private bool TryKeepDoorOrder(GamePlayerResources* resources, out AiCurrentOrder order, out string reason)
        {
            order = default(AiCurrentOrder);
            int x = (int)resources->r_KeepDoorTilePositionX, y = (int)resources->r_KeepDoorTilePositionY;
            if ((int)resources->r_KeepDoorId > 0 && IsMapPosition(x, y))
            { order = new AiCurrentOrder(AiOrderKind.Hold, x, y); reason = string.Empty; return true; }
            reason = "Vanilla gives this fallback group no order without a keep door."; return false;
        }

        private static bool InStorage(GamePlayerResources* resources, int slot, AiNativeGroup group) =>
            resources->r_AITribeIdsByStorageRole[slot] == group.Id && resources->r_AITribeGlobalIdsByStorageRole[slot] == group.GlobalId;
    }
}
