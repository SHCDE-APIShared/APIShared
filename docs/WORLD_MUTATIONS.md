# Effective world-mutation observations

`APIShared.WorldMutations.WorldMutationEvents` is a process-owned synchronous
observation broker available from APIShared 0.74.0. Consumers must declare that
minimum version before binding these types. It supplies effective native arguments and original-call
outcomes for construction, editor terrain operations, moat work and demolition.
It does not calculate affected tile envelopes, search units, authorize rescue,
or change game state. Those policies belong to consumers.

## Registration and availability

After APIShared's native initialization, check `AvailableSources`, then call
`TryRegister(ownerGuid, registrationId, observer, out reason, order, sources, enabled)`.
Registration is process-lifetime; use the optional nonthrowing `enabled` predicate
for logical activation. There is no replay, unregistration, background queue or
implicit UI dispatch. Registration fails before any source is available; requesting
a mask does not promise every bit initialized. Require the necessary source bits
explicitly, and recheck availability after mission/editor start.

Callbacks execute on the invoking simulation/editor thread. UI work must use a
separate supported UI scheduler. Order is numeric, owner GUID ordinal, then
registration ID ordinal. Registration changes become visible at the next batch.
Each observer's `TryBegin(context)` can return its own `IWorldMutationInvocation`.
Only that receipt receives `Complete(outcome)`, in reverse begin order. Observer,
enabled-predicate, receipt and reporter failures are isolated from the original;
one diagnostic per registration is attempted. Disabled observers receive no new
begin or batch publications; an already-created receipt still completes.

## Invocation and outer batch

`TryBegin` runs immediately before the **actual original**, after earlier hooks
have selected its effective arguments. It is not the SE public Pre event. For
construction, observation is inside the existing cost owner's final-original
wrapper, independent of cost-policy demand. No extra native entry hook competes
with the extender for those sources.

`WorldMutationCompletion` records `OriginalInvoked`, `OriginalReturned`, integral
`ReturnValue` and the original `Failure`. Normal return does not mean placement
succeeded. Exceptions propagate unchanged and originals are never retried.
Each child has an invocation ID, parent invocation ID and outer batch ID. Mirror
siblings share the batch even though the extender runs them before public Pre.

`OnBatchCompleted` runs after the outer wrapper's Post/finally processing. For
AIV it encloses the entire native build-step original, including its cleanup and
tail call; nested build observations do not independently close that AIV batch.
The batch callback is supplied to every enabled registration in the batch snapshot;
the source mask selects invocation begins, not batch callbacks. Consumers should
ignore batches for which they accumulated no work.

**Batch completion is not general permission to mutate native state.** Calling
frames outside the observed function can still be active. In particular, a
moat-worker handler updates actor state after the dig/fill helper returns. Consumers
must establish their own permitted caller boundary, actor exclusions, pointer
freshness, geometry preconditions and game-mode authority before writing anything.
The generic API deliberately does not expose an executing actor ID: the native
current-unit context is not reliable for all fill callers, including Chore 41.

## World lifetime

`TryGetCurrentWorld(out sessionId, out worldGeneration)` returns false outside an
identified native world. Session is the central lifecycle attempt identity.
Generation also changes when native-world identity changes and on initialization,
mission retirement and multiplayer repair invalidation. During resync the world
is unavailable. IDs are process-local; do not synchronize them as peer identities.

Capture both IDs with delayed consumer work and discard stale work. The batch's
`IsCurrentWorld` must also be true. A current batch alone does not establish that
all pointers/units retained by a consumer remain valid. Raw addresses in contexts
are invocation-scoped opaque scalar values; never dereference them later.

## Sources and scalar argument order

`GetArgument` retains every effective scalar, including argument zero. These are
the real native/SE signatures, not a normalized region. Mapper and other domain
integers must be interpreted through the selected SDK enums.

| Source | Arguments in order |
| --- | --- |
| AivBuildStep | AIV context address, player ID, frame index, restricted, free build |
| BuildStructure | tile manager, player ID, X, Y, mapper, size, direction, remaining native flags |
| HumanWall | tile manager, player ID, X, Y, end X, end Y, mapper, remaining native flags |
| AiWall | tile manager, player ID, X, Y, mapper |
| EditorTileType | tile manager, brush size, center tile ID, center Y, a5, tile property, tile type, a8 |
| EditorTileTypeClear | tile manager, center tile ID, center Y, brush size |
| EditorHeightPreset | tile manager, center tile ID, center Y, brush size, height preset |
| EditorHeightRaise | tile manager, center tile ID, center Y, brush size, raise modifier |
| EditorHeightNumber | tile manager, center tile ID, center Y, brush size, signed short height |
| EditorHeightEven | tile manager, center tile ID, center Y, brush size |
| EditorDelete | tile manager, X, Y, brush size |
| MoatDig / MoatFill | tile manager, task slot |
| BuildingRemoval | building manager, terminal building ID |
| WallRemoval | tile manager, player ID, tile ID |

Typed convenience properties select only applicable fields: `FrameIndex`, `X/Y`,
`EndX/EndY`, `Mapper`, `Size`, `Direction`, `TaskSlot`, `ObjectId`, `TileId`,
`PlayerId`, `NativeContextAddress`. Unavailable coordinates use -1, task/object/
player IDs use zero. `Size` is not a guaranteed complete mutation envelope.
CastlePlanner or other earlier handlers can affect effective arguments and final
building direction; consumers must inspect the created compound where needed.

## Native ownership and compatibility

Managed adapters decorate the extender's existing Original delegate and outer
wrapper with permanent, rooted IL hooks. Human construction and terminal building
removal cooperate with the existing `HumanConstructionRuntime` owner. Other entry
detours are not installed for these sources. All seven editor root wrappers and
their generated mirror closures are covered.

Only the previously unowned dig/fill helpers require native hooks. They use the
audited native DLL hash, full pristine function hashes, bounded Indirect backend
scans, exact whole-instruction six/ten-byte entries, native-site claims, live
foreign-patch guards and permanent publication. Entries are revalidated at mission
and editor start. A changed entry disables its source and logs an error; no
published hook is removed or rewritten. An independent foreign hook beginning
exactly at the audited continuation boundary remains allowed.

Unknown native hashes and incompatible managed IL fail affected sources closed.
Fixes' unrelated keep-mounted-unit rescue remains owned by Fixes. The broker does
not replace foreign settings or repairs. Observation does not grant multiplayer
mutation authority; consumers must separately establish deterministic/repair-safe
behavior and enforce their permitted game modes.

Without active demand, no context, receipt, argument array, reflection invocation
or TLS dictionary is created per call. Generated typed delegates call their
original directly. Active work scales with mutation invocations and interested
observers, not the total number of units or map tiles.

## Offline verification and remaining game validation

The production registry is source-linked into `Core.Tests.WorldMutationTests`:
private receipts, nested/sibling batching, ordering, original failure, hostile
callbacks/reporters, world identity invalidation and zero-allocation dormant calls.
`WorldMutationWrapperTests` transforms/JITs actual installed extender wrappers and
mirror closures, and executes the production original/scope adapter across
Mirror→Pre→Original→Post, wrapper failure and original failure.
`WorldMutationNativeTests` verifies full canonical moat bodies and instruction
boundaries, then exercises the production native backend policy with both exact
entry prefixes in private executable fixtures. No game hooks are initialized.

These checks do not establish in-game placement, tile-envelope completeness,
rescue correctness, caller-frame write safety, multiplayer determinism or repaired
world behavior. Those require separate consumer audits and controlled game tests.
