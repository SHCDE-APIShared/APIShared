using System;
using APIShared.Units.AiMilitary.Native;

namespace APIShared.Units.AiMilitary.Orders
{
    // Fresh-member half of 122B40. Selection is read-only until one free entry is
    // committed. In particular the whole-player reset loops in 111060/111330 are
    // deliberately not replayed during admission of one additional unit.
    internal sealed unsafe class AiStrategicRecruitInitializer : IAiRecruitOrderHandler
    {
        private readonly AiMilitaryNativeBackend backend;
        internal AiStrategicRecruitInitializer(AiMilitaryNativeBackend backend) { this.backend = backend; }
        public bool Handles(AiOrderKind kind) => kind == AiOrderKind.Strategic;
        public bool TryInitialize(int unitId, uint globalId, AiNativeGroup group, AiCurrentOrder order, out string reason)
        {
            long operationWorld = backend.WorldGeneration;
            if (!backend.IsWorldCurrent(operationWorld)) { reason = "The strategic admission belongs to an ended world."; return false; }
            byte* raw = backend.NativeRawUnit(unitId);
            if (*(short*)(raw + 0x8F6) != 0 || raw[0xA6B] != 0 || *(short*)(raw + 0xA9E) == 0)
            { reason = "The recruit is not eligible for a fresh native strategic job."; return false; }
            int action = order.Action == 0x420 ? 0x3F2 : order.Action;
            int state = *(short*)(raw + 0x918), type = *(short*)(raw + 0x6E6);
            if (action != 0x3FD && (((((state - 0x65) & 0xFFF9) == 0) && state != 0x69) ||
                ((type == 24 || type == 71 || type == 82 || type == 84 || type == 85) && (state == 5 || state == 0x7D)) ||
                (type == 30 && (state == 4 || state == 0x7D)) || (type == 85 && state == 0x82) || (type == 29 && state == 3) ||
                (type == 5 && ((uint)(state - 2) <= 2 || (uint)(state - 8) <= 1))))
            { reason = "The recruit is already executing a state excluded by the native fresh strategic dispatcher."; return false; }
            if (action == 0x414)
            { reason = "The tower deployment action requires its dedicated machine adapter."; return false; }
            if (!TrySelect(raw, group.Owner, order.Action, out Selection selected, out reason)) return false;
            if (!backend.IsWorldCurrent(operationWorld)) { reason = "The world changed during strategic job selection."; return false; }
            if (!backend.TryUnit(unitId, globalId, out _, out reason)) return false;
            if (!backend.TryTilePosition(selected.Task, out int taskX, out int taskY))
            { reason = "The native job target tile is no longer valid."; return false; }
            bool committed = false;
            try
            {
                if (!selected.TryReserve(unitId)) { reason = "The native job entry changed before reservation."; return false; }
                *(short*)(raw + 0x918) = (short)(action == 0x3F4 ? 0x67 : 0x65);
                *(int*)(raw + 0xA00) = selected.Task;
                *(short*)(raw + 0x95A) = (short)action;
                *(short*)(raw + 0xA40) = (short)taskX; *(short*)(raw + 0xA42) = (short)taskY;
                int positioned = backend.InitializeJobPosition(unitId);
                if (!backend.IsWorldCurrent(operationWorld)) { reason = "The world changed during strategic job positioning."; return false; }
                if (positioned == 0 && !backend.TryBuildRecruitRoute(unitId, globalId, group, selected.X, selected.Y, out reason)) return false;
                if (!selected.IsStillReservedBy(unitId) ||
                    !backend.IsStrategicCandidateCurrent(selected.RowsBase, selected.Owner, selected.Task, selected.Approach))
                { reason = "The selected native job identity/reservation changed while constructing the recruit route."; return false; }
                if (!backend.TryUnit(unitId, globalId, out _, out reason) || !backend.HasMember(group, unitId) ||
                    *(ushort*)(raw + 0x930) != group.Id)
                { if (reason.Length == 0) reason = "Recruit membership changed while initializing its native job."; return false; }
                committed = true; reason = string.Empty; return true;
            }
            finally { if (!committed && backend.IsWorldCurrent(operationWorld)) selected.Rollback(unitId); }
        }

        private readonly struct Table
        {
            internal Table(int count, int rows, int owner, int limit, int weight, int bias = 0, bool global = false, bool targetDistance = false)
            { Count = count; Rows = rows; Owner = global ? 0 : owner * 0x177BC; Limit = limit; Weight = weight; Bias = bias; TargetDistance = targetDistance; }
            internal readonly int Count, Rows, Owner, Limit, Weight, Bias;
            internal readonly bool TargetDistance;
        }
        private readonly struct Selection
        {
            internal Selection(int* row, int task, int approach, int x, int y, bool shared, int rowsBase, int owner, int costLimit)
            { Row = row; Task = task; Approach = approach; X = x; Y = y; Shared = shared; RowsBase = rowsBase; Owner = owner; CostLimit = costLimit; }
            private readonly int* Row;
            internal readonly int Task, Approach, X, Y;
            internal readonly int RowsBase, Owner;
            private readonly int CostLimit;
            private readonly bool Shared;
            internal bool Exists => Row != null;
            internal bool IsStillReservedBy(int unit) => Row != null && Row[0] == Task && Row[1] == Approach && Row[2] < CostLimit && (Shared || Row[3] == unit);
            internal bool TryReserve(int unit)
            {
                if (Row == null || Row[0] != Task || Row[1] != Approach || (!Shared && Row[3] != 0)) return false;
                if (!Shared) Row[3] = unit;
                return true;
            }
            internal void Rollback(int unit)
            { if (!Shared && Row != null && Row[0] == Task && Row[1] == Approach && Row[3] == unit) Row[3] = 0; }
        }

        private bool TrySelect(byte* unit, int owner, int action, out Selection selected, out string reason)
        {
            selected = default(Selection); reason = string.Empty;
            int best = action == 0x3F7 ? 100000 : 1000;
            Table primary = new Table(0x2EA70E4, 0x2EA70EC, owner, 9999, 8);
            Table secondary = new Table(0x2EB2CC8, 0x2EB2CD0, owner, 5999, 0, 5);
            switch (action)
            {
                case 0x3F2: if (!Scan(unit, primary, ref best, ref selected, out reason)) return false; break;
                case 0x420:
                    if (!Scan(unit, primary, ref best, ref selected, out reason) || !Scan(unit, secondary, ref best, ref selected, out reason)) return false;
                    if (!selected.Exists && !Scan(unit, new Table(0x2EAEE2C, 0x2EAEE34, owner, int.MaxValue, -2), ref best, ref selected, out reason)) return false;
                    break;
                case 0x3F3: case 0x3F4:
                    if (!Scan(unit, new Table(0x2EAAF90, 0x2EAAF98, owner, 5999, 0), ref best, ref selected, out reason)) return false; break;
                case 0x3F5:
                    if (!Scan(unit, new Table(0x2EAEE2C, 0x2EAEE34, owner, int.MaxValue, -2), ref best, ref selected, out reason)) return false; break;
                case 0x3F6:
                    if (!Scan(unit, new Table(0x2EB2CC8, 0x2EB2CD0, owner, 5999, 0), ref best, ref selected, out reason)) return false; break;
                case 0x3F7:
                    if (!Scan(unit, new Table(0x2EB6B64, 0x2EB6B6C, owner, 5999, 8), ref best, ref selected, out reason)) return false; break;
                case 0x3FB:
                    if (!Scan(unit, new Table(0x2EB9668, 0x2EB9670, owner, int.MaxValue, 0, global: true), ref best, ref selected, out reason)) return false; break;
                case 0x3FD: return SelectShared(out selected, out reason);
                case 0x413:
                    if (!Scan(unit, new Table(0x2EBAA00, 0x2EBAA08, owner, 5999, 0, targetDistance: true), ref best, ref selected, out reason)) return false; break;
                default: reason = "The current strategic action has no verified singleton initializer."; return false;
            }
            if (!selected.Exists) { reason = "No free native job is available without resetting other members' reservations."; return false; }
            return true;
        }

        private bool Scan(byte* unit, Table table, ref int best, ref Selection selected, out string reason)
        {
            reason = string.Empty;
            int count = *(int*)((byte*)backend.Module + table.Count + table.Owner);
            if (count < 0 || count > 1000) { reason = "The native strategic candidate count is outside its bounded table."; return false; }
            int* rows = (int*)((byte*)backend.Module + table.Rows + table.Owner);
            int ux = *(short*)(unit + 0x71C), uy = *(short*)(unit + 0x71E);
            for (int i = 0; i < count; i++)
            {
                int* row = rows + i * 4;
                if (row[3] != 0 || row[2] >= table.Limit || !backend.TryTilePosition(row[1], out int x, out int y) ||
                    !backend.IsStrategicCandidateCurrent(table.Rows, table.Owner == 0 ? 0 : table.Owner / 0x177BC, row[0], row[1])) continue;
                int score = Math.Max(Math.Abs(ux - x), Math.Abs(uy - y)); // 79C0 scratch[3], not its Manhattan return.
                if (table.Weight == -2) score += (100 - row[2]) * 2;
                else score += row[2] * table.Weight;
                if (table.TargetDistance)
                {
                    int targetOwner = *(int*)((byte*)backend.Module + 0x2EA70DC + table.Owner);
                    if ((uint)targetOwner > 8) { reason = "The strategic enemy-home owner is invalid."; return false; }
                    int tx = *(int*)((byte*)backend.Module + 0x379AFA8 + targetOwner * 0x583C);
                    int ty = *(int*)((byte*)backend.Module + 0x379AFAC + targetOwner * 0x583C);
                    score += Math.Max(Math.Abs(tx - x), Math.Abs(ty - y));
                }
                if ((long)score + table.Bias < best)
                { best = score; selected = new Selection(row, row[0], row[1], x, y, false, table.Rows, table.Owner / 0x177BC, table.Limit); }
            }
            return true;
        }

        private bool SelectShared(out Selection selected, out string reason)
        {
            selected = default(Selection); reason = string.Empty;
            int count = *(int*)((byte*)backend.Module + 0x2EA3780);
            if (count < 1 || count > 1000) { reason = "No bounded shared strategic target list is available."; return false; }
            int* rows = (int*)((byte*)backend.Module + 0x2EA3788);
            int* chosen = rows; bool shared = true;
            for (int i = 0; i < count; i++) if (rows[i * 4 + 3] == 0) { chosen = rows + i * 4; shared = false; break; }
            if (!backend.TryTilePosition(chosen[1], out int x, out int y)) { reason = "The shared strategic approach tile is invalid."; return false; }
            selected = new Selection(chosen, chosen[0], chosen[1], x, y, shared, 0x2EA3788, 0, int.MaxValue); return true;
        }
    }
}
