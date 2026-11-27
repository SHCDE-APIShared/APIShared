using System;

namespace APIShared.Units.AiMilitary.Native
{
    // A handle, never a retained native pointer. All three values are rechecked before use.
    internal readonly struct AiNativeGroup : IEquatable<AiNativeGroup>
    {
        internal AiNativeGroup(int id, uint globalId, int owner)
        { Id = id; GlobalId = globalId; Owner = owner; }
        internal int Id { get; }
        internal uint GlobalId { get; }
        internal int Owner { get; }
        public bool Equals(AiNativeGroup other) => Id == other.Id && GlobalId == other.GlobalId && Owner == other.Owner;
        public override bool Equals(object obj) => obj is AiNativeGroup other && Equals(other);
        public override int GetHashCode() => unchecked((Id * 397 ^ (int)GlobalId) * 397 ^ Owner);
    }
}
