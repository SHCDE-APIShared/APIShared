using APIShared.Units.AiMilitary.Native;

namespace APIShared.Units.AiMilitary.Orders
{
    internal enum AiOrderKind
    {
        // Persisted AIO1 domain values; additions must not renumber existing kinds.
        Unknown = 0, Idle = 1, Move = 2, Hold = 3, Patrol = 4, Strategic = 5,
        AttackUnit = 6, AttackBuilding = 7, DigMoat = 8, Stop = 9, AttackTile = 10
    }

    // Effective accepted arguments. Neither the original SE Post args nor another unit's
    // command/path fields are a substitute for these values.
    internal readonly struct AiCurrentOrder
    {
        internal AiCurrentOrder(AiOrderKind kind, int x = 0, int y = 0, int command = 0,
            int targetId = 0, uint targetGlobalId = 0, int action = 0, int extra = 0, int moveType = 0)
        { Kind = kind; X = x; Y = y; Command = command; TargetId = targetId;
          TargetGlobalId = targetGlobalId; Action = action; Extra = extra; MoveType = moveType; }
        internal AiOrderKind Kind { get; }
        internal int X { get; }
        internal int Y { get; }
        internal int Command { get; }
        internal int TargetId { get; }
        internal uint TargetGlobalId { get; }
        internal int Action { get; }
        internal int Extra { get; }
        internal int MoveType { get; }
    }

    // Called only at the core's quiescent simulation point. A failed handler must not
    // modify old members; the backend restores the recruit and membership on failure.
    internal interface IAiRecruitOrderHandler
    {
        bool Handles(AiOrderKind kind);
        bool TryInitialize(int unitId, uint globalId, AiNativeGroup group, AiCurrentOrder order, out string reason);
    }
}
