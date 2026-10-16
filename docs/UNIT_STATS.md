# Unit stat modifiers

Several mods change a unit's maximum health when it appears: Lord health percentages,
global health multipliers, per-unit profiles of custom troops. Each writes the field
itself, so the result depends on which mod writes last, and a value scaled before a save
can be scaled again after loading it. `APIShared.Units.UnitStatModifiers` composes every
registered modifier in a fixed order into one write per unit, and remembers what it wrote
so the same unit is never scaled twice.

Audience: authors of mods that change a unit's maximum health. Contracts are on the
declarations in [UnitStatContracts.cs](../src/Units/Stats/UnitStatContracts.cs) and
[UnitStatModifiers.cs](../src/Units/Stats/UnitStatModifiers.cs); the
[compiled example](../examples/ThirdPartyMod/UnitStatExample.cs) shows a registration.

## Usage

Register from `Awake` or the Extender's `LibraryLoaded`, on every multiplayer peer, before
any mission:

```csharp
UnitStatModifiers.TryRegister(MyPlugin.Guid, "lord-health", UnitStat.MaxHealth,
    UnitStatLayer.Multiplier, order: 0,
    (UnitStatSubject unit, out UnitStatContribution contribution) =>
    {
        contribution = UnitStatContribution.Percent(lordPercent);   // a synchronized setting
        return unit.UnitType == eChimps.CHIMP_TYPE_LORD;            // false: no contribution
    },
    out UnitStatRegistration registration, out string reason);
```

The modifier runs for every settled unit (see
[UnitSettledEvents](../src/Units/Settled/UnitSettledEvents.cs)) and decides from the
subject whether and how to contribute. Keep it cheap and deterministic: integer settings
that are identical on every peer, no local-player, Unity or clock decisions, no logging per
call. Dispose the handle to stop contributing.

## Contract

**Layers and order.** Contributions apply in layer order, then within a layer by ascending
`order`, ordinal owner GUID and ordinal registration ID, independent of registration time:

| Layer | Contribution | Effect |
|---|---|---|
| `Base` | `Set(Base, value)` | Replaces the starting value (the native maximum); the last one wins |
| `Additive` | `Add(amount)` | Adds a signed amount |
| `Multiplier` | `Scale(numerator, denominator)`, `Percent(percent)` | Multiplies, rounding half away from zero after each step |
| `Override` | `Set(Override, value)` | Replaces the result so far; the last one wins |
| `Clamp` | `Range(minimum, maximum)` | Limits the result |

The result is at least 1. A contribution of the wrong kind, a ratio with a negative
numerator or a denominator below 1, and a range with minimum above maximum are ignored and
counted in `CallbackFailures`, like a throwing modifier; the other modifiers still apply.

**The write.** One write per unit per settlement, and only when the result differs from the
live maximum: maximum health; current health rescaled to keep the unit's share (rounded; a
living unit keeps at least 1, a unit at 0 stays at 0); health percentage (rounded) and
health-bar blocks (percentage / 10). It happens in APIShared's own UnitSettled callback at
order `int.MinValue`, on the simulation thread, inside the settling tick. Peers that
register the same modifiers write the same values at the same point.

**Applied marker.** For each unit APIShared records the GlobalId, type, the native maximum
it started from and the maximum it wrote, and keeps the table in the versioned save record
`APIShared.UnitStats` (entry `maxhealth`). When a unit settles again with the same GlobalId
and type and still carries the written maximum, the composition starts from the recorded
native maximum, so a save, reload or second report never applies a modifier twice. A unit
whose type changed (recruitment, assignment, disband) or whose slot holds another unit starts
from its new live maximum. Every archive load replaces the table: restored from the save, or
emptied for a map, a save without the record or unreadable data. A save made with this service
therefore loads with the values it was saved with, however often it is saved and loaded again
(`SaveLoadSaveLoadAppliesEveryModifierOnce` in `tests/Core.Tests/UnitStatModifierTests.cs`).

**Already-settled units are not re-applied.** Registering, disposing or changing a modifier
affects units that settle afterwards. Units already in the world keep their values until
they settle again (for example when recruited), and then compose from their recorded native
maximum with the modifiers active at that moment. Re-applying at registration time would
write at a moment that differs between multiplayer peers.

**Settings are frozen during a match.** Modifiers read settings that change only between
matches. A setting that its mod explicitly allows to change in a running match is an
individually designed exception, with its own timing and multiplayer rules; this service has
no mechanism for it.

**Thread and lifetime.** Registration and disposal are thread-safe from any thread. Nothing
is subscribed before the first registration; that registration subscribes APIShared to
UnitSettled and registers the save record, both for the process lifetime. With no active
registration a settlement costs one volatile read.

## Migration guides

For each consumer's maintainer; nothing here changes those mods. A transition switch that
keeps the old path for a release or two is an optional strategy, useful when many independent
mods depend on the old behavior; it is not required. Never run both paths for the same units.

**Saves made before the migration** need no special handling (agreed on
[#43](https://github.com/SHCDE-APIShared/APIShared/pull/43)). They carry no `APIShared.UnitStats`
record: units the game restores from them keep their saved values, and a unit that settles
again after the load starts from its live maximum, so a value scaled before the save can be
scaled once more.

### ExtraFeatures Lord health

Today `LordHealthRuntime` records each Lord's Vanilla maximum per GlobalId in its own save
record (`ExtraFeatures.LordHealth.v1`), finds Lords from `OnUnitCreate` Post and a 10-tick
scan, and writes maximum, current, percentage and health-bar blocks itself. At a loaded save
it applies the current percentages again from the recorded Vanilla maximum.

1. Require the APIShared release that contains `UnitStatModifiers` in the plugin's dependency
   attribute.
2. At startup (`Awake` or the Extender's `LibraryLoaded`), on every peer, register one
   `Multiplier` modifier `lord-health` for `UnitStat.MaxHealth` at `order: 0`, and keep the handle.
3. Capture the normalized human and AI percentages when the match starts, as `BeginMap` does
   now; they stay fixed for the match.
4. In the modifier, return false unless `unit.UnitType == eChimps.CHIMP_TYPE_LORD`, the feature
   mode allows Lord health (`GameplayFeatureModePolicy`), and the player's percentage is not 100.
   Otherwise return `UnitStatContribution.Percent(percent)`, choosing the human or AI
   percentage with `GamePlayerManagerAPI.Instance.IsAIPlayer(unit.PlayerId)`. The service rounds
   like `LordHealthMultiplierPolicy` (half away from zero, at least 1) and rescales current
   health, percentage and health-bar blocks with the same arithmetic.
5. Remove the `OnUnitCreate` subscription, the tick scan, `TryApplyPlayerLord` and the
   `ExtraFeatures.LordHealth.v1` record handler: the service keeps the Vanilla maximum and the
   applied marker in its own save record.
6. Behaviour change at a loaded save: today a loaded save's Lords pick up the current
   percentage (applied again from the recorded Vanilla maximum). With the service they keep the
   percentage the save was made with. This is intended: a load continues a match, the settings
   coordinator restores the save's settings, and settings stay frozen during a match, so the
   saved value is the correct one. Mention it in the release notes.
7. To check in game: the service writes when the Lord settles (the first tick at which its slot
   is really alive, see UnitSettledEvents); whether the game has finished AI Lord health scaling
   by then is not verified.
8. In the settings help text, say how the percentage combines with other mods' health
   multipliers: it multiplies together with them, and an absolute value (`Override`) from
   another mod replaces it.
9. Check: new game, human and AI Lords at Vanilla x percent; save, load, save, load: unchanged
   health and no `APIShared` write at the reloads.

**CrusaderDETweaker health multiplier.** Today it multiplies by a float at `OnUnitCreate`
Post and after recruitment and sets current health to the new maximum. As a `Multiplier`
with an integer ratio (for example `Scale(1250, 1000)` for 1.25) and its non-modifiable
unit categories excluded in the modifier, it becomes deterministic and keeps wounds.

**Byzantine unit profiles.** Today `ExactUnitStatsRuntime` scans every tick and rescales a
carrier once when its maximum equals the carrier's native default. As a `Base` returning
`NativeValue * Hp / NativeHp` for units whose exact profile resolves, it composes with the
multipliers above instead of racing them. Whether the exact-unit tag is resolvable when the
unit settles is not verified. Speed stays on its own path (below).

## Speed

Not composed here. The game's movement code recalculates a moving unit's active speed
(see `ExactUnitStatsRuntime` in the Byzantine project), so a value written when the unit
settles does not last. Per-unit speed needs a site in the movement step; `UnitStat` can gain
a speed entry without breaking this API when such a site exists.

## Limits

- Maximum health only. The written fields are `r_MaxHealth`, `r_CurrentHealth`,
  `r_CurrentHealthPercentage` and `r_HealthBarBlocks`, audited by
  `tools/Validation/Verify-Interop.ps1`.
- Units that the game restores from a save without its spawn function are not reported by
  UnitSettled and keep their saved values; that is the intended behavior for units already
  composed before the save.
- A mod that keeps writing maximum health itself is not coordinated: if it changes a value
  this service wrote, the next settlement of that unit treats the changed value as native.
- Records are per peer and not compared across peers; they are identical when the peers were
  in sync, like the units they describe.

## Implementation

[UnitStatComposer](../src/Units/Stats/UnitStatComposer.cs) holds one immutable snapshot of
active registrations, sorted by layer, order, owner and ID, published with one volatile
write; the settle path reads it once and runs one pass. Modifiers receive the subject by
value and return their contribution by `out`; nothing is allocated. The marker table is an
array indexed by unit slot. UnitSettled delivers on one thread, the only writer of single
records; save capture copies the table under a sequence counter (odd while a record or the
table changes) and retries on a change, so the settle path takes no lock. Loads replace the
table reference. The settle and compose methods skip `.locals init`
(`SkipLocalsInitAttribute`), which the .NET Framework JIT otherwise satisfies by zeroing the
frame with `rep stos`, and the unit store passes primitives instead of a struct for the same
reason.

### Measurements

`tests/APISharedTests/UnitStatBenchmarkTests.cs`: 64 real `GameUnit` records (installed
interop layout) in private memory, read and written through the same fields as production
(the slot lookup is an index instead of `UnitAccess`). Each call goes through an
`Action<UnitSettledNotification>`, as UnitSettled invokes it; every call starts from 80/100
so every mode writes. The hand-written write is a consumer's own callback scaling by 3/2
with the same wound rule. 200,000 calls per sample, 31 samples after two warm-up rounds,
alternating mode order, raised priority. 0 bytes allocated per call in every mode.

| Mode (ns per settlement, min / median) | Cost |
|---|---|
| Hand-written single write (one consumer's own callback) | 6.8 / 7.2 |
| Composer, 0 active modifiers | 2.0 / 2.1 |
| Composer, 1 modifier | 41.0 / 43.0 |
| Composer, 8 modifiers (all five layers) | 61.3 / 69.9 |

The quietest of five runs (other work kept the machine at 60 to 100 percent CPU); the
others were up to 8 ns (1 modifier) and 40 ns (8 modifiers) slower at the median.
Removing parts one at a time puts the extra cost of one modifier, about 36 ns, at roughly
8 ns for the wound rescale and percentage (three 64-bit divisions, two of which the
hand-written write also needs), 4 ns for the composition and the modifier call, 3 ns for
the marker read and its sequence-counted write, and the rest in the call structure: two
calls into the composer, two calls through the unit store and the subject passed by value.
Each further modifier adds about 3 ns.

**What the comparison leaves out.** The hand-written write is not correct on its own: it
scales again after a reload or a second report, and it does not compose with other mods. A
correct consumer also needs its own record of what it wrote, kept in the save and restored
before units can settle again, plus its own wound rescale. Today one consumer keeps a save
file of its own and scans every 10 ticks, another scans every unit on every tick. A settlement happens once per created, recruited,
assigned or disbanded unit, never per tick or frame, so 8 modifiers cost about 70 ns per
new unit for every mod together.

Limits: desktop CLR 4.8.1 on the test host, not the game's Unity Mono; no in-game timing.
