using System;
using System.Collections.Generic;

namespace APIShared.Pathfinding
{
    /// <summary>What a tile rule asks route engines to do with routes into one tile.</summary>
    public enum RouteTileEffect
    {
        /// <summary>No contribution.</summary>
        None = 0,
        /// <summary>Prefer other routes: entering the tile costs extra (see <see cref="RouteTileRuleSet.Set"/>).</summary>
        Penalty = 1,
        /// <summary>Do not route into the tile. A unit already on it may still leave it.</summary>
        Forbidden = 2
    }

    /// <summary>
    /// Passive, process-lived tile rules for route engines (for example fire, hazards or reserved areas).
    /// A mod registers one rule set per owner GUID and registration ID and then sets or clears tile effects.
    /// Engines that support the contract read the combined effect and the change journal; without such an
    /// engine the rules have no effect. Rules can only restrict or penalize: they never permit a transition
    /// the game forbids, never change unit speed and never affect Vanilla's own route searches or commands.
    /// </summary>
    /// <remarks>
    /// Thread and determinism: every call belongs on the game's simulation thread. Rule data must come from
    /// deterministic game state, because engines apply rules to gameplay routes; consumers keep multiplayer
    /// peers on identical rule mods and settings themselves. Calls never install hooks, scan the map or call
    /// into the game. Coordinates are tile coordinates within 0..MapSize-1; player IDs are one-based (1..8).
    /// Combination over all sets: Forbidden for a player wins; otherwise the largest penalty applies.
    /// </remarks>
    public static class RouteTileRules
    {
        /// <summary>Largest penalty per tile, in ground steps of the searching unit.</summary>
        public const int MaximumPenalty = 255;
        /// <summary>Largest supported map edge; coordinates outside 0..MapSize-1 are rejected.</summary>
        public const int MapSize = 800;
        /// <summary>Changes the journal keeps; an engine that falls further behind re-reads every rule.</summary>
        public const int JournalCapacity = 65536;

        private static readonly object Sync = new object();
        private static RouteTileRuleSet[] sets = Array.Empty<RouteTileRuleSet>();
        private static readonly int[] journal = new int[JournalCapacity];
        private static long sequence;

        /// <summary>Whether any rule set currently holds a tile effect.</summary>
        public static bool HasRules { get; private set; }

        /// <summary>Whether any rule set with a <see cref="RouteTileRuleSet.ReplanPolicy"/> currently holds a tile effect.</summary>
        public static bool HasReplanRules { get; private set; }

        /// <summary>Monotone count of effective tile changes, registrations and clears; the journal position.</summary>
        public static long Version => sequence;

        /// <summary>Registers one process-lived rule set; a second registration of the same identity fails.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, out RouteTileRuleSet rules, out string reason)
        {
            rules = null;
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId)) { reason = "Owner and registration ID required"; return false; }
            lock (Sync)
            {
                foreach (RouteTileRuleSet set in sets)
                    if (set.OwnerGuid == ownerGuid && set.RegistrationId == registrationId) { reason = "Registration already exists"; return false; }
                var next = new RouteTileRuleSet[sets.Length + 1];
                Array.Copy(sets, next, sets.Length);
                next[sets.Length] = rules = new RouteTileRuleSet(ownerGuid, registrationId);
                Array.Sort(next, (a, b) =>
                {
                    int c = StringComparer.Ordinal.Compare(a.OwnerGuid, b.OwnerGuid);
                    return c != 0 ? c : StringComparer.Ordinal.Compare(a.RegistrationId, b.RegistrationId);
                });
                sets = next;
            }
            reason = null;
            return true;
        }

        /// <summary>The combined effect of all rule sets for one player entering one tile.</summary>
        /// <param name="tileX">Tile X coordinate.</param>
        /// <param name="tileY">Tile Y coordinate.</param>
        /// <param name="playerId">One-based player ID (1..8).</param>
        /// <param name="penalty">The largest penalty when the result is <see cref="RouteTileEffect.Penalty"/>, else 0.</param>
        public static RouteTileEffect GetEffect(int tileX, int tileY, int playerId, out int penalty)
        {
            penalty = 0;
            if (!HasRules || !ValidTile(tileX, tileY) || playerId < 1 || playerId > 8) return RouteTileEffect.None;
            int key = Key(tileX, tileY); uint bit = 1U << playerId;
            RouteTileEffect effect = RouteTileEffect.None;
            foreach (RouteTileRuleSet set in sets)
            {
                if (!set.TryGet(key, out RouteTileRuleSet.Entry entry) || (entry.Players & bit) == 0) continue;
                if (entry.Effect == RouteTileEffect.Forbidden) { penalty = 0; return RouteTileEffect.Forbidden; }
                if (entry.Penalty > penalty) { penalty = entry.Penalty; effect = RouteTileEffect.Penalty; }
            }
            return effect;
        }

        /// <summary>
        /// The replan policy for one player's unit whose remaining route enters one tile: the policy of the
        /// first rule set (ordinal owner GUID, then registration ID) that has a policy and an effect on the
        /// tile for that player; null when no such set exists.
        /// </summary>
        public static RouteReplanPolicy GetReplanPolicy(int tileX, int tileY, int playerId)
        {
            if (!HasReplanRules || !ValidTile(tileX, tileY) || playerId < 1 || playerId > 8) return null;
            int key = Key(tileX, tileY); uint bit = 1U << playerId;
            foreach (RouteTileRuleSet set in sets)
            {
                RouteReplanPolicy policy = set.ReplanPolicy;
                if (policy != null && set.TryGet(key, out RouteTileRuleSet.Entry entry) && (entry.Players & bit) != 0) return policy;
            }
            return null;
        }

        /// <summary>Copies the tiles changed since <paramref name="cursor"/> and advances it.</summary>
        /// <param name="cursor">The engine's journal position; start with <see cref="Version"/> after a full read.</param>
        /// <param name="tiles">Receives changed tiles packed as <c>y * MapSize + x</c>; a tile may repeat.</param>
        /// <returns>The number of tiles written; -1 when changes were lost (fell behind the journal or the
        /// buffer is too small for all pending changes): re-read every rule and set the cursor to <see cref="Version"/>.</returns>
        public static int ReadChanges(ref long cursor, int[] tiles)
        {
            if (tiles == null) throw new ArgumentNullException(nameof(tiles));
            long end = sequence;
            if (cursor < 0 || cursor > end || end - cursor > JournalCapacity || end - cursor > tiles.Length) { cursor = end; return -1; }
            int count = 0;
            for (long i = cursor; i < end; i++) tiles[count++] = journal[i % JournalCapacity];
            cursor = end;
            return count;
        }

        internal static void Changed(int key)
        {
            journal[sequence % JournalCapacity] = key;
            sequence++;
            bool any = false, replan = false;
            foreach (RouteTileRuleSet set in sets)
                if (set.Count != 0) { any = true; if (set.ReplanPolicy != null) replan = true; }
            HasRules = any; HasReplanRules = replan;
        }

        internal static bool ValidTile(int tileX, int tileY) => (uint)tileX < MapSize && (uint)tileY < MapSize;
        internal static int Key(int tileX, int tileY) => tileY * MapSize + tileX;
    }

    /// <summary>One mod's tile effects (see <see cref="RouteTileRules"/>). Simulation thread only.</summary>
    public sealed class RouteTileRuleSet
    {
        /// <summary>Player mask of all eight players (bits 1..8).</summary>
        public const uint AllPlayers = 0x1FE;

        internal struct Entry { internal RouteTileEffect Effect; internal int Penalty; internal uint Players; }
        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();

        internal RouteTileRuleSet(string ownerGuid, string registrationId) { OwnerGuid = ownerGuid; RegistrationId = registrationId; }

        /// <summary>The registering mod's GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>The owner-local registration ID.</summary>
        public string RegistrationId { get; }
        /// <summary>Number of tiles with an effect in this set.</summary>
        public int Count => entries.Count;

        private RouteReplanPolicy replanPolicy;
        /// <summary>
        /// Optional rule for units whose already planned route meets this set's tiles later (for example
        /// fire that starts after a move command); null (default) leaves such routes unchanged. Engines that
        /// support it watch remaining routes and re-plan or stop units as the policy says. Changing it
        /// journals every tile of this set, so engines re-read their effects.
        /// </summary>
        public RouteReplanPolicy ReplanPolicy
        {
            get => replanPolicy;
            set
            {
                if (ReferenceEquals(replanPolicy, value)) return;
                replanPolicy = value;
                var keys = new List<int>(entries.Keys);
                keys.Sort();
                foreach (int key in keys) RouteTileRules.Changed(key);
            }
        }

        /// <summary>Sets this set's effect for one tile; <see cref="RouteTileEffect.None"/> clears it.</summary>
        /// <param name="tileX">Tile X coordinate.</param>
        /// <param name="tileY">Tile Y coordinate.</param>
        /// <param name="effect">The effect.</param>
        /// <param name="penalty">1..<see cref="RouteTileRules.MaximumPenalty"/> ground steps for <see cref="RouteTileEffect.Penalty"/>; ignored otherwise.</param>
        /// <param name="players">Affected players as a bit mask (bit p = player p); <see cref="AllPlayers"/> by default.</param>
        /// <returns>False for invalid arguments; nothing changes then.</returns>
        public bool Set(int tileX, int tileY, RouteTileEffect effect, int penalty = 0, uint players = AllPlayers)
        {
            if (!RouteTileRules.ValidTile(tileX, tileY) || (players & ~AllPlayers) != 0 ||
                effect < RouteTileEffect.None || effect > RouteTileEffect.Forbidden ||
                (effect == RouteTileEffect.Penalty && (penalty < 1 || penalty > RouteTileRules.MaximumPenalty))) return false;
            int key = RouteTileRules.Key(tileX, tileY);
            if (effect == RouteTileEffect.None || players == 0)
            {
                if (entries.Remove(key)) RouteTileRules.Changed(key);
                return true;
            }
            var entry = new Entry { Effect = effect, Penalty = effect == RouteTileEffect.Penalty ? penalty : 0, Players = players };
            if (entries.TryGetValue(key, out Entry old) && old.Effect == entry.Effect && old.Penalty == entry.Penalty && old.Players == entry.Players) return true;
            entries[key] = entry;
            RouteTileRules.Changed(key);
            return true;
        }

        /// <summary>Removes this set's effect for one tile.</summary>
        public bool Clear(int tileX, int tileY) => Set(tileX, tileY, RouteTileEffect.None);

        /// <summary>Removes every effect of this set (for example at a map change).</summary>
        public void ClearAll()
        {
            if (entries.Count == 0) return;
            var keys = new List<int>(entries.Keys);
            keys.Sort();
            entries.Clear();
            foreach (int key in keys) RouteTileRules.Changed(key);
        }

        internal bool TryGet(int key, out Entry entry) => entries.TryGetValue(key, out entry);
    }

    /// <summary>
    /// Replan rule of one <see cref="RouteTileRuleSet"/> (see <see cref="RouteTileRuleSet.ReplanPolicy"/>): what an
    /// engine does with a unit that is already moving when one of the set's tiles appears on its remaining
    /// route. Immutable.
    /// </summary>
    /// <remarks>
    /// Once the unit is at most <see cref="TriggerDistance"/> steps before such a tile, the engine plans a new
    /// route to the unit's unchanged move target that avoids the rule tiles. It adopts that route when it is
    /// at most <see cref="MaximumDetourPercent"/> percent, or at least up to <see cref="MinimumDetourTiles"/>
    /// steps, longer than the remaining route. Otherwise the unit stops on its next tile and continues to the
    /// same target once a route without such tiles exists or after <see cref="MaximumWaitTicks"/> simulation
    /// ticks (0: no stop, the unit keeps its route). The unit's order, target and state stay unchanged; a new
    /// order ends the wait. Engines decide on simulation ticks only, so peers with identical rules agree.
    /// Engines may check units in intervals, share one decision among members of one group command and
    /// skip units whose route they cannot read; a policy is a request, not a guarantee.
    /// </remarks>
    public sealed class RouteReplanPolicy
    {
        /// <summary>Largest <see cref="TriggerDistance"/>.</summary>
        public const int MaximumTriggerDistance = 16;

        /// <summary>Creates a policy; out-of-range arguments throw <see cref="ArgumentOutOfRangeException"/>.</summary>
        /// <param name="triggerDistance">1..<see cref="MaximumTriggerDistance"/> route steps before a rule tile.</param>
        /// <param name="maximumDetourPercent">0..1000 percent of the remaining route length.</param>
        /// <param name="minimumDetourTiles">0..2000 extra steps that are always allowed.</param>
        /// <param name="maximumWaitTicks">0..72000 simulation ticks (about 40 per second at normal speed).</param>
        public RouteReplanPolicy(int triggerDistance, int maximumDetourPercent, int minimumDetourTiles, int maximumWaitTicks)
        {
            if (triggerDistance < 1 || triggerDistance > MaximumTriggerDistance) throw new ArgumentOutOfRangeException(nameof(triggerDistance));
            if (maximumDetourPercent < 0 || maximumDetourPercent > 1000) throw new ArgumentOutOfRangeException(nameof(maximumDetourPercent));
            if (minimumDetourTiles < 0 || minimumDetourTiles > 2000) throw new ArgumentOutOfRangeException(nameof(minimumDetourTiles));
            if (maximumWaitTicks < 0 || maximumWaitTicks > 72000) throw new ArgumentOutOfRangeException(nameof(maximumWaitTicks));
            TriggerDistance = triggerDistance; MaximumDetourPercent = maximumDetourPercent;
            MinimumDetourTiles = minimumDetourTiles; MaximumWaitTicks = maximumWaitTicks;
        }

        /// <summary>Route steps before a rule tile at which the engine decides.</summary>
        public int TriggerDistance { get; }
        /// <summary>Allowed extra length of a new route, in percent of the remaining route.</summary>
        public int MaximumDetourPercent { get; }
        /// <summary>Extra steps a new route may always add, also for short remaining routes.</summary>
        public int MinimumDetourTiles { get; }
        /// <summary>Longest stop without an allowed route, in simulation ticks; 0 keeps the route instead.</summary>
        public int MaximumWaitTicks { get; }

        /// <summary>Whether a new route of <paramref name="replanned"/> steps may replace <paramref name="remaining"/> steps.</summary>
        public bool AllowsDetour(int remaining, int replanned)
        {
            if (remaining < 0 || replanned < 0) return false;
            long allowed = Math.Max((long)remaining * MaximumDetourPercent / 100, MinimumDetourTiles);
            return replanned <= remaining + allowed;
        }
    }
}
