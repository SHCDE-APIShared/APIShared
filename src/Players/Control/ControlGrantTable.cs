using System;
using System.Collections.Generic;

namespace APIShared.PlayerControl
{
    // Pure simulation-state registry. Network callers must supply the captured native
    // Chore issuer; a claimed player ID from a packet must never be used as that argument.
    internal sealed class ControlGrantTable
    {
        private readonly Dictionary<GrantKey, Grant> grants = new Dictionary<GrantKey, Grant>();
        private long epoch;
        private int host;
        private bool multiplayer;

        private readonly struct GrantKey : IEquatable<GrantKey>
        {
            private readonly string registration;
            private readonly int controller;
            internal GrantKey(string registration, int controller) { this.registration = registration; this.controller = controller; }
            public bool Equals(GrantKey other) => controller == other.controller && StringComparer.Ordinal.Equals(registration, other.registration);
            public override bool Equals(object obj) => obj is GrantKey other && Equals(other);
            public override int GetHashCode() => unchecked(StringComparer.Ordinal.GetHashCode(registration) * 397 ^ controller);
        }

        private readonly struct Grant
        {
            internal readonly int Controller, Owners;
            internal readonly PlayerControlRights Rights;
            internal readonly long Revision;
            internal Grant(int controller, int owners, PlayerControlRights rights, long revision)
            { Controller = controller; Owners = owners; Rights = rights; Revision = revision; }
        }

        internal void Reset(long newEpoch, int hostPlayerId, bool realMultiplayer = true)
        {
            if (newEpoch <= 0) throw new ArgumentOutOfRangeException(nameof(newEpoch));
            if (hostPlayerId < (realMultiplayer ? 1 : 0) || hostPlayerId > 8) throw new ArgumentOutOfRangeException(nameof(hostPlayerId));
            grants.Clear(); epoch = newEpoch; host = hostPlayerId; multiplayer = realMultiplayer;
        }

        internal void Clear() { grants.Clear(); epoch = 0; host = 0; }

        internal bool Apply(long expectedEpoch, int nativeIssuer, string registration, int controller,
            int ownerMask, PlayerControlRights rights, long revision)
        {
            if (epoch <= 0 || expectedEpoch != epoch || nativeIssuer != host || string.IsNullOrWhiteSpace(registration) ||
                controller < (multiplayer ? 1 : 0) || controller > 8 || ownerMask < 0 || (ownerMask & ~0x1fe) != 0 ||
                (rights & ~PlayerControlRights.All) != 0 || revision <= 0) return false;
            // Controller zero is the local, non-network spectator identity. The network
            // adapter rejects it in multiplayer; keeping it distinct avoids inventing player 1.
            var key = new GrantKey(registration, controller);
            if (grants.TryGetValue(key, out var previous) && revision <= previous.Revision) return false;
            grants[key] = new Grant(controller, ownerMask, rights, revision);
            return true;
        }

        internal bool Allows(long expectedEpoch, string registration, long revision, int nativeIssuer,
            int objectOwner, PlayerControlRights required)
        {
            if (epoch <= 0 || epoch != expectedEpoch || objectOwner < 1 || objectOwner > 8 ||
                nativeIssuer < (multiplayer ? 1 : 0) || nativeIssuer > 8 || required == PlayerControlRights.None ||
                (required & ~PlayerControlRights.All) != 0 || registration == null ||
                !grants.TryGetValue(new GrantKey(registration, nativeIssuer), out var grant)) return false;
            return grant.Revision == revision && grant.Controller == nativeIssuer &&
                (grant.Owners & (1 << objectOwner)) != 0 && (grant.Rights & required) == required;
        }

        internal bool HasCurrentGrant(long expectedEpoch, string registration, long revision, int nativeIssuer) =>
            epoch > 0 && epoch == expectedEpoch && registration != null &&
            nativeIssuer >= (multiplayer ? 1 : 0) && nativeIssuer <= 8 &&
            grants.TryGetValue(new GrantKey(registration, nativeIssuer), out var grant) &&
            grant.Revision == revision && grant.Owners != 0 && grant.Rights != PlayerControlRights.None;
    }
}
