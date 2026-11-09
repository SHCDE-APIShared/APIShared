using System;
using System.Collections.Generic;

namespace APIShared.Wildlife
{
    /// <summary>Kinds of animal herds, with Vanilla's own record values (the herd record's kind word).</summary>
    /// <remarks>
    /// The Script Extender has no symbol for these values. They are the values Vanilla's herd spawn
    /// (RVA 0x11E150) writes and its herd dispatcher (0x117520) selects the update by, for the native image
    /// named in docs/HERDS.md. Other tribe kinds (troop groups) are not herds.
    /// </remarks>
    public enum HerdKind : short
    {
        /// <summary>Deer, 9 to 12 animals when Vanilla spawns the herd.</summary>
        Deer = 0xD,
        /// <summary>Lions, 3 to 4 animals.</summary>
        Lion = 0xE,
        /// <summary>Rabbits, 14 to 21 animals.</summary>
        Rabbit = 0xF,
        /// <summary>Camels, 2 to 5 animals.</summary>
        Camel = 0x10,
        /// <summary>Goats, 4 to 5 animals.</summary>
        Goat = 0x28,
        /// <summary>Hyenas, 6 to 9 animals.</summary>
        Hyena = 0x29,
        /// <summary>One crocodile.</summary>
        Crocodile = 0x2A
    }

    /// <summary>A snapshot of one animal herd record.</summary>
    public readonly struct HerdInfo
    {
        internal HerdInfo(int herdId, uint globalId, HerdKind kind, int ownerPlayerId, int leaderUnitId, int animalCount, bool isFleeing)
        {
            HerdId = herdId; GlobalId = globalId; Kind = kind; OwnerPlayerId = ownerPlayerId;
            LeaderUnitId = leaderUnitId; AnimalCount = animalCount; IsFleeing = isFleeing;
        }

        /// <summary>The herd's tribe ID (1 through <see cref="Herds.MaximumHerdId"/>), as in GameUnit.r_TribeId.</summary>
        public int HerdId { get; }
        /// <summary>The record's global ID. A new herd that reuses the record gets a new one.</summary>
        public uint GlobalId { get; }
        /// <summary>The kind of animals.</summary>
        public HerdKind Kind { get; }
        /// <summary>The owning player, 0 for wild herds.</summary>
        public int OwnerPlayerId { get; }
        /// <summary>The one-based unit ID of the leader, 0 when the herd has none.</summary>
        public int LeaderUnitId { get; }
        /// <summary>Vanilla's member count. Corpses stay members until Vanilla removes them.</summary>
        public int AnimalCount { get; }
        /// <summary>Whether a deer, goat or rabbit herd is fleeing; always false for other kinds.</summary>
        public bool IsFleeing { get; }
    }

    /// <summary>Reads Vanilla's animal herds and spawns new ones.</summary>
    /// <remarks>
    /// Available after APIShared's native initialization on the audited native image (see
    /// <see cref="IsAvailable"/>). Herds live in Vanilla's tribe records; a map holds all of its herds there
    /// from its first tick. Read on the simulation thread (a <see cref="Simulation.SimulationTickEvents"/>
    /// callback or a <see cref="HerdRouteEvents"/> handler) or while the simulation is stopped; the reads take
    /// no engine lock. Outside a mission the records belong to no map and must not be interpreted.
    /// </remarks>
    public static class Herds
    {
        /// <summary>The highest herd ID. ID 0 is Vanilla's reserved slot.</summary>
        public const int MaximumHerdId = HerdNativeAssembly.HerdCapacity - 1;

        /// <summary>Whether herd reading and spawning are available.</summary>
        public static bool IsAvailable => HerdRuntime.IsAvailable;

        /// <summary>Why the herd service is unavailable; empty when it is available.</summary>
        public static string UnavailableReason => HerdRuntime.UnavailableReason;

        /// <summary>Reads an animal herd record that is in use.</summary>
        /// <param name="herdId">The herd's tribe ID.</param>
        /// <param name="herd">The snapshot on success.</param>
        /// <returns>False for an invalid ID, a free record, a troop group or an unavailable service.</returns>
        public static bool TryGet(int herdId, out HerdInfo herd) => HerdRuntime.TryRead(herdId, out herd);

        /// <summary>Copies the unit IDs of a herd's members, in ascending order, into a list the caller owns.</summary>
        /// <remarks>Clears the list first. Members include corpses and units marked for deletion until Vanilla's
        /// herd maintenance (RVA 0x1261F0, every tick) removes them; check each unit's state.</remarks>
        /// <param name="herdId">The herd's tribe ID.</param>
        /// <param name="unitIds">Receives one-based unit IDs.</param>
        /// <returns>False when <see cref="TryGet"/> would return false; the list is then empty.</returns>
        public static bool TryCopyMemberUnitIds(int herdId, List<int> unitIds) => HerdRuntime.TryCopyMembers(herdId, unitIds);

        /// <summary>Spawns a herd the way Vanilla does and keeps at most <paramref name="maxAnimals"/> of it.</summary>
        /// <remarks>
        /// Call only inside a <see cref="Simulation.SimulationTickEvents"/> callback. Vanilla's spawn (RVA 0x11E150)
        /// draws the herd size and the animals' looks from the game's random numbers, so multiplayer peers must make
        /// the same calls in the same tick and order. Vanilla refuses blocked tiles, water and tiles outside the map.
        /// Animals above <paramref name="maxAnimals"/> are marked for deletion in the same tick, Vanilla's own path
        /// for animals over its limit; Vanilla's herd maintenance then removes them from the herd and picks a leader
        /// among the rest. The new herd is wild (owner 0) and grows, wanders and flees by Vanilla's rules.
        /// </remarks>
        /// <param name="kind">The kind of herd.</param>
        /// <param name="tileX">Tile X of the herd's place.</param>
        /// <param name="tileY">Tile Y of the herd's place.</param>
        /// <param name="maxAnimals">At least 1; Vanilla's own size when it is larger than that.</param>
        /// <param name="herdId">The new herd's ID on success, otherwise 0.</param>
        /// <param name="reason">Empty on success; otherwise why no herd was spawned.</param>
        /// <returns>Whether a herd was spawned.</returns>
        public static bool TrySpawn(HerdKind kind, int tileX, int tileY, int maxAnimals, out int herdId, out string reason) =>
            HerdRuntime.TrySpawn(kind, tileX, tileY, maxAnimals, out herdId, out reason);
    }

    /// <summary>One waypoint of a herd route, in tiles.</summary>
    public readonly struct HerdWaypoint
    {
        /// <summary>Creates a waypoint.</summary>
        public HerdWaypoint(int tileX, int tileY) { TileX = tileX; TileY = tileY; }
        /// <summary>Tile X.</summary>
        public int TileX { get; }
        /// <summary>Tile Y.</summary>
        public int TileY { get; }
    }

    /// <summary>The route Vanilla has just built for a herd; valid only during the handler call.</summary>
    /// <remarks>
    /// Vanilla's route builder (RVA 0x127E60) floods the sectors around the leader and writes up to
    /// <see cref="MaximumWaypoints"/> waypoints outward in a random direction; the herd then walks them in
    /// order, a fleeing herd takes the last one at once. Handlers may only shorten or replace the route.
    /// A route shortened to no waypoint keeps the herd where its leader stands: Vanilla reads the first
    /// waypoint without looking at the count, so APIShared sets it to the leader's tile.
    /// Using the object after the handler returned throws <see cref="InvalidOperationException"/>.
    /// </remarks>
    public sealed class HerdRoute
    {
        /// <summary>The most waypoints a Vanilla route holds.</summary>
        public const int MaximumWaypoints = 10;

        private readonly HerdRuntime.RouteAccess access;

        internal HerdRoute(HerdInfo herd, int leaderTileX, int leaderTileY, HerdRuntime.RouteAccess access)
        {
            Herd = herd; LeaderTileX = leaderTileX; LeaderTileY = leaderTileY; this.access = access;
        }

        /// <summary>The herd the route belongs to.</summary>
        public HerdInfo Herd { get; }
        /// <summary>The leader's tile X, or -1 when the herd has no living leader.</summary>
        public int LeaderTileX { get; }
        /// <summary>The leader's tile Y, or -1 when the herd has no living leader.</summary>
        public int LeaderTileY { get; }
        /// <summary>The current number of waypoints, 0 through <see cref="MaximumWaypoints"/>.</summary>
        public int WaypointCount => access.Count;

        /// <summary>Reads a waypoint.</summary>
        /// <param name="index">0 through <see cref="WaypointCount"/> - 1.</param>
        public HerdWaypoint GetWaypoint(int index) => access.Get(index);

        /// <summary>Keeps only the first <paramref name="count"/> waypoints.</summary>
        /// <param name="count">0 through <see cref="WaypointCount"/>.</param>
        public void Truncate(int count) => access.Truncate(count);

        /// <summary>Replaces the route with one waypoint, for example a way home.</summary>
        /// <param name="tileX">Tile X inside the map.</param>
        /// <param name="tileY">Tile Y inside the map.</param>
        public void ReplaceWithSingleWaypoint(int tileX, int tileY) => access.ReplaceWithSingle(tileX, tileY);
    }

    /// <summary>Handles a herd route right after Vanilla built it.</summary>
    public delegate void HerdRouteHandler(HerdRoute route);

    /// <summary>Lets mods shorten or redirect herd routes.</summary>
    /// <remarks>
    /// Deer, goats, lions, hyenas, rabbits and camels build their routes in Vanilla's route builder (RVA 0x127E60);
    /// crocodiles build none, and rabbits have a second route function of their own that is not covered. The
    /// builder runs inside the herd's update on the simulation thread, also while the herd flees; in the map editor
    /// herds build no routes. Handlers run there, in ascending order, then ordinal owner GUID and registration ID;
    /// later handlers see earlier changes. Exceptions are isolated per handler and logged once. Multiplayer peers
    /// must register the same handlers and decide from simulation data and synchronized settings only.
    /// The hook installs on the first registration and stays for the process lifetime; without registrations
    /// Vanilla's builder runs unchanged.
    /// </remarks>
    public static class HerdRouteEvents
    {
        /// <summary>Registers a route handler for the process lifetime, on the initialized Unity thread.</summary>
        /// <param name="ownerGuid">Your stable mod GUID.</param>
        /// <param name="registrationId">Stable ID, unique within this owner.</param>
        /// <param name="handler">Called with each route Vanilla builds.</param>
        /// <param name="reason">Empty on success; otherwise why registration failed.</param>
        /// <param name="order">Ascending handler order, default zero.</param>
        /// <returns>Whether the handler was registered and the hook is installed.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId, HerdRouteHandler handler, out string reason, int order = 0) =>
            HerdRuntime.TryRegisterRouteHandler(ownerGuid, registrationId, handler, order, out reason);
    }

    /// <summary>Vanilla's threat counters of the 10 by 10 tile sector around a tile.</summary>
    public readonly struct HerdSectorThreats
    {
        internal HerdSectorThreats(int units, int predators) { Units = units; Predators = predators; }
        /// <summary>Units of every player that are no animals, plus deer and goats of fleeing herds.</summary>
        public int Units { get; }
        /// <summary>Lions, hyenas and crocodiles.</summary>
        public int Predators { get; }
    }

    /// <summary>One mod's set of calm herds; see <see cref="HerdFlightRules"/>.</summary>
    public sealed class HerdFlightRule
    {
        internal HerdFlightRule(string ownerGuid, string registrationId, int slot)
        {
            OwnerGuid = ownerGuid; RegistrationId = registrationId; Slot = slot;
        }

        internal int Slot { get; }
        /// <summary>The registering mod's GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>The owner-local registration ID.</summary>
        public string RegistrationId { get; }

        /// <summary>Marks a herd calm or no longer calm for this rule.</summary>
        /// <remarks>Only inside a <see cref="Simulation.SimulationTickEvents"/> callback, so that all peers change it
        /// in the same tick.</remarks>
        /// <param name="herdId">1 through <see cref="Herds.MaximumHerdId"/>.</param>
        /// <param name="calm">True: the herd ignores its sector's threat counters while any rule keeps it calm.</param>
        /// <returns>False outside a simulation tick or for an invalid herd ID; nothing changes then.</returns>
        public bool TrySetCalm(int herdId, bool calm) => HerdRuntime.TrySetCalm(this, herdId, calm);

        /// <summary>Removes every calm mark of this rule.</summary>
        /// <remarks>Call it outside a running mission or inside a <see cref="Simulation.SimulationTickEvents"/> callback,
        /// so that all peers change the marks in the same tick.</remarks>
        public void ClearAll() => HerdRuntime.ClearCalm(this);
    }

    /// <summary>Lets mods keep deer and goat herds from fleeing.</summary>
    /// <remarks>
    /// Vanilla starts the flight of a deer or goat herd when the sector of its leader counts any threat: the
    /// sum of <see cref="HerdSectorThreats.Units"/> and <see cref="HerdSectorThreats.Predators"/>, read in each herd
    /// update (RVA 0x124F20 deer, 0x125330 goats). The counters know neither sight nor walls. A herd that any
    /// rule marks calm reads zero there and does not start fleeing for that reason; being shot at and a
    /// flight already running are not affected. Other kinds flee by other rules and ignore these marks.
    /// Rules are marks, not callbacks: a rule owner decides in its own simulation tick work and sets the
    /// marks, the native gate reads one byte per herd update without a managed call. APIShared clears all
    /// marks at every mission initialization and end, because herd IDs belong to one map; the marks are not
    /// saved, so rule owners set them again after loading. A mark belongs to a herd ID, not to a herd: when a herd
    /// dies out, a new herd can take over its record within the map and keeps the old mark until the rule owner
    /// sets it again, so owners clear the marks of herds that are gone or set their marks anew each time they
    /// decide. Multiplayer peers must register the same rules
    /// and decide from simulation data and synchronized settings only.
    /// The gate installs on the first registration, which must happen during startup before the first
    /// mission; it stays for the process lifetime.
    /// </remarks>
    public static class HerdFlightRules
    {
        /// <summary>Registers a calm-mark set for the process lifetime, on the initialized Unity thread.</summary>
        /// <param name="ownerGuid">Your stable mod GUID.</param>
        /// <param name="registrationId">Stable ID, unique within this owner.</param>
        /// <param name="rule">The registration on success.</param>
        /// <param name="reason">Empty on success; otherwise why registration failed.</param>
        /// <returns>Whether the rule was registered and the gate is installed.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId, out HerdFlightRule rule, out string reason) =>
            HerdRuntime.TryRegisterFlightRule(ownerGuid, registrationId, out rule, out reason);

        /// <summary>Reads Vanilla's threat counters of the sector that holds a tile.</summary>
        /// <remarks>Same thread rules as <see cref="Herds"/>. Vanilla keeps the counters current itself, one sector
        /// column at a time (RVA 0x126810).</remarks>
        /// <returns>False outside the 80 by 80 sector grid or when the herd service is unavailable.</returns>
        public static bool TryGetSectorThreats(int tileX, int tileY, out HerdSectorThreats threats) =>
            HerdRuntime.TryGetSectorThreats(tileX, tileY, out threats);
    }
}
