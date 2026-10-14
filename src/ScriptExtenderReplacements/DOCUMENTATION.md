# Opt-in Script Extender replacements

This area contains public replacements for specific Script Extender methods.
Unlike the automatic corrections in `ScriptExtenderFixes`, these methods do not
hook another assembly's code. Mods choose to call them explicitly. Existing SE
callers keep their behavior until their authors migrate.

## Pathfinding tables: why migrate?

The audited SE 2.14.1 implementation uses `eChimps.CHIMP_NUM_TYPES` (89) both for
enum validation and for native table sizes and row offsets. Vanilla's native
permission rows have 90 Int32 slots. Consequently, SE class rows 2 through 6
start 1 through 5 values too early. Its Can/Set methods inherit the incorrect
address; a setter can change another class/type's permission. For example,
class 2 / archer reads offset 111 instead of 112 from the class-one table base.
The canonical values at those offsets are 0 and 1 respectively.

The flat SE spans expose only 89 profiles and a 534-value permission prefix.
Those sizes do not establish corruption, but cannot cover the full 90/540-slot
storage. The replacement separates enum validity from native capacity. It does
not add a new unit type or change the SE constant.

## Migration

Add a hard dependency on an APIShared package containing this API, reference its
public assembly with `Private=false`, and call after APIShared's readiness
notification. This feature is currently under development without a version
increment: the existing 0.9.0 release does not contain it. Use the rebuilt
development package for testing; select the actual containing release as your
minimum dependency before publishing a consumer.

```csharp
using CorrectedPathing =
    APIShared.ScriptExtenderReplacements.GamePathingManagerAPI;

var pathing = CorrectedPathing.Instance;
if (!pathing.IsAvailable)
{
    Log(pathing.UnavailabilityReason);
    return;
}
var permissions =
    pathing.GetUnitTypePathfindingConnectionClassPermissions(connectionClass);
```

Replace the manager type or qualify these calls with the alias. Method names,
parameters and return types match the SE:

| Method | Replacement behavior |
|---|---|
| `GetUnitTypePathfindingProfiles()` | 90-slot writable live profile span |
| `GetUnitTypePathfindingConnectionClasses()` | 540-slot writable live span, classes 1..6 in 90-slot rows |
| `GetUnitTypePathfindingConnectionClassPermissions(connectionClass)` | 90-slot writable live row, offset `(class - 1) * 90` |
| `GetUnitTypePathfindingProfile(unitType)` | Same single-type behavior as SE |
| `SetUnitTypePathfindingProfile(unitType, profile)` | Same single-type behavior as SE |
| `CanUnitTypeUsePathConnectionClass(unitType, connectionClass)` | Nonzero permission at the corrected address |
| `SetUnitTypeCanUsePathConnectionClass(unitType, connectionClass, canUse)` | Writes 0 or 1 at the corrected address |

Only these seven methods and their relevant constants are provided. Keep other
pathing operations on the SE manager; a wholesale alias replacement in a file
using unrelated manager methods requires separating those calls.
See the [compiled example](../../examples/ThirdPartyMod/PathfindingReplacementExample.cs).

## Public contracts and limits

- `PATHFINDING_UNIT_TYPE_COUNT` stays 89. Typed getters/setters accept 0..88;
  `CHIMP_NUM_TYPES` and larger values are rejected. Native slot 89 is exposed
  only through spans. `NATIVE_PATHFINDING_UNIT_TYPE_CAPACITY` is 90.
- Classes 1..6 are supported. Invalid class/unavailable span calls return empty;
  invalid/unavailable bool calls return false. The profile getter returns
  `default(PathfindingProfile)` (Large, zero), not the named Default profile.
  Profile setters accept only Large or Default and do not write on failure.
- These are borrowed writable native views, not snapshots. Use them immediately
  on the appropriate game thread; callers own synchronization. Writes apply
  process-wide to subsequent searches, without rebuilding existing paths,
  changing managed overrides or synchronizing multiplayer settings.
- Initialization only publishes validated addresses; it never resets values.
  Legitimate changes by Fixes or any other mod remain visible. There is no
  fallback to the affected SE methods and no automatic correction of other mods.
- `Instance` is safe before initialization but returns unavailable results.
  `IsAvailable` and `UnavailabilityReason` describe this service independently
  of other APIShared capabilities. Unsupported images remain unavailable.

## Implementation and maintenance

The immutable address state is published once through APIShared's existing
`LibraryLoaded` initialization, before readiness callbacks. The singleton roots
it for process lifetime, including after BepInEx startup cleanup. No hooks,
polling, plugin component callbacks or executable memory writes are needed.
Availability requires the audited native image, complete in-module ranges,
the supported enum contract (including underlying widths and all six class
values), and agreement with SE-resolved table addresses.
Mutable table contents are deliberately not compared during initialization.

Native update evidence and exact layout are recorded in
[UpdateToNewDLL](../../UpdateToNewDLL.md#pathfinding-table-replacements).
After an SE update, recheck public address resolution and enum semantics rather
than accepting or rejecting it solely by a version label. After a native update,
re-audit the readers and capacity before supporting another image.

The [isolated tests](../../tests/APISharedTests/PathfindingReplacementTests.cs)
exercise real replacement methods against owned unmanaged buffers, all class/type
pairs, boundary slots, guarded writes, foreign values and unavailable states.
They do not prove in-game path-search outcomes. Runtime acceptance additionally
requires the availability marker and a complete consumer comparison after
startup cleanup.

## Manual acceptance in the game

1. Close the game. Run APIShared's `build.bat /nopause`, then the consumer's
   normal build driver, using this development package. The version label alone
   cannot distinguish the development API from the published package.
2. Start the game. If the Script Extender asks to apply staged mod changes and
   restart, choose **No** for this test. The installed 2.14.1 dialog explicitly
   discards staged changes and continues playing on this branch. Choosing Yes
   may replace these development files with the subscribed Workshop release.
3. Enable Bugfixes and QoL and its enemy-gate pathfinding correction. Its config
   section `[GateMigrationDiagnostics]` must have `DetailedDiagnostics = true`
   before startup to log a successful comparison (it currently defaults to true).
   Load an ordinary skirmish or save and let it run for a few seconds.
4. In the newest BepInEx startup section, check for
   `SE_PATHFINDING_REPLACEMENT available: profiles=90, permissions=540, rowStride=90, validUnitTypes=0..88`.
   After loading the map, check for
   `APIShared pathfinding globals match the canonical FBCB9319 process-start tables`
   with `profileLength=90/90`, `permissionLength=540/540`,
   `profileMismatches=0`, `permissionMismatches=0` and `invalidValues=0`.
5. The old 89/534 coverage warning, replacement-unavailable warning and access
   exceptions must be absent in that same startup section. If another mod
   intentionally changes table values, the changed-value warning can be valid;
   keep its exact mismatch samples for review rather than resetting settings
   or assuming an addressing defect.
6. A second map in the same process should work normally. The comparison is
   intentionally once per process and need not repeat on every map. A fresh
   process repeats initialization and comparison after the next map load.

Normal movement is a smoke test, not proof of every permission writer. The
consumer only reads these globals. Exact setter targets, class/type boundaries,
invalid inputs and preserved adjacent values are verified with the isolated
buffer tests; do not mutate live permissions merely to reproduce that test.
