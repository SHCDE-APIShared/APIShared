using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using APIShared.Units.AiMilitary.Orders;

namespace APIShared.Units.AiMilitary.Native
{
    internal sealed unsafe partial class AiMilitaryNativeBackend
    {
        internal bool TryCaptureStrategicIntent(AiNativeGroup group, int action, out AiCurrentOrder order)
        {
            order = default(AiCurrentOrder);
            if (!TryStrategicContext(group, action, out GamePlayerResources* resources, out int role, out uint targetGlobal)) return false;
            order = new AiCurrentOrder(AiOrderKind.Strategic, x: (int)resources->r_AILordMinusOne, y: role,
                command: (int)resources->r_AISiegeState, targetId: (int)resources->r_SiegeAttackTargetPlayerId,
                targetGlobalId: targetGlobal, action: action, extra: unchecked((int)resources->r_AISiegeAttempts));
            return true;
        }

        internal bool IsStrategicIntentCurrent(AiNativeGroup group, AiCurrentOrder order)
        {
            if (order.Kind != AiOrderKind.Strategic || !TryStrategicContext(group, order.Action,
                out GamePlayerResources* resources, out int role, out uint targetGlobal)) return false;
            return order.X == resources->r_AILordMinusOne && order.Y == role &&
                order.TargetId == resources->r_SiegeAttackTargetPlayerId && order.TargetGlobalId == targetGlobal &&
                order.Extra == unchecked((int)resources->r_AISiegeAttempts);
        }

        private bool TryStrategicContext(AiNativeGroup group, int action, out GamePlayerResources* resources,
            out int role, out uint targetGlobal)
        {
            resources = null; role = 0; targetGlobal = 0;
            if (!IsCurrent(group) || !AiMilitaryCatalog.TryReadAic(group.Owner, out InternalAIC* aic) ||
                !GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(group.Owner, out resources) || resources == null ||
                resources->r_AIAllyOrderMode == 2) return false;
            int slot = -1;
            for (int i = 0; i < 300; i++)
                if (AiStrategicIntentPolicy.RoleForStorage(i) != 0 && resources->r_AITribeIdsByStorageRole[i] == group.Id &&
                    resources->r_AITribeGlobalIdsByStorageRole[i] == group.GlobalId)
                { if (slot != -1) return false; slot = i; }
            role = AiStrategicIntentPolicy.RoleForStorage(slot);
            bool enhanced = (((byte*)aic)[0x2F4] & 1) != 0 || *(int*)((byte*)Module + 0x3665FB8) != 0;
            if (!AiStrategicIntentPolicy.Allows(role, action, (int)resources->r_AISiegeState, enhanced)) return false;
            int target = (int)resources->r_SiegeAttackTargetPlayerId;
            if (target < 1 || target > 8 || target == group.Owner ||
                !GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(target, out GamePlayerResources* enemy) || enemy == null || enemy->r_LordUnitGlobalId == 0 ||
                !TryUnit((int)enemy->r_LordUnitId, enemy->r_LordUnitGlobalId, out var lord, out _) ||
                lord->r_UnitChimp != eChimps.CHIMP_TYPE_LORD || lord->r_ControllableForPlayerId != target) return false;
            targetGlobal = enemy->r_LordUnitGlobalId;
            int ownerOffset = group.Owner * 0x177BC;
            if (*(int*)((byte*)Module + 0x2EA70DC + ownerOffset) != target) return false;
            // Verify role identity for every actual member; no leader state or another
            // member's order/path is copied to infer a historical execution state.
            for (int word = 0; word < 625; word++)
            {
                uint bits = *(ushort*)(Tribe(group) + 0x60 + word * 2);
                for (int bit = 0; bits != 0 && bit < 16; bit++, bits >>= 1)
                    if ((bits & 1) != 0)
                    {
                        int id = word * 16 + bit;
                        if (id == 0 || !TryUnit(id, 0, out var member, out _) || member->r_TribeId != group.Id ||
                            member->r_ControllableForPlayerId != group.Owner || member->r_AITribeRole != role) return false;
                    }
            }
            switch (action)
            {
                case 0x3F7: return SuitableTaskList(0x2EB6B64, 0x2EB6B6C, 5999, group.Owner);
                case 0x3F4: return SuitableTaskList(0x2EAAF90, 0x2EAAF98, 5999, group.Owner);
                case 0x3F2: return SuitableTaskList(0x2EA70E4, 0x2EA70EC, 9999, group.Owner);
                case 0x420:
                    return SuitableTaskList(0x2EA70E4, 0x2EA70EC, 9999, group.Owner) ||
                        SuitableTaskList(0x2EB2CC8, 0x2EB2CD0, 5999, group.Owner) ||
                        SuitableTaskList(0x2EAEE2C, 0x2EAEE34, int.MaxValue, group.Owner);
                case 0x3FB:
                    return AiMilitaryNativeObservers.TryGetGlobalTaskOwner(out int owner, out int enemyOwner, out _) &&
                        owner == group.Owner && enemyOwner == target && SuitableTaskList(0x2EB9668, 0x2EB9670, int.MaxValue);
                default: return false;
            }
        }

        private bool SuitableTaskList(int countBase, int rowsBase, int costLimit, int owner = 0)
        {
            int offset = owner * 0x177BC;
            int count = *(int*)((byte*)Module + countBase + offset);
            if (count < 1 || count > 1000) return false;
            int* rows = (int*)((byte*)Module + rowsBase + offset);
            // Existing reservations remain untouched and can prove ongoing intent;
            // the initializer separately selects a free, suitable job for this recruit.
            for (int i = 0; i < count; i++)
            {
                int* row = rows + i * 4;
                if (row[2] < costLimit && TryTilePosition(row[1], out _, out _) && IsStrategicCandidateCurrent(rowsBase, owner, row[0], row[1])) return true;
            }
            return false;
        }

        internal bool IsStrategicCandidateCurrent(int rowsBase, int owner, int taskTile, int approachTile)
        {
            if (!AiRecruitMutationReceipts.IsCurrentWorld) return false;
            if (!TryTilePosition(taskTile, out _, out _) || !TryTilePosition(approachTile, out _, out _) ||
                (*(uint*)((byte*)Module + 0x48F71B0 + approachTile * 4) & 0x10000100) != 0) return false;
            var flags = (TilePropertyFlag)(*(uint*)((byte*)Module + 0x48F71B0 + taskTile * 4));
            int approachHeight = EffectiveTileHeight(approachTile);
            if (!AiRecruitMutationReceipts.IsCurrentWorld) return false;
            int taskHeight = *(byte*)((byte*)Module + 0x4DDD350 + taskTile);
            int taskGround = *(byte*)((byte*)Module + 0x4E2B870 + taskTile);
            if (rowsBase == 0x2EB6B6C) return (flags & TilePropertyFlag.IsMoat) != 0 &&
                taskGround <= approachHeight + 16 && approachHeight - 16 <= taskGround;
            int target;
            if (owner != 0) target = *(int*)((byte*)Module + 0x2EA70DC + owner * 0x177BC);
            else if (!AiMilitaryNativeObservers.TryGetGlobalTaskOwner(out _, out target, out _)) return false;
            if (target < 1 || target > 8) return false;
            if (rowsBase == 0x2EA70EC || rowsBase == 0x2EAAF98 || rowsBase == 0x2EBAA08)
            {
                if ((flags & (TilePropertyFlag.IsWall | TilePropertyFlag.CrenelatedLow)) == 0 ||
                    (flags & TilePropertyFlag.GoodsyardRelated) != 0 ||
                    (*(byte*)((byte*)Module + 0x4E79D90 + taskTile) & 7) + 1 != target ||
                    *(short*)((byte*)Module + 0x4B6AA50 + taskTile * 2) != 0 ||
                    taskHeight - taskGround <= 20 || (taskHeight != approachHeight + 16 && taskGround >= approachHeight + 16) ||
                    approachHeight - 16 > taskHeight) return false;
                return rowsBase != 0x2EAAF98 || (flags & TilePropertyFlag.CrenelatedHigh) == 0;
            }
            if (rowsBase == 0x2EB2CD0 || rowsBase == 0x2EAEE34)
            {
                int id = *(short*)((byte*)Module + 0x4B6AA50 + taskTile * 2);
                return id > 0 && GameBuildingManagerAPI.Instance.TryGetBuildingById(id, out GameBuilding* building) &&
                    building != null && building->r_AliveState == AliveState.IsAlive && building->r_PlayerIdOwner == target &&
                    taskGround <= approachHeight + 16 && approachHeight - 16 <= taskGround;
            }
            if (rowsBase == 0x2EB9670)
            {
                int id = *(short*)((byte*)Module + 0x4C559B0 + taskTile * 2);
                int targetHeight = EffectiveTileHeight(taskTile);
                return id > 0 && TryUnit(id, 0, out var unit, out _) && unit->r_ControllableForPlayerId == target &&
                    targetHeight <= approachHeight + 16 && approachHeight - 16 <= targetHeight;
            }
            return true;
        }

        internal bool IsDigMoatIntentCurrent(AiNativeGroup group, AiCurrentOrder order)
        {
            if (!IsCurrent(group) || order.Command != 6 || !IsMapPosition(order.X, order.Y) ||
                !GameTileManagerAPI.Instance.TryGetMoatWorkTaskAtTile(order.X, order.Y, out var current)) return false;
            MoatWorkTask* task = current.Pointer;
            if (task == null || task->r_TileX != order.X || task->r_TileY != order.Y || task->r_OwnerPlayerId == 0) return false;
            bool completeMoat = GameTileManagerAPI.Instance.HasTilePropertyFlag(task->r_TileId, TilePropertyFlag.IsMoat);
            return task->r_WorkType == MoatWorkType.DigPlannedMoat
                ? !completeMoat && GameTileManagerAPI.Instance.HasTilePropertyFlag(task->r_TileId, TilePropertyFlag.PlannedMoat)
                : task->r_WorkType == MoatWorkType.FillDugMoat && completeMoat;
        }
    }
}
