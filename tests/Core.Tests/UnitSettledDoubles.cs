// Test doubles for the external SDK boundary: unit type values match SHCDESE.Interop.eChimps.
namespace SHCDESE.Interop
{
    public enum eChimps : ushort
    {
        CHIMP_TYPE_NULL = 0, CHIMP_TYPE_PEASANT = 1, CHIMP_TYPE_WOODCUTTER = 3,
        CHIMP_TYPE_ARCHER = 22, CHIMP_TYPE_MACEMAN = 26, CHIMP_TYPE_SWORDSMAN = 27,
    }
}

namespace APIShared.Core.Tests
{
    using APIShared.Units;
    using SHCDESE.Interop;

    // Mutable slot table standing in for the Script Extender unit array; lookups do not allocate.
    internal sealed class FakeUnitSlots : IUnitSettleReader
    {
        private readonly Dictionary<int, UnitSettleProbe> slots = new Dictionary<int, UnitSettleProbe>(256);
        internal int Reads;

        public UnitSettleProbe Read(int unitId)
        {
            Reads++;
            return slots.TryGetValue(unitId, out UnitSettleProbe unit) ? unit : default;
        }

        internal void Alive(int unitId, int globalId, int playerId, eChimps type) =>
            slots[unitId] = new UnitSettleProbe { Found = true, Alive = true, GlobalId = globalId, PlayerId = playerId, UnitType = type };

        // NeedsInit and recruitment's transient AliveState 4: neither alive nor dead.
        internal void Transient(int unitId, int globalId, int playerId, eChimps type) =>
            slots[unitId] = new UnitSettleProbe { Found = true, GlobalId = globalId, PlayerId = playerId, UnitType = type };

        internal void Dead(int unitId) => slots[unitId] = new UnitSettleProbe { Found = true, Dead = true };

        internal void Remove(int unitId) => slots.Remove(unitId);
    }
}
