using APIShared.Units.AiMilitary.Native;

namespace APIShared.Units.AiMilitary.Orders
{
    internal sealed unsafe class AiMoveRecruitInitializer : IAiRecruitOrderHandler
    {
        private readonly AiMilitaryNativeBackend backend;
        internal AiMoveRecruitInitializer(AiMilitaryNativeBackend backend, AiMilitaryNativeBindings bindings) { this.backend = backend; }
        public bool Handles(AiOrderKind kind) => kind == AiOrderKind.Move || kind == AiOrderKind.Hold || kind == AiOrderKind.Patrol || kind == AiOrderKind.Stop || kind == AiOrderKind.DigMoat;
        public bool TryInitialize(int unitId, uint globalId, AiNativeGroup group, AiCurrentOrder order, out string reason)
        {
            if (order.Kind == AiOrderKind.Stop) return backend.StopRecruit(unitId, globalId, out reason);
            if (order.Kind == AiOrderKind.DigMoat && (order.Command != 6 ||
                !backend.TryUnit(unitId, globalId, out var digger, out reason) || !AiMilitaryCatalog.CanDig(digger->r_UnitChimp)))
            { reason = "This recruit/order has no native dig-moat member branch."; return false; }
            byte* raw = backend.NativeRawUnit(unitId);
            if (*(short*)(raw + 0x8F6) != 0 || raw[0xA6B] != 0 || *(short*)(raw + 0x918) == 0x6A ||
                *(short*)(raw + 0xA9E) == 0 || *(short*)(raw + 0x6EC) == 1)
            { reason = "The recruit is not eligible for the native movement member initializer."; return false; }
            *(short*)(raw + 0x934) = (short)order.X; *(short*)(raw + 0x936) = (short)order.Y;
            *(short*)(raw + 0x918) = 0x65; *(short*)(raw + 0x9F4) = 3;
            *(int*)(raw + 0x748) = 0; *(short*)(raw + 0x74C) = 0; *(short*)(raw + 0x8E6) = 0;
            if (!backend.TryBuildRecruitRoute(unitId, globalId, group, order.X, order.Y, out reason)) return false;
            if (order.Kind == AiOrderKind.DigMoat)
            {
                // 11E960 case6, after its movement half. No group-wide command or
                // work-list re-selection is issued for existing diggers.
                *(short*)(raw + 0x9F4) = 6;
                *(short*)(raw + 0xA40) = (short)order.X; *(short*)(raw + 0xA42) = (short)order.Y;
                *(short*)(raw + 0x968) = (short)order.X; *(short*)(raw + 0x96A) = (short)order.Y;
                *(int*)(raw + 0xA00) = 0; *(short*)(raw + 0x99E) = 0;
            }
            return true;
        }
    }
}
