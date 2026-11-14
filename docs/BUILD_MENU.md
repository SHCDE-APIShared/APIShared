# Build-menu availability

Requires APIShared 0.54.0 or later. `APIShared.Buildings.BuildMenuAvailability` lets mods make build-menu
mappers (buildings and other placement tools, `eMappers`) available or unavailable, relative to Vanilla's own
decision. APIShared owns the native query; mods own their rules and settings.

## Register a rule

Register on the initialized Unity thread during startup, before the first mission of the process: the native
wrapper installs on the first registration and only then. Keep the rule for the process lifetime.

```csharp
using APIShared.Buildings;
using SHCDESE.Interop;

if (!BuildMenuAvailability.TryRegister(pluginGuid, "hunters-hut", out BuildMenuAvailabilityRule rule, out string reason))
    logger.LogError(reason);

// Later, for example when a synchronized setting changes:
rule.TrySetDecision((int)eMappers.MAPPER_HUNTER,
    settings.HutAlwaysAvailable ? BuildMenuAvailabilityDecision.Allow : BuildMenuAvailabilityDecision.Default, out reason);
```

- `Allow` makes a mapper available although a Vanilla game rule refuses it, for example the Hunter's hut on a
  skirmish or multiplayer map that started without deer and goats, or a mapper the game type hides. The mission's
  own availability table still counts: a mapper the map or scenario switches off stays unavailable.
- `Veto` makes a mapper unavailable whatever Vanilla and the other rules decide.
- `Default` withdraws the rule's decision; `SetEnabled(false)` withdraws all of them and keeps them for later;
  `ClearAll` drops them.
- Any enabled veto wins; otherwise any enabled allow applies; otherwise Vanilla decides. Registration order and
  owner do not matter.
- Decisions may change at any time and from any thread; the next query uses them.

## Missions and multiplayer

Vanilla asks one native function whether a mapper may be used: the build menu, its hotkeys, the managed placement
check and the local game state the UI shows. It has no player argument and decides for the player this game shows.
The simulation, the AI and other peers never ask it, so a rule changes no simulation state by itself; the
placement command it enables runs on every peer like any other. Register identical rules on every peer and drive
them from synchronized settings, so that all players have the same options. A rule may decide per player by setting
its decisions from the local player's ID.

## Limits

- Bound to the audited native image; elsewhere `IsAvailable` is false and registrations fail.
- Mappers 1 through 511. `Allow` respects the mission table only for mappers below 0x1B8, the range that table
  covers; above it `Allow` makes the mapper available.
- A registration after the first mission started fails when no rule installed the wrapper before.

## Implementation

One purely native inline stub wraps the entry of Vanilla's availability query (RVA 0xFFC70) and reads one decision
byte per mapper. Without a decision it continues into Vanilla unchanged; a veto returns 0 without running Vanilla,
which has no side effects; an allow calls a copy of the original entry and returns 1 where Vanilla returned 0 or
less, unless the mission's availability word for that mapper is 0 or less. No managed transition. The stub stays
for the process lifetime. Native details and update checks:
[UpdateToNewDLL.md](../UpdateToNewDLL.md#build-menu-availability), site group in [native sites](NATIVE_SITES.md).
