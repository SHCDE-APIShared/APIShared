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
emptied for a map, a save without the record or unreadable data.

**Already-settled units are not re-applied.** Registering, disposing or changing a modifier
affects units that settle afterwards. Units already in the world keep their values until
they settle again (for example when recruited), and then compose from their recorded native
maximum with the modifiers active at that moment. Re-applying at registration time would
write at a moment that differs between multiplayer peers.

**Thread and lifetime.** Registration and disposal are thread-safe from any thread. Nothing
is subscribed before the first registration; that registration subscribes APIShared to
UnitSettled and registers the save record, both for the process lifetime. With no active
registration a settlement costs one volatile read.

## Consumer notes

Notes for each consumer's maintainer; nothing here changes those mods. Keep the old path
behind a switch for two releases, and never run both paths for the same units.

**ExtraFeatures Lord health** (first Serpens consumer). Today it records the Lord's Vanilla
maximum per GlobalId in its own save file and writes maximum, current, percentage and
health-bar blocks from a 10-tick scan. As a `Multiplier` returning `Percent(human or AI
percent)` for `CHIMP_TYPE_LORD` only, the composer keeps the Vanilla basis and the marker.
Its source notes that the game finishes AI Lord health scaling after the spawn Post; whether
that is done when the Lord settles is not verified. Saves that carry only its own record
need its existing path.

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
frame with `rep stos`.

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
| Hand-written single write (one consumer's own callback) | 6.7 / 6.9 |
| Composer, 0 active modifiers | 1.9 / 2.1 |
| Composer, 1 modifier | 44.0 / 44.9 |
| Composer, 8 modifiers (all five layers) | 64.8 / 66.2 |

One of three consecutive runs; the other two were up to 10 ns (1 modifier) and 30 ns
(8 modifiers) slower at the median on this shared machine. The fixed part over a
hand-written write (about 37 ns) is the marker read and its sequence-counted write, the
subject and the modifier call; each further modifier adds about 3 ns.

Limits: desktop CLR 4.8.1 on the test host, not the game's Unity Mono; no in-game timing.
Settlements are rare (one per created or recruited unit), so these costs are per unit, not
per frame.
