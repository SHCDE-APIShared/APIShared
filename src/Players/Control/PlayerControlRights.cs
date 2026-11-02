using System;

namespace APIShared.PlayerControl
{
    /// <summary>Explicit delegated rights. No right changes an object's owner or grants report visibility.</summary>
    [Flags]
    public enum PlayerControlRights
    {
        /// <summary>No foreign control.</summary>
        None = 0,
        /// <summary>Movement, combat, stop, stance and other non-destructive unit orders.</summary>
        UnitOrders = 1,
        /// <summary>Building interaction, recruitment, production, repair and gate/bridge operation, at the owner's normal cost.</summary>
        BuildingActions = 2,
        /// <summary>Player-wide economy actions, including tax, rations and trade.</summary>
        PlayerEconomy = 4,
        /// <summary>Ordinary paid construction for the object owner, separate from editor/test placement.</summary>
        Construction = 8,
        /// <summary>Additional permission for disbanding units and demolishing buildings; the corresponding object right is also required.</summary>
        DestructiveActions = 16,
        /// <summary>Every currently defined control right.</summary>
        All = UnitOrders | BuildingActions | PlayerEconomy | Construction | DestructiveActions
    }

    /// <summary>Which authorized owners participate in a local input context; never changes the real local player.</summary>
    public enum PlayerControlSelectionMode
    {
        /// <summary>Only the one requested owner participates, as used by a spectator's player selector.</summary>
        ExclusivePlayer,
        /// <summary>Own objects and explicitly authorized foreign owners may share one selection.</summary>
        AdditionalOwners
    }

    /// <summary>The native object table used by a stable object identity.</summary>
    public enum PlayerControlObjectKind
    {
        /// <summary>A unit slot.</summary>
        Unit,
        /// <summary>A building slot.</summary>
        Building
    }

    /// <summary>One captured object identity. Slots and owner IDs are one-based; GlobalId distinguishes recycled slots.</summary>
    public readonly struct PlayerControlObject : IEquatable<PlayerControlObject>
    {
        /// <summary>Creates an immutable identity; live existence and ownership are checked again at execution.</summary>
        public PlayerControlObject(PlayerControlObjectKind kind, int slot, uint globalId, int ownerPlayerId)
        {
            if (kind != PlayerControlObjectKind.Unit && kind != PlayerControlObjectKind.Building) throw new ArgumentOutOfRangeException(nameof(kind));
            if (slot <= 0) throw new ArgumentOutOfRangeException(nameof(slot));
            if (globalId == 0) throw new ArgumentOutOfRangeException(nameof(globalId));
            if (ownerPlayerId < 1 || ownerPlayerId > 8) throw new ArgumentOutOfRangeException(nameof(ownerPlayerId));
            Kind = kind; Slot = slot; GlobalId = globalId; OwnerPlayerId = ownerPlayerId;
        }
        /// <summary>Native table.</summary>
        public PlayerControlObjectKind Kind { get; }
        /// <summary>Positive native table slot.</summary>
        public int Slot { get; }
        /// <summary>Captured stable global ID.</summary>
        public uint GlobalId { get; }
        /// <summary>Captured owner, from 1 through 8.</summary>
        public int OwnerPlayerId { get; }
        /// <summary>Compares the complete identity, including owner and table.</summary>
        public bool Equals(PlayerControlObject other) => Kind == other.Kind && Slot == other.Slot && GlobalId == other.GlobalId && OwnerPlayerId == other.OwnerPlayerId;
        /// <summary>Compares a boxed complete identity.</summary>
        public override bool Equals(object obj) => obj is PlayerControlObject other && Equals(other);
        /// <summary>Returns a hash of the complete identity.</summary>
        public override int GetHashCode() => unchecked((((int)Kind * 397 ^ Slot) * 397 ^ (int)GlobalId) * 397 ^ OwnerPlayerId);
    }
}
