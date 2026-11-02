using System;
using System.Collections.Generic;
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

    /// <summary>Coordinates optional category radii through the existing Script Extender proximity event.</summary>
    /// <remarks>The Script Extender retains the live native hook. This service owns only a private,
    /// read-only native predicate and one event subscription. Without active contributions Vanilla runs.
    /// It does not change enemy building, wall, Keep, terrain or occupancy checks.</remarks>
    public static partial class EnemyProximityPolicies
    {
        internal static readonly EnemyProximityRegistry Registry = new EnemyProximityRegistry();

        /// <summary>Register once after APIShared native initialization, initially inactive.</summary>
        /// <remarks>Invalid/duplicate identity or unavailable event integration returns false with a reason.
        /// Registrations survive the process, including inactive ones. Callbacks execute synchronously on
        /// the native caller's thread, outside locks, ordered by phase, order and ordinal owner/ID.
        /// Each immutable result commits atomically; a throwing or invalid participant is discarded and
        /// later participants continue. Block is sticky. Recursive questions bypass policy callbacks.
        /// Do not touch UI, send network messages or mutate game state in a callback. There is no replay
        /// or implicit thread dispatch. Existing earlier SE skips are respected; unrelated later raw-SE
        /// subscribers are outside this broker's control. Query-backend failure leaves original radii
        /// active but does not prevent a valid block decision. Check QueryDiagnostic separately.</remarks>
        public static bool TryRegister(string ownerGuid, string registrationId, EnemyProximityPhase phase,
            Func<EnemyProximityContext, EnemyProximityDecision> callback,
            out EnemyProximityRegistration registration, out string error, int order = 0)
        {
            registration = null;
            if (Volatile.Read(ref integrationReady) == 0)
            { error = "Enemy proximity event integration is not initialized."; return false; }
            return Registry.TryRegister(ownerGuid, registrationId, phase, callback, out registration, out error, order);
        }

        /// <summary>Whether this exact identity registered successfully, even if logically inactive.</summary>
        public static bool HasRegistration(string ownerGuid, string registrationId) => Registry.HasRegistration(ownerGuid, registrationId);
    }

    internal sealed class EnemyProximityRegistry
    {
        private const int MaximumRadius = 46340;
        private readonly object gate = new object();
        private readonly List<EnemyProximityRegistration> all = new List<EnemyProximityRegistration>();
        private EnemyProximityRegistration[] active = Array.Empty<EnemyProximityRegistration>();
        private EnemyProximityRegistration[] identities = Array.Empty<EnemyProximityRegistration>();
        [ThreadStatic] private static int depth;
        internal bool HasDemand => Volatile.Read(ref active).Length != 0;

        internal bool TryRegister(string owner, string id, EnemyProximityPhase phase,
            Func<EnemyProximityContext, EnemyProximityDecision> callback,
            out EnemyProximityRegistration registration, out string error, int order = 0)
        {
            registration = null; error = null;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || callback == null ||
                (phase != EnemyProximityPhase.Override && phase != EnemyProximityPhase.Limit))
            { error = "Invalid enemy proximity registration."; return false; }
            lock (gate)
            {
                foreach (var item in all)
                    if (item.OwnerGuid == owner && item.RegistrationId == id)
                    { error = "Duplicate enemy proximity registration: " + owner + "/" + id; return false; }
                registration = new EnemyProximityRegistration(this, owner, id, phase, callback, order);
                all.Add(registration);
                Volatile.Write(ref identities, all.ToArray());
                return true;
            }
        }
        internal bool HasRegistration(string owner, string id)
        {
            foreach (var item in Volatile.Read(ref identities))
                if (item.OwnerGuid == owner && item.RegistrationId == id) return true;
            return false;
        }
        internal bool IsEnabled(EnemyProximityRegistration item) { lock (gate) return item.Active; }
        internal void SetEnabled(EnemyProximityRegistration item, bool enabled)
        {
            lock (gate)
            {
                if (item.Active == enabled) return;
                item.Active = enabled;
                var enabledItems = all.FindAll(entry => entry.Active);
                enabledItems.Sort((x, y) => {
                    int c = x.Phase.CompareTo(y.Phase); if (c != 0) return c;
                    c = x.Order.CompareTo(y.Order); if (c != 0) return c;
                    c = StringComparer.Ordinal.Compare(x.OwnerGuid, y.OwnerGuid);
                    return c != 0 ? c : StringComparer.Ordinal.Compare(x.RegistrationId, y.RegistrationId);
                });
                Volatile.Write(ref active, enabledItems.ToArray());
            }
        }
        internal EnemyProximityDecision Evaluate(int player, int x, int y, int original, byte unknown)
        {
            var snapshot = Volatile.Read(ref active);
            if (depth != 0 || snapshot.Length == 0 || player < 1 || player > 8) return default;
            int civilian = original, military = original;
            bool blocked = false, radiusAllowed = ValidRadius(original);
            depth++;
            try
            {
                foreach (var item in snapshot)
                {
                    EnemyProximityDecision result;
                    try { result = item.Callback(new EnemyProximityContext(player, x, y, original, unknown, civilian, military)); }
                    catch { item.Error(); continue; }
                    if ((result.CivilianRadius.HasValue && !ValidRadius(result.CivilianRadius.Value)) ||
                        (result.MilitaryRadius.HasValue && !ValidRadius(result.MilitaryRadius.Value)))
                    { item.Error(); continue; }
                    blocked |= result.Block;
                    if (!radiusAllowed) continue;
                    if (result.CivilianRadius.HasValue)
                        civilian = item.Phase == EnemyProximityPhase.Override ? result.CivilianRadius.Value : Math.Min(civilian, result.CivilianRadius.Value);
                    if (result.MilitaryRadius.HasValue)
                        military = item.Phase == EnemyProximityPhase.Override ? result.MilitaryRadius.Value : Math.Min(military, result.MilitaryRadius.Value);
                }
                return new EnemyProximityDecision(civilian != original ? (int?)civilian : null,
                    military != original ? (int?)military : null, blocked);
            }
            finally { depth--; }
        }
        private static bool ValidRadius(int radius) => radius >= 0 && radius <= MaximumRadius;
    }
}
