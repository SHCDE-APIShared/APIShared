# AI military admission

This guide covers the optional `ModApiClient.TryGetAiMilitary` capability and
`APIShared.Units.AiMilitary`. It is intended for placement tools and map authors.
Acquiring the capability does not transfer control of existing armies.

## Consumer workflow

Acquire the capability after `ApiShared.WhenReady`. Check `GetOptions` for the
owner and primary unit type. The result is a snapshot; admission repeats the checks.
The owner is a one-based player ID, while `AiUnitRef` always includes both the
unit slot and nonzero native global ID. Semantic roles are separate from native
unit-role numbers, tribe IDs and player storage slots.

For a placement invocation, hold the actual game engine monitor, call
`TryBeginSpawnScope`, execute the ordinary native placement, call `Complete`, and
dispose the scope in `finally` before releasing that monitor. Capture is scoped to
that thread, invocation, owner and primary type. It uses the actual native creation
result before mutable Extender Post callbacks. Uncompleted scopes remove only
their own captured units with matching identities.

`Complete` returns an initial result per captured primary unit. `Queued` is not
success: the service waits for initialization and the completed engine-run safe
point. The completion callback reports `Applied` or `Rejected` once, including a
world-end rejection. Editor declarations report `Declared` synchronously instead.
Callbacks are exception-isolated and can run on the simulation caller. Dispatch
presentation work to the UI thread. A callback may end the world; retain neither
native pointers nor assumptions about the next callback's world.

`RequestAssignment` accepts a free existing unit or this consumer's protected unit.
It refuses arbitrary already-managed troops and units claimed by another owner.
`TryDeclareMapAssignment` records an existing editor unit for the next map start.
`TryGetState` reads a tracked result only while identity, type and owner still match;
it does not continuously certify every subsequent Vanilla action.

Local placement and reassignment are refused in real multiplayer. Map-authored
assignments use the deterministic load path on every peer instead. Existing
consumers are unaffected unless they acquire/use this capability or load its records.

## Roles and limits

Native group selection is automatic. The capability deliberately exposes semantic
roles rather than a numeric native tribe picker.

| Area | Roles | Type and configuration requirements |
| --- | --- | --- |
| None | `None` | Military units receive a private unmanaged group and role `65535`. Civilian and human-owned placement keeps its ordinary behavior. |
| Defense | `CastleDefense`, `DefensePatrol`, `EconomyProtection`, `Intervention` | Combat types; patrol count must be 1–10. Castle defense uses the existing native/Fixes guarding-position resolver. |
| Defense | `HomeMoat` | A native digging-capable type. |
| Harassment | `Harassment` | Combat types or supported fully crewed mobile machines. |
| Harassment | `HarassmentEngineers` | Engineers; the physical pool/build guard must retain space for the missing regular two-engineer quota. |
| Siege | `SiegeEngineers` | Engineers in the native construction pool. |
| Siege | `SiegeMoat` | Digging-capable type matching the AIC moat troop. |
| Siege | `SiegeAssassins`, `SiegeLadders`, `SiegeTunnelers` | Assassins, laddermen or tunnelers respectively. |
| Siege | `SiegeDiversion`, `SiegeStorm`, `SiegeCover`, `SiegeShock`, `SiegeReserve`, `SiegeWall` | Configured AIC troop/list must match. Storm groups: 1–2; cover: at most 3; reserve: at most 2; wall: at most 8. Cover/reserve/wall use their native base group when the configured count is nonpositive. Healers are excluded from these assault roles except cover. |

Digging-capable types are archer, spearman, pikeman, maceman, engineer, slave,
eunuch, skirmisher, sapper and demolisher. Mobile machine admission requires two
engineers for a catapult/Arabian ballista, four for a tower/ram or one for a shield.
The machine and every required crew identity must be alive and owned by the same
player. Vanilla mapper creation does not create these crews, so new uncrewed
machines cannot select an AI role. No crew is created by this API.

Building-bound oil, defensive-machine and outpost roles, worker roles, automatic
role zero and the construction-product role are not manual admission targets.
The independent StartConditions isolation implementation remains unchanged.

## Current orders and failure

Admission establishes membership, leader relations, role and the recruit's current
order. It never rebroadcasts a group order to existing members. Observations record
effective accepted native arguments, not reconstructed Extender Post arguments.
Moving/patrolling/attacking recruits get their own route. Strategic work selects a
fresh eligible job and reserves it only after successful setup; paths and jobs are
never copied from a leader.

A genuinely empty newly allocated group can use its verified initial order. An
existing group's unknown, expired or incompatible order is rejected explicitly;
there is no wait until its next AI command. Strategic intent additionally checks
the current siege phase, target identities, AIC, group storage identity and native
task-list ownership. Unsupported transitions and exhausted/unreachable tasks fail
without changing existing members. A failed fresh placement removes that captured
unit; failed existing-unit admission restores its owned pre-admission state.

`AiStrategicOrderEvents` and `AiGlobalTaskEvents` offer ordered, immutable,
exception-isolated observations at shared process-owned boundaries. Their before/
after pair has an invocation ID and completion state; observers cannot cancel,
rewrite or replay native work. Consumers must not hook the same native entries.

## Additional troops

`Regular` participates in the ordinary recruitment quota. `Additional` remains a
real troop for strength, readiness, combat and physical capacity, and is excluded
only from recruitment-quota comparisons. `None` normalizes to regular. Identity
markers and the same native fold's additional counts are separate from Vanilla's
real count vector. The service never subtracts additional troops globally.

A constructed machine inherits additional status only if all contributing
engineers throughout its construction were additional. Mixed construction stays
regular; engineers keep their own markers. History is bounded and persisted. The
audited Fixes replacement operation carries the exact source machine's classification
to its verified replacement, including delayed crew attachment. Unrelated crew
reuse cannot inherit that status. Explicit admission of a fully crewed existing
machine can classify that machine independently of its construction history.

For harassment engineers, admission reserves the missing regular quota and requires
the resulting physical population plus that reserve to remain below 20. The native
20-unit build guard and greater-than-20 retirement behavior remain unchanged.
This is a check of the current pool, not a promise of unlimited later production:
future construction crews and recruitment still share those physical native limits.

## Maps and saves

Editor placement defaults to declarations; the caller can request immediate editor
application. Final map owner/AIC checks are deferred until map start, even if the
editor currently has a different AI profile. Invalid final assignments remain
protected declarations with an explicit failure; human ownership takes precedence.

The versioned simulation record stores identity, declaration/result state,
additional markers, construction history, the matching quota snapshot and accepted
orders. An ordinary save restores its actual current state; it does not replay the
original authored role over later Vanilla changes. Missing/deleted identities are
discarded. Unknown/corrupt records retain their original archive bytes and refuse
native initialization if early protection cannot be established.

Early archive preparation happens before native initialization. All authored units
are quarantined before any detach operation. A foreign veto or inconsistent saved
private group aborts the load through the audited native failure path before AI
work can run. Records and markers are reconciled under the real engine monitor.

Multiplayer new-map starts compare map-scoped simulation record digests through a
separate readiness channel before Vanilla GO. Every human peer participates even
when the host has no records. Admission binds roster, map, AIV/settings and each
peer's whole selected file; the native phase accepts only the immutable confirmed
ticket and unchanged local file. This supplements existing save readiness rather
than assuming the ordinary map-header CRC covers appended mod records.

## Verification

Native fixtures execute the actual generated detours, inactive gates and ABI/flag
contracts against the supported native image. Additional suites cover accepted
order lifetime, attack initialization, quota folding, persistence and actual
Extender wrapper transformations. These are offline checks, not real-game or
host/client acceptance. Spectator `/devdiag` builds provide bounded per-assignment
expected/actual identity, role, group membership and additional-marker checks;
normal builds exclude that temporary diagnostic implementation.
