using System;
using System.Runtime.InteropServices;
using APIShared.Units.AiMilitary.Orders;

namespace APIShared.Units.AiMilitary.Native
{
    internal sealed unsafe partial class AiMilitaryNativeBackend
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate long UnitMoveDelegate(IntPtr manager, int unitId, int x, int y, int extra);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GroupQueryDelegate(IntPtr manager, int groupId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int UnitQueryDelegate(IntPtr manager, int unitId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void UnitActionDelegate(IntPtr manager, int unitId);
        private UnitMoveDelegate buildRoute;
        private GroupQueryDelegate allAssassins, moveCompleted;
        private UnitQueryDelegate jobPosition;
        private UnitQueryDelegate tileHeight;
        private UnitActionDelegate clearPath, endAttack;

        private void InitializeRouting(AiMilitaryNativeBindings bindings)
        {
            buildRoute = bindings.Bind<UnitMoveDelegate>(AiMilitaryNativeBindings.MoveRva);
            allAssassins = bindings.Bind<GroupQueryDelegate>(AiMilitaryNativeBindings.AllAssassinsRva);
            moveCompleted = bindings.Bind<GroupQueryDelegate>(AiMilitaryNativeBindings.MoveCompletedRva);
            jobPosition = bindings.Bind<UnitQueryDelegate>(AiMilitaryNativeBindings.JobPositionRva);
            tileHeight = bindings.Bind<UnitQueryDelegate>(AiMilitaryNativeBindings.EffectiveTileHeightRva);
            clearPath = bindings.Bind<UnitActionDelegate>(AiMilitaryNativeBindings.ClearPathRva);
            endAttack = bindings.Bind<UnitActionDelegate>(AiMilitaryNativeBindings.EndAttackRva);
        }

        internal byte* NativeRawUnit(int unitId) => (byte*)UnitManager + unitId * UnitStride;
        internal bool IsMapPosition(int x, int y) => (uint)x < 800 && (uint)y < 800 &&
            ((byte*)Module + 0x3A11EA4)[y * 800 + x] != 0;

        internal bool TryTilePosition(int tile, out int x, out int y)
        {
            x = y = 0;
            if (tile <= 0 || tile >= 640000) return false;
            y = *(short*)((byte*)Module + 0x3AAE2A4 + tile * 2);
            if ((uint)y >= 800) return false;
            x = tile - *(int*)((byte*)Module + 0x402FF2C + y * 12);
            return IsMapPosition(x, y);
        }

        // Does not write caller-specific command/state/target fields. Each initializer
        // publishes precisely its own native caller's fields before requesting a route.
        internal bool TryBuildRecruitRoute(int unitId, uint globalId, AiNativeGroup group,
            int x, int y, out string reason)
        {
            long operationWorld = worldGeneration;
            if (!IsWorldCurrent(operationWorld)) { reason = "The recruit route belongs to an ended world."; return false; }
            if (!TryUnit(unitId, globalId, out var unit, out reason) || !HasMember(group, unitId))
            { if (reason.Length == 0) reason = "Recruit membership changed before path construction."; return false; }
            if (!IsMapPosition(x, y)) { reason = "The effective order destination is outside the playable map."; return false; }
            byte* raw = NativeRawUnit(unitId);
            // 19B1B0 may repair an incoherent unit by moving world occupancy. Admission
            // only operates on settled, coherent recruits, so that repair is unnecessary.
            int cx = *(short*)(raw + 0x71C), cy = *(short*)(raw + 0x71E);
            if (!IsMapPosition(cx, cy) || *(int*)(raw + 0x72C) != cx + *(int*)((byte*)Module + 0x402FF2C + cy * 12))
            { reason = "The recruit has not settled into a coherent native tile."; return false; }
            int* context = (int*)((byte*)Module + 0x60AD6E0);
            int previous0 = context[0], previous1 = context[1], previous2 = context[2];
            try
            {
                context[0] = 0; context[1] = 0; context[2] = allAssassins(TribeManager, group.Id) != 0 ? 1 : 0;
                if (!AiUnitMoveReceipts.TryRun(unitId, x, y, 0,
                    () => buildRoute(UnitManager, unitId, x, y, 0), out reason)) return false;
                if (!IsWorldCurrent(operationWorld) || unit->r_GlobalId != globalId || !HasMember(group, unitId) ||
                    *(short*)(raw + 0x744) != x || *(short*)(raw + 0x746) != y)
                { reason = "The effective singleton movement was rejected or changed."; return false; }
                reason = string.Empty; return true;
            }
            finally
            { if (IsWorldCurrent(operationWorld)) { context[0] = previous0; context[1] = previous1; context[2] = previous2; } }
        }

        internal int InitializeJobPosition(int unitId) => jobPosition(UnitManager, unitId);
        internal int EffectiveTileHeight(int tileId) => tileHeight(Module + 0x405EDB0, tileId);
        internal bool IsGroupMovementCompleted(AiNativeGroup group) => IsCurrent(group) && moveCompleted(TribeManager, group.Id) != 0;

        internal bool StopRecruit(int unitId, uint globalId, out string reason)
        {
            long operationWorld = worldGeneration;
            if (!IsWorldCurrent(operationWorld)) { reason = "The recruit stop belongs to an ended world."; return false; }
            if (!TryUnit(unitId, globalId, out _, out reason)) return false;
            byte* raw = NativeRawUnit(unitId);
            if (*(short*)(raw + 0x8F6) == 1 || raw[0xA6B] != 0)
            { reason = "The recruit is not eligible for native Stop(31)."; return false; }
            raw[0xA1D] = 3; *(short*)(raw + 0x9F4) = 3; *(short*)(raw + 0xA08) = 0;
            clearPath(UnitManager, unitId); endAttack(UnitManager, unitId);
            if (!IsWorldCurrent(operationWorld)) { reason = "The world changed during recruit stop."; return false; }
            *(int*)(raw + 0xA00) = 0; *(short*)(raw + 0x99E) = 0;
            if (*(short*)(raw + 0x918) == 0x7C) *(short*)(raw + 0x918) = 0;
            return true;
        }
    }
}
