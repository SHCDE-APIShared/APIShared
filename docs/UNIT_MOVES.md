# Effective native unit moves

From APIShared 0.74.0, `APIShared.Units.UnitMoveEvents` exposes the actual final
unit-move Original boundary through its existing Script Extender owner. Consumers
must declare that minimum version before binding these types.

## Observation

After initialization, check `IsAvailable` and register an `IUnitMoveObserver` with
`TryRegister(ownerGuid, registrationId, observer, out reason, order, enabled)`.
Registration is process-lifetime with no replay or unregistration. Logical
activation uses the optional cheap, read-only enabled predicate. Ordering is
numeric, owner GUID ordinal, then registration ID ordinal. Callbacks run on the
native publisher thread; there is no Unity dispatch or implicit mutation/network
authority. A successful registration requires the published audited Original
adapter and common world lifecycle, independently of optional AI gameplay features.

`TryBegin(UnitMoveContext)` receives **effective** `UnitId`, `X`, `Y` and `Extra`
after mutable SE Pre processing. SE's legacy Post arguments may instead contain
the wrapper's initial unit ID and coordinates. Read required identity/state only
synchronously and revalidate all delayed work against the context's `SessionId`
and `WorldGeneration`. The invocation and parent IDs identify wrapper nesting,
not a multiplayer protocol. A nested Pre order can therefore reference the outer
wrapper even before that outer wrapper reaches its own original.

Return an observer-private `IUnitMoveInvocation` or null. Its `Complete` receives
the exact context with `NativeInvoked`, `NativeReturned`, the typed Int64
`NativeResult`, original `Failure`, and `IsCurrentWorld`. Receipts complete in
reverse registration order even if disabled after begin. Callbacks and reporting,
including exception formatting, cannot escape into the original. Originals are
never retried and native exceptions propagate unchanged during ordinary calls.
Observers have no veto/replacement return value. A callback replacing the world
before Original causes the now-stale Original to be refused rather than mutating
a reused unit slot. An observed wrapper also retains its world across legacy Pre.

The original outcome is captured before SE's mutable Post callbacks. Native zero
means a real matching native route refusal; a Pre veto, redirect or missing
Original does not. Consumers may use the native outcome to capture failed-route
state, but must establish their own source-specific native caller/identity proof.
A later Post can change the route, unit, world or managed return; the original
receipt alone is not final-route validation.

## Guarded single-wrapper calls

`TryRun(ownerGuid, unitId, x, y, extra, identityCurrent, invoke, out result,
out reason)` supports callers that already possess native execution authority.
It does not schedule work, take the engine monitor, submit a Chore or grant a game
mode. The invocation must synchronously call exactly one SE move wrapper.
The supplied identity predicate must be cheap and read-only, validating stable
unit identity rather than command fields the original legitimately changes.
Predicate exceptions are isolated and reject admission.

The expected outer wrapper is bound **before Pre**. A redirected final Original,
duplicate root wrapper, changed identity or replaced world is refused. Existing
AI admission guards remain additional prerequisites. Independent commands nested
inside Pre or Post keep their ordinary behavior; nested `TryRun` scopes are also
independent. Their contexts do not inherit the parent's `OriginOwnerGuid`.
Only the guarded wrapper carries that owner, enabling consumers to recognize their
own operation without suppressing unrelated commands during reentrant callbacks.

`UnitMoveRunResult` separates `NativeInvoked/NativeReturned/NativeResult/Failure`
from `WrapperReturned/WrapperResult/WrapperFailure`. Boolean true and status
`NativeSucceeded` mean exactly one matching current original returned one, even
when Post replaced the managed result with zero or threw afterwards. A managed
Post return of one cannot turn native zero into success. Invocation exceptions
are captured in this opt-in result rather than escaping the helper.
`NativeRefused` remains distinct from `NoOriginal`, `Redirected`, `MultipleCalls`,
`IdentityChanged`, `WorldChanged` and `NativeFailed`. Recheck final unit/route
state after the helper before restoring or parking anything.

## Ownership, lifetime and cost

`AiMilitaryOrderBroker` retains its single existing Original decoration. Its
UnitMove adapter is published separately before optional AI feature setup, then
the public broker is made available after the common world/resync lifecycle.
No second IL hook or native entry detour is added for this service. The existing
AI Before/After receipts, group-dispatch exclusion and individual-order invalidation
retain their behavior. Permanent hooks are never removed. `IsAvailable` and
guarded calls check the owner's latest `SafeIlHook.IsApplied`; a rejected later
IL rebuild cannot authorize an unobserved guarded call. Availability also requires
SE's native hook to have succeeded, remain installed and target the audited
0x196280 entry; SE's ContinueOnError setup can otherwise leave that hook absent.
The backend-generated readonly `_patchBytes` contract is checked once and cloned;
bounded live entry bytes and any indirect jump slot must continue to match it.
Foreign overwrites log an error and latch the source unavailable, even if RedBird's
IsInstalled metadata remains true. Startup copies are never self-approved from
already overwritten live bytes. No reflection, pattern scan or allocation occurs
during live comparisons; calls without enabled demand skip these reads entirely.

The native DLL is hash-bound to the common audited image. SE delegate/static
wrapper ABI, exactly one Original invocation and whole-wrapper return/exception
structure are validated before publication. The native move entry remains owned
by SE. No live copied-buffer probing is performed at startup.

Without demand, outer Enter/Exit and the actual Original owner add zero managed
allocations after warm-up: no frame, context, receipt, array or reflection call is
created per move. SE itself still allocates its ordinary event arguments. Active
cost scales with move invocations and interested observers, not all units/ticks.

`UnitMoveEventsTests` executes the production transform on the real installed SE
wrapper, replacing only the external native delegate/event publisher with managed
fixtures. It covers effective Pre inputs, actual Native versus mutable Post
results, ordinary and guarded failures, independent nested Pre/Post commands,
nested guarded origins, AI receipts, world/identity replacement, hostile callbacks
and reporters, negative IL/hash contracts and allocation-free dormant owner calls.
These offline tests do not establish in-game routing, rescue, multiplayer behavior
or a consumer's native mutation authority.
