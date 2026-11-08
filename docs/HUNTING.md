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

## Own player's animals and corpses

Two more options on the definition:

- `WithOwnPlayerPrey()`: Hunters may also hunt animals of this type that belong to their own
  player (the same owner, not an ally), as well as unowned ones. Vanilla refuses owned animals
  at three native points; APIShared installs its gates there on the first such registration.
  If another plugin already patches those points (for example an older ImprovedHunters), the
  registration is `Unsupported` and says why.
- `WithCorpseRemovedWhenButchered()`: the corpse disappears as soon as a Hunter starts
  butchering it, instead of fading out, so no other Hunter can pick it again.

How long a corpse lasts before it fades is part of each animal type's own update and stays
with the mod that adds or changes that animal.

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

## Target policies

`HunterTargetPolicies.TryRegister` adds a rule that sees each candidate of Vanilla's
Hunter target query, with the correct Hunter id, and may allow or refuse it.

```csharp
HunterTargetPolicies.TryRegister(pluginGuid, "own-chickens-only",
    (in HunterTargetCandidate candidate) => candidate.CandidateType == chicken && TooFar(candidate)
        ? HunterTargetDecision.Refuse : HunterTargetDecision.Default,
    out string reason);
```

- Only candidates that are alive, no corpse and owned by no player reach policies; Vanilla
  rejects the others before. Reservation, distance and sight tests follow the decision.
- Any `Refuse` refuses. Otherwise any `Allow` admits a catalog type; `Allow` cannot admit a
  type outside the catalog, because only catalog types get their reservation released.
- Policies run on the simulation thread for every candidate of every query, so keep them
  fast and deterministic. `IsSimulationQuery` is false for one local query Vanilla runs only
  for the selected Hunter's idle speech; change simulation state only when it is true.

## Meat per animal

Vanilla gives a Hunter the same base of 6 meat for every animal, before its bonus. Give a
type its own base with `HuntablePreyDefinition.WithBaseMeat(amount)`, or change it per yield:

```csharp
HunterMeatYieldEvents.TryRegister(pluginGuid, "meat", yield =>
{
    if (yield.PreyType == (int)eChimps.CHIMP_TYPE_DEER) yield.BaseAmount = settings.DeerMeat;
}, out string reason);
```

- The base ranges from 0 through 1000; Vanilla's bonus is added to it afterwards.
- Handlers run in order on the simulation thread when the Hunter takes the meat from his hut.
  They must decide identically on every multiplayer peer.
- APIShared remembers which animal each Hunter butchered and keeps that record in saves. A
  yield whose animal is unknown, for example in a save written before this record existed,
  keeps Vanilla's base and raises no event.

## Missions and multiplayer

- Each mission freezes the catalog during initialization. Registrations and
  `TrySetEnabled` take effect at the next mission.
- Custom types are active only where the mission mode allows custom gameplay mods,
  including the map editor. Elsewhere only deer and goat are huntable.
- In a real multiplayer lobby, the catalog digest is part of the existing production
  peer agreement. Peers with different catalogs cannot start; register identically
  and drive `TrySetEnabled` from synchronized settings.
- The catalog is not saved. A loaded game uses the registrations of the current session.
- Policies and meat handlers are not part of the peer agreement. Register them identically
  on every peer and keep their decisions deterministic.
- A native resynchronization does not transfer the record of butchered animals. Affected
  Hunters fall back to Vanilla's base for that one yield.

## Limits

APIShared admits the type to Vanilla's Hunter target query and releases the Hunter's
reservation. Everything else follows Vanilla for that type until a consumer changes it:
how the animal reacts, whether one arrow kills it, and how long its corpse
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
- **Hunter native sites.** Two purely native stubs, without a managed transition, record
  the querying unit and its caller at the query's entry (`0x18AF50`), and the Hunter, corpse
  and corpse type when a Hunter arrives at a corpse (`0x1306AE`), which every butchered animal
  passes exactly once. The corpse record is written only while a base meat or a meat handler
  is active. Target policies read the query record inside the Extender event; the meat yield
  uses the Extender's `OnCalculateBonusYield` and checks that the record still belongs to that
  Hunter and his current target. The same records correct two wrong unit ids of Script Extender
  Hunter events ([fixes](SCRIPT_EXTENDER_FIXES.md#hunter-event-unit-ids)).
- **Owner gates.** Three purely native gates at the query owner check (`0x18AFD8`), the explicit
  attack team check (`0x18EBDA`) and the arrow team check (`0x192AA7`) admit an animal of a flagged type
  when its owner equals the Hunter's. The arrow then repeats Vanilla's hit checks and skips the
  "under attack" warning, which a player's own Hunter must not trigger. They are installed on the first
  registration that needs them; an empty table keeps Vanilla's behavior.
- **Corpse removal.** The corpse-arrival stub marks a flagged corpse for deletion (alive state 3),
  Vanilla's own end path; the unit pass then removes it. The butchering Hunter never reads it again.
- **Native contract.** The field and the sites are bound to the audited native image. See
  [UpdateToNewDLL.md](../UpdateToNewDLL.md#huntable-prey-reservation-word) and
  [Hunter native sites](../UpdateToNewDLL.md#hunter-native-sites).
