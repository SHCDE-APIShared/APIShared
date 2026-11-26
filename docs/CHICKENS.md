# Chickens per player

Requires APIShared 0.54.0 or later. `APIShared.Wildlife.ChickenPopulationModifiers` lets mods change how many
chickens Vanilla keeps for each player. APIShared owns the native decision; mods own their settings.

## What Vanilla does

A scheduled step of the simulation computes a target for every active player: 0 to 10 from a player stock (probably
food), or 100 while Vanilla's own "many chickens" switch is set. It compares the target with the player's chickens:
below it, one chicken appears next to a random bakery, granary or mill of that player (none without such a
building); above it, the first chicken of that player starts to fade out. Not in the map or siege editor.

## Register a modifier

Register on the initialized Unity thread during startup, before the first mission of the process: the native
bridge installs on the first registration and only then. The handle starts disabled.

```csharp
using APIShared.Modifiers;
using APIShared.Wildlife;

if (!ChickenPopulationModifiers.TryRegister(pluginGuid, "chickens-per-player",
        context => settings.Active ? ValueModifierContribution.FixedValue(settings.Chickens) : ValueModifierContribution.None,
        out ChickenPopulationModifierHandle handle, out string reason))
    logger.LogError(reason);
handle.SetEnabled(settings.Enabled);
```

- The provider sees `ChickenPopulationContext`: the player (1 through 8), Vanilla's final target and the player's
  current chicken count.
- Contributions compose like [staged numeric modifiers](VALUE_MODIFIERS.md) from Vanilla's target, in ascending
  `order`, then ordinal owner GUID and registration ID. The result is rounded once and clamped to 0 through
  `ChickenPopulationModifiers.MaximumTarget` (1000).
- Only the target changes. When and where Vanilla spawns or removes a chicken stays Vanilla's: at most one per
  player and step, spawned only with a bakery, granary or mill.

## Missions and multiplayer

Providers run on the simulation thread, for every active player of each chicken step, without registry locks.
Decide from simulation data and synchronized settings only, so that every peer gets the same target; register
identically on every peer and switch handles identically, for example from synchronized settings before or after a
mission. Providers must not block, write native records or touch UI. An exception skips that provider's
contribution for that decision and is logged once. Fixed values alone are composed without allocation.

## Limits

- Bound to the audited native image; elsewhere `IsAvailable` is false and registrations fail.
- A first registration after the first mission started fails.

## Implementation

One inline stub over Vanilla's compare of target and count (RVA 0xD2AB4) replays the "many chickens" selection, and
only while an enabled handle exists (one native demand word) calls a rooted managed callback with player, target
and count, whose result replaces the target before the replayed compare. Both of Vanilla's branches read that
compare's flags. The stub stays for the process lifetime. Native details and update checks:
[UpdateToNewDLL.md](../UpdateToNewDLL.md#chicken-population), site group in [native sites](NATIVE_SITES.md).
