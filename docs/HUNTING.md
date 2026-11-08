# Huntable prey

Vanilla Hunters only hunt deer and goats. `APIShared.Wildlife.HuntablePrey` lets
mods read which unit types Hunters may currently hunt and add further types: all
other Vanilla animals as well as new animals of your own. How a new animal enters
the world is up to the registering mod.

## Register a type

Call `HuntablePrey.TryRegister` on the initialized Unity thread during startup, on
every simulation peer. Use your BepInEx GUID and an owner-local registration ID.
Keep the registration for the process lifetime.

```csharp
using APIShared.Wildlife;
using SHCDESE.Interop;

if (!HuntablePrey.TryRegister(pluginGuid, "camel",
        new HuntablePreyDefinition((int)eChimps.CHIMP_TYPE_CAMEL, handlingCost: 120),
        out HuntablePreyRegistration camel, out string reason))
    logger.LogError(reason);

// Later, before or after a mission, for example from a setting:
camel.TrySetEnabled(settings.HuntCamel, out reason);
```

- The unit type is the raw native type (`GameUnit.r_UnitChimp`) from 1 through 65535,
  so types outside `eChimps` work as well.
- `handlingCost` is a hint in map ticks for mods that rank targets. APIShared does not use it.
- When several mods register the same type, the highest `priority` wins; ties sort by
  ordinal owner GUID, then registration ID. A registration for deer or goat replaces
  the built-in entry's data; Vanilla keeps its own reservation release for them.
- War dogs and Engineers can never be admitted (status `Unsupported`): Vanilla stores
  other data in the field the Hunter uses for its reservation.
- `Status` and `Diagnostic` report the result of the latest catalog preparation:
  `Pending`, `Admitted`, `ExcludedByPriority`, `Unsupported` or `Disabled`.

## Read the catalog

`HuntablePrey.Current` returns the frozen catalog of the running mission, or outside a
mission the catalog the next mission would use. It always contains deer and goat.

```csharp
var catalog = HuntablePrey.Current;
if (catalog.TryGet(unitType, out HuntablePreyEntry entry))
    Use(entry.HandlingCost, entry.OwnerGuid, entry.IsVanilla);
```

`IsHuntable` answers whether Vanilla's Hunter query admits the type. Mods that refine
the query themselves (for example through the Script Extender's
`OnUnitHunterQueryTarget`) can still refuse individual candidates.

## Missions and multiplayer

- Each mission freezes the catalog during initialization. Registrations and
  `TrySetEnabled` take effect at the next mission.
- Custom types are active only where the mission mode allows custom gameplay mods,
  including the map editor. Elsewhere only deer and goat are huntable.
- In a real multiplayer lobby, the catalog digest is part of the existing production
  peer agreement. Peers with different catalogs cannot start; register identically
  and drive `TrySetEnabled` from synchronized settings.
- The catalog is not saved. A loaded game uses the registrations of the current session.

## Limits

APIShared admits the type to Vanilla's Hunter target query and releases the Hunter's
reservation. Everything else follows Vanilla for that type until a consumer changes it:
how the animal reacts, whether one arrow kills it, its meat, and how long its corpse
stays. Vanilla's own corpse search only looks for deer and goat corpses. Predators such
as lions still fight back.

## Implementation

- **Admission.** APIShared subscribes to the Script Extender's `OnUnitHunterQueryTarget`
  before consumers and sets `IsValidTarget = true` for active registered types. It installs
  no hook of its own at the query.
- **Reservation release.** While a Hunter holds a target, Vanilla writes 2 into the target's
  reservation word (public `GameUnit +0x448`) on each Hunter update. The query only accepts 0.
  Only deer and goats count the word down; any other type would be hunted once and then
  never again. APIShared counts it down for active registered types in a native simulation
  tick callback: 256 unit slots per map tick, chosen from the map tick, at most one
  decrement per slot and tick. Only IDs below the unit manager's next ID are visited, the
  same range Vanilla updates each tick. A held reservation therefore never reaches 0, and a
  released one reaches 0 within two passes over that range (2 × used IDs / 256 map ticks,
  at most 80 map ticks with all 9,999 slots in use).
  There is no per-unit hook and no allocation; without active registrations both paths
  return after one volatile read.
- **Native contract.** The field is bound to the audited native image. See
  [UpdateToNewDLL.md](../UpdateToNewDLL.md#huntable-prey-reservation-word).
