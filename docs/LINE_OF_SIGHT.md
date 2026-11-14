# Line of sight

Audience: mods that need to know whether one place or unit can see another the way
the game decides it, and mods that change which buildings block that sight.
Contracts are on the declarations in
[LineOfSightContracts.cs](../src/Sight/LineOfSightContracts.cs),
[LineOfSight.cs](../src/Sight/LineOfSight.cs) and
[SightBlockingBuildings.cs](../src/Sight/SightBlockingBuildings.cs) (namespace
`APIShared.Sight`). Native update contracts are in
[UpdateToNewDLL](../UpdateToNewDLL.md#line-of-sight) and the Vanilla audit in the
native baseline (`knowledge/LINE_OF_SIGHT.md`).

## Usage

### Asking Vanilla's sight test

`LineOfSight` calls the same native routines the game uses for every unit sight
check (for example the Hunter's target search and the ranged attack order). It
installs no hook and changes no game state.

```csharp
using APIShared.Sight;

// On the simulation thread, for example inside a SimulationTickEvents callback.
if (LineOfSight.TryEvaluateUnits(viewerUnitId, targetUnitId, LineOfSightRoutine.Bidirectional,
        out int result, out LineOfSightFailure failure) &&
    LineOfSight.IsVisibleForTargetQuery(result))
{
    // The Hunter's own target search would accept this target as seen.
}
```

- `TryEvaluate(SightPoint viewer, SightPoint target, routine, ...)` works on world
  points (8 units per tile, tile centres at `tile * 8 + 4`).
- `TryGetUnitPoint` reads a unit's point as Vanilla does: world X/Y and the signed
  sum of its height and height offset, plus an offset you pass.
  `ViewerEyeHeight` (30) and `TargetHeight` (26) are Vanilla's offsets in both Hunter
  callers; the ranged attack order raises the viewer offset to 45 or 60 for two unit
  types, so pass your own offset where that matters.
- `TryGetTileViewpoint` stands a viewer on a tile centre at the tile's height plus an
  offset, for example to test a place a unit could move to before it goes there.
- `LineOfSightRoutine.Bidirectional` is Vanilla's wrapper: forward trace, and the
  reverse trace only when the forward one answers 0. `Directional` is a single
  forward trace of the core with Vanilla's obstacle checks enabled.
- The result is Vanilla's raw value: `0` blocked (also for a target reached after a
  single step), otherwise the traced steps up to `StepLimitResult` (1000), and
  `SamePointResult` (100000) for identical X and Y. `IsVisibleForTargetQuery`
  (1..0x1B0, which also limits the Hunter search to 432 steps) and
  `IsVisibleForDirectAttack` (above 0) restate the two Vanilla readers.

A failed query returns false with a `LineOfSightFailure` and result 0; choose your
own fallback. Unavailability is permanent for the process
(`LineOfSight.UnavailableReason`).

### Making an exempt building type block sight

Vanilla's obstacle-height helper ignores the Hunter's hut when it is asked for sight,
so units see through it, while arrows still collide with it. A mod can register a
rule that lets the type block sight like an ordinary building (with its own value
from Vanilla's building-height table, 40 for the Hunter's hut):

```csharp
// In your LibraryLoaded handler, on the Unity thread, before the first mission.
if (SightBlockingBuildings.TryRegister(MyGuid, "hut-sight", eStructs.STRUCT_HUNTERS_HUT,
        out SightBlockingBuildingRule rule, out string reason))
{
    // Later, outside a running mission or inside a SimulationTickEvents callback,
    // from synchronized settings:
    rule.SetEnabled(mySettings.HutBlocksSight);
}
```

- Only types for which `IsExemptInVanilla` is true can be registered; on the audited
  image that is the Hunter's hut only.
- Registrations start disabled. A type blocks while at least one enabled rule of any
  owner exists for it; without one, Vanilla's behaviour is unchanged.
- The rule affects every sight test of the game, including `LineOfSight` queries, not
  only one mod's units.

## Public limits

- **Thread.** Call `LineOfSight` synchronously on the simulation thread. It reads the
  live map; another thread may observe a half-updated map. A query from inside
  another query on the same thread (for example from a native hook on the traced
  routines) is refused with `Reentrant`.
- **Determinism.** Results depend only on map state and inputs. Gameplay decisions
  in multiplayer must come from simulation-thread queries at the same tick on every
  peer. `SightBlockingBuildingRule.SetEnabled` changes gameplay for all units; every
  peer must apply the same state at the same tick.
- **Inputs.** Both points must lie inside the current map diamond with non-negative
  world coordinates; heights are limited to +/- `MaximumHeightMagnitude` (100000) so
  that Vanilla's integer interpolation cannot overflow. Vanilla's routines index the
  map without bounds checks, so the service checks before the native call.
- **Registration window.** The first `SightBlockingBuildings.TryRegister` installs the
  native reader hook and must happen before the first mission of the process
  (`NativeInlinePatchWindow`); later first registrations fail with a reason. The hook
  and every registration stay for the process lifetime.
- **Native image.** Both services are bound to the audited `CrusaderDE.dll` (SHA-256
  `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`); there is no
  pattern fallback. Another image leaves them unavailable with a logged reason, and
  Vanilla's behaviour is unchanged.
- **Cost.** One managed-to-native call per query (two for a blocked bidirectional
  query), at most 1000 traced steps, no allocation. The building rule adds a few
  native instructions to each building lookup of the sight trace once installed and
  nothing before the first registration.

## Implementation

`LineOfSightNative` validates, at `LibraryLoaded`, the complete wrapper `0xA06F0`, the
core entry `0x9E350` and the six instructions that pin the result rules and eye
heights in the two Hunter callers, all against the unmodified load image. It claims
both entries as `CallOnly` in the [native-site registry](NATIVE_SITES.md) under
`Sight.LineOfSightCalls` and calls them through their live entries, as every native
caller does. The core's eighth argument is always `0`, the value the wrapper passes;
a non-zero value would switch all obstacle checks off. Vanilla writes a scratch value
to `context + 0xC`; each query passes a zeroed 16-byte context on its own stack,
between canaries, and disables the service for the process if anything else changed.

`SightBlockingBuildingsNative` validates the helper's image-base `lea`, its type
switch, the two table loads at `0x6BA0C`, the indirect jump, cases 0 and 3, all four
case targets and the 72 dispatch bytes. The first registration claims
`Sight.BuildingHeightDispatch` (Exclusive `0x6BA0C`/17, Observe the switch, the jump
and the tables) and installs one purely native `X64InlineHook` stub inside the
native inline patch window, published through `NativeInlineStubGroup`. The stub reads
one byte per building type from a never-freed 72-byte table: `0xFF` runs both
original loads, any other byte replaces the dispatch case and runs the original case
target load. `SetEnabled` recomputes that byte under a lock (single-byte store); the
hook is never unpatched.

Tests: `tests/WorkerProductionBonus.NativeTests/SightDispatchTests.cs` executes the
real wrapper, core and helper of a private image copy on a synthetic tile row with
the production stub patched in (hut transparent while dormant, blocking while active,
other types and the projectile mode unchanged, context write limited to `+0xC`,
eighth argument). `tests/APISharedTests/SightTests.cs` covers the result rules, the
rule combination of several owners and fail-closed input checks. No in-game
measurement or multiplayer acceptance exists yet.
