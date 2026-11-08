# Herds

Requires APIShared 0.38.0 or later. `APIShared.Wildlife.Herds`, `HerdRouteEvents` and
`HerdFlightRules` let mods read Vanilla's animal herds, spawn new ones, shorten or redirect
their routes and keep deer and goats from fleeing. Rules stay with the consumer: APIShared
owns the native sites and runs Vanilla unchanged until a mod registers.

## Read herds

Vanilla keeps every herd in a tribe record; a unit's `r_TribeId` names its herd. A map holds
all of its herds there from its first tick.

```csharp
if (Herds.TryGet(unit->r_TribeId, out HerdInfo herd) && herd.Kind == HerdKind.Deer && !herd.IsFleeing)
{
    var members = new List<int>();
    Herds.TryCopyMemberUnitIds(herd.HerdId, members);
}
```

`HerdInfo` holds the kind, owner (0 for wild herds), leader, member count, the record's
`GlobalId` (a new herd that reuses the record gets a new one) and, for deer, goats and rabbits,
whether the herd flees. Members include corpses until Vanilla removes them. Read on the
simulation thread, for example in a `SimulationTickEvents` callback or a route handler, or
while the simulation is stopped.

## Spawn herds

`Herds.TrySpawn(kind, tileX, tileY, maxAnimals, out herdId, out reason)` calls Vanilla's own
herd spawn and keeps at most `maxAnimals` animals; the rest leave in the same tick through
Vanilla's path for animals over its limit. Call it only inside a `SimulationTickEvents`
callback. Vanilla draws the herd from the game's random numbers, so multiplayer peers must
make the same calls in the same tick and order.

| Kind | Vanilla size |
|---|---|
| `Deer` | 9 to 12 |
| `Goat` | 4 to 5 |
| `Lion` | 3 to 4 |
| `Hyena` | 6 to 9 |
| `Rabbit` | 14 to 21 |
| `Camel` | 2 to 5 |
| `Crocodile` | 1 |

Vanilla refuses blocked tiles, water and tiles outside the map; the call then returns false.
New herds are wild and grow, wander and flee by Vanilla's rules.

## Shorten or redirect routes

```csharp
HerdRouteEvents.TryRegister(MyPlugin.Guid, "home-range", route =>
{
    if (route.Herd.Kind == HerdKind.Deer && TooFar(route))
        route.ReplaceWithSingleWaypoint(homeX, homeY);
});
```

Vanilla builds a route of up to 10 waypoints outward from the leader each time a herd sets
off, also when it flees. Handlers run right after that, on the simulation thread, in
ascending order, then by owner GUID and registration ID; later handlers see earlier changes.
A handler may read the waypoints, `Truncate` them or replace the route with one waypoint. A
route cut to nothing keeps the herd where its leader stands. The `HerdRoute` object is only
valid during the call.

Deer, goats, lions, hyenas, rabbits and camels build their routes there. Crocodiles build
none, and rabbits have a second route function of their own that this event does not cover.
In the map editor herds build no routes.

## Keep herds calm

```csharp
HerdFlightRules.TryRegister(MyPlugin.Guid, "calm", out HerdFlightRule rule, out string reason);
// later, in a SimulationTickEvents callback:
rule.TrySetCalm(herdId, NoEnemyCanSeeIt(herdId));
```

Vanilla lets a deer or goat herd flee as soon as the 10 by 10 tile sector of its leader counts
any threat: units of any player that are no animals, deer and goats of fleeing herds, and
lions, hyenas and crocodiles. The counters know neither sight nor walls.
`HerdFlightRules.TryGetSectorThreats` reads them. A herd that any rule marks calm reads zero
there and does not start fleeing for that reason. Being shot at and a flight already running
stay Vanilla's.

Marks are set, not asked for: the native gate reads one byte per herd update. Set them inside a
`SimulationTickEvents` callback, so that all peers change them in the same tick.
`ClearAll` removes a rule's marks. APIShared clears all marks at every mission initialization
and end, because herd IDs belong to one map; marks are not saved, so set them again after
loading.

## Missions and multiplayer

Register on the initialized Unity thread during startup, on every peer, and keep the
registration for the process lifetime; switch your rules logically. Decide from simulation
data and synchronized settings only. The flight gate installs on the first flight-rule
registration and only before the first mission of the process; a later first registration
fails. The route hook installs on the first route registration at any time.

## Limits

- Herds are bound to the audited native image; on another image `Herds.IsAvailable` is false
  and registrations fail.
- Only deer and goats read the flight marks; other kinds flee by other rules.
- Rabbits' second route function and crocodiles have no route event.

## Implementation

Herds are tribe records of Vanilla's tribe manager (RVA 0x7CC6720, 0x688 bytes each). The
Script Extender's `GameTribe` starts 0x2A behind a raw record; `HerdNativeContract` checks its
named fields against the raw offsets that Vanilla's own instructions read. The kind (raw +0x42)
and flight word (raw +0x5F4) have no Extender name and are pinned by those instructions.

- Spawning calls `0x11E150(manager, art, x, y, height)` with Vanilla's art per kind.
- Routes: one function detour on the route builder `0x127E60`, which zeroes the route index
  and count before it writes waypoints. Callers walk to the first waypoint without looking at
  the count, so APIShared writes the leader's tile there when a handler emptied the route.
- Flight: two purely native inline stubs where the deer (`0x124F20`) and goat (`0x125330`)
  updates read their sector's threat counters; a calm herd gets zero. No managed call per
  update. `HerdCalmTable` combines the marks of all rules.

Native details and update checks: [UpdateToNewDLL.md](../UpdateToNewDLL.md#herds), site
groups in [native sites](NATIVE_SITES.md).
