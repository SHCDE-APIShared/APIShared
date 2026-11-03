# Enemy-unit proximity during building actions

`APIShared.Buildings.EnemyProximityPolicies` composes optional, owner-bound radius
policies on the existing Script Extender `OnBuildingAllowRepairInProximity` event.
The service does not install another native hook. It changes no building, wall,
Keep, terrain or direct-occupancy rule and does not edit the shared hostile-unit
list. With no effective contribution, the original native predicate executes.

Requires APIShared 0.25.0 or later.

## Usage: registering a policy

Register after native initialization, using your own stable GUID and an owner-local
registration ID. `TryRegister` returns a process-held registration, initially
disabled. `SetEnabled` changes logical participation; there is no unregister or
native unpatch operation. `HasRegistration` includes disabled registrations and
can identify a cooperating consumer without inspecting its implementation.

Callbacks receive a read-only value context: one-based player, tile coordinates,
original native radius, the existing unknown filter byte, and the civilian and
military radii resolved by earlier contributions. The underlying event does not
identify build versus repair versus demolition, or provide a full footprint.
Do not infer an action from the event name alone.

Return an `EnemyProximityDecision` with optional category radii and/or `Block`.
`null` means no contribution, while zero removes that category's distance test.
Public contributions accept `0..46340`; negative settings sentinels must be resolved
by the consumer before returning a decision.

1. Both categories start with the actual incoming native radius.
2. `Override` contributions replace only the supplied categories.
3. `Limit` contributions combine the supplied categories using `min`.
4. A valid `Block` remains authoritative over every radius contribution.

Within a phase, callbacks run by numeric order, then ordinal owner GUID and
registration ID. They execute synchronously on the original query's thread,
outside registry locks. Do not block, touch UI, send network packets, mutate the
game or retain a presumed action context. Multiplayer consumers must use matching
registrations and synchronized settings on all peers. The broker performs no
settings synchronization of its own.

A throwing callback or invalid decision contributes nothing; earlier valid
contributions remain and later callbacks continue. Recursive queries bypass policy
callbacks. If the raw incoming radius is outside the safe square range, no radius
replacement is performed; valid block decisions still apply. An unchanged final
pair leaves the original function in charge. A changed pair executes the prepared
native predicate once, without managed callbacks or allocations per examined unit.

An incoming Script Extender `SkipOriginalFunction` is respected. The broker's sticky
block guarantee covers its own participants: arbitrary later subscribers to the
raw Script Extender event are outside its ordering and cannot be controlled here.

## Limits: categories and native availability

Civilian membership preserves the audited worker taxonomy: production/extraction
workers, peasants, economic ox/horse transport and civilian castle residents and
entertainers. Engineers, tunnelers, fighting monks and Arabian slaves are military;
remaining threats considered by Vanilla use the military radius. All original
candidate, identity, death, state and Assassin/Bedouin exclusions are retained.

`QueryDiagnostic` reports native category-predicate availability independently of
registration and veto coordination. If the native copy cannot be prepared, radius
changes remain inactive and the original native predicate runs; valid policy
blocks still work. Unsupported native builds do not disable unrelated services.
The implementation and complete byte/layout contract are described in
[UpdateToNewDLL](../UpdateToNewDLL.md#enemy-unit-proximity-and-peace-time).

## Usage: reading active Peace Time

`APIShared.Simulation.PeaceTimeState.TryGetActive(out bool active)` reads Vanilla's
actual active flag in the current fully started mission, including an editor
mission with an active native peace state. Its return value is availability:
successful `false` is verified inactive peace; failed reads must not be treated
as either an active timer or as a reconstructed configuration value.

The getter is read-only and acquires no engine lock or thread dispatcher. Call it
from the existing native query/simulation context, or while the engine is stopped
and locked. Menu, pending/failed load and world replacement invalidate the read.
Saved state and expiry are authoritative; no timer, polling or notifications are
added. `Diagnostic` reports source availability separately from proximity queries.

## Usage: consumer example

A general-radius mod registers an `Override`. A peace-time mod registers a `Limit`
that returns `new EnemyProximityDecision(civilianRadius: 3)` only after a successful
active-peace read. Resolved civilian values 0, 1 and 2 remain unchanged; 15 becomes
3. Military radii and independent building rules are unaffected. The shared
library itself supplies neither this cap nor a default gameplay policy.

A consumer replacing an older direct Script Extender subscriber should publish
a stable registration ID even when logically disabled. Other consumers can then
refuse only their optional collaboration when the older implementation is present;
the shared broker does not special-case mod GUIDs or guess versions.

## Implementation and validation

Native comparison tests execute the production private predicate against the
original machine function, including boundaries, mixed categories, stale IDs and
existing exclusions. Registry and mission-reader tests cover composition, vetoes,
failure isolation and lifecycle invalidation. The complete copied native peace
updater is also executed with only its external display/sound edges replaced by
private native no-ops: equality retains peace, the following tick clears it, and
the production reader observes both states. These offline checks do not replace
game/editor/save-load or matching-build multiplayer acceptance. A small distance
does not prove that a more distant wall ring cannot enclose an isolated worker.
