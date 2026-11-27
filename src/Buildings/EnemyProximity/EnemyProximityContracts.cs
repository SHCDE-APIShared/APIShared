using System;
using System.Threading;

namespace APIShared.Buildings
{
    /// <summary>Ordered stages of the shared enemy-unit distance calculation.</summary>
    public enum EnemyProximityPhase
    {
        /// <summary>Replace only the categories supplied by a participant.</summary>
        Override,
        /// <summary>Take the minimum of each supplied category and its current radius.</summary>
        Limit
    }

    /// <summary>Immutable inputs and current radii for one native enemy-unit proximity question.</summary>
    /// <remarks>The question does not identify building, repair or demolition, nor a complete footprint.
    /// Positions and distances are integer native map tiles. No game state has been changed.</remarks>
    public readonly struct EnemyProximityContext
    {
        internal EnemyProximityContext(int player, int x, int y, int original, byte unknown, int civilian, int military)
        { PlayerId = player; TileX = x; TileY = y; OriginalRadius = original; Unknown = unknown; CivilianRadius = civilian; MilitaryRadius = military; }
        /// <summary>One-based native player ID.</summary>
        public int PlayerId { get; }
        /// <summary>Native query tile X, not necessarily a building anchor.</summary>
        public int TileX { get; }
        /// <summary>Native query tile Y, not necessarily a building anchor.</summary>
        public int TileY { get; }
        /// <summary>Unmodified radius passed to the Script Extender question.</summary>
        public int OriginalRadius { get; }
        /// <summary>Original byte controlling Vanilla's additional exclusion.</summary>
        public byte Unknown { get; }
        /// <summary>Current civilian radius after earlier participants.</summary>
        public int CivilianRadius { get; }
        /// <summary>Current radius for all remaining Vanilla threat types.</summary>
        public int MilitaryRadius { get; }
    }

    /// <summary>One atomic contribution. Null means no category contribution; zero is a real radius.</summary>
    public readonly struct EnemyProximityDecision
    {
        /// <summary>Creates a contribution. Supplied radii must be between zero and 46340 inclusive.
        /// A negative consumer sentinel must be resolved against the incoming radius by that consumer.
        /// Any invalid value discards the entire contribution, including its block flag.</summary>
        public EnemyProximityDecision(int? civilianRadius = null, int? militaryRadius = null, bool block = false)
        { CivilianRadius = civilianRadius; MilitaryRadius = militaryRadius; Block = block; }
        /// <summary>Optional civilian radius.</summary>
        public int? CivilianRadius { get; }
        /// <summary>Optional radius for remaining Vanilla threat types.</summary>
        public int? MilitaryRadius { get; }
        /// <summary>Request a block that no later radius contribution can undo.</summary>
        public bool Block { get; }
        /// <summary>Contribute nothing.</summary>
        public static EnemyProximityDecision None => default;
        /// <summary>Block without proposing any radius.</summary>
        public static EnemyProximityDecision Blocked => new EnemyProximityDecision(block: true);
    }

    /// <summary>Process-held participant, initially inactive. No native unpatch or unregister operation exists.</summary>
    public sealed class EnemyProximityRegistration
    {
        private readonly EnemyProximityRegistry registry;
        internal readonly Func<EnemyProximityContext, EnemyProximityDecision> Callback;
        internal readonly int Order;
        internal bool Active;
        private int errors;
        internal EnemyProximityRegistration(EnemyProximityRegistry registry, string owner, string id,
            EnemyProximityPhase phase, Func<EnemyProximityContext, EnemyProximityDecision> callback, int order)
        { this.registry = registry; OwnerGuid = owner; RegistrationId = id; Phase = phase; Callback = callback; Order = order; }
        /// <summary>Stable owner GUID used for deterministic ordinal ordering.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable owner-local ID.</summary>
        public string RegistrationId { get; }
        /// <summary>Stage of this contribution.</summary>
        public EnemyProximityPhase Phase { get; }
        /// <summary>Current logical activation.</summary>
        public bool IsEnabled => registry.IsEnabled(this);
        /// <summary>Throwing or invalid contributions, saturating at one million.</summary>
        public int CallbackErrorCount => Volatile.Read(ref errors);
        /// <summary>Change logical demand. All peers must use the same authoritative settings.
        /// This does not install, remove or rewrite executable code.</summary>
        public void SetEnabled(bool enabled) => registry.SetEnabled(this, enabled);
        internal void Error()
        { int value; do { value = Volatile.Read(ref errors); if (value >= 1000000) return; } while (Interlocked.CompareExchange(ref errors, value + 1, value) != value); }
    }
}
