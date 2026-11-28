# Shared unit relocation

APIShared0.79.0 provides `APIShared.Units.UnitRelocationService`, a single owner of the audited native relocation entry. This is a low-level shared operation, not a path search or a general worker-state recovery algorithm. Consumers retain their settings, destination selection, AI/job admission, network authority and command continuation.

## Startup and compatibility

Declare a dependency on APIShared0.79.0 or newer. Call `EnsureAvailable(ownerGuid, out reason)` during your LibraryLoaded initialization, after APIShared initialized. Cold acquisition is allowed only on that startup thread before the first native mission initialization. Once installed, acquisition queries the existing owner; it does not repatch. The hook, callbacks and registrations remain rooted until process exit.

APIShared binds metadata without claiming or patching this entry. Updating APIShared alone therefore does not install a competing hook over older private consumers. If another mod already owns or modified the required span, acquisition is refused with a diagnostic. Do not chain, overwrite or adopt an unknown owner's stub. Older private relocation hooks and a newly requested shared owner cannot be combined silently; update the consumer to this service. No automatic runtime takeover or unpatch is supported.

`IsAvailable` checks the actual published entry, complete generated stub, unchanged helper continuation and standing-recovery call anchor. A lost owner, changed executable mapping or changed bytes permanently revokes availability. Restoring bytes later does not reactivate the service. Error logging is independent of this state, so an already reported overwrite cannot qualify as healthy. Failures affect this service, not unrelated APIShared capabilities.

## Read-only veto policies

`TryRegisterPolicy(ownerGuid, registrationId, unitType, shouldCancel, out registration, out reason, order)` registers a process-lived rule, initially disabled. The owner and ID must be nonempty and unique as a pair. Null `unitType` means all native UInt16 types; an exact type filter avoids entering managed code for other units.

Call `registration.SetEnabled(bool)` after every owned settings/session transition. `registration.IsEnabled` reports logical activation, independently of service health. Activation updates native interest before publishing an enabled rule; deactivation disables the rule before clearing unused interest. A single-type change touches one native interest cell. No executable bytes change.

Rules execute synchronously on the native caller thread, outside registry locks, in ascending order then ordinal owner/ID order. Returning true vetoes the Original. All interested rules still run after an earlier veto; veto decisions are combined. Exceptions are isolated and logged once per registration, and do not veto Vanilla or stop independent rules. Registrations added during a callback join a later invocation. Callbacks must be cheap and read-only: no game-state mutations, blocking, UI access or recursive explicit relocation.

`UnitRelocationContext` contains immediate identity, controller, destination, base height and semantic origin. Retaining it grants neither native lifetime nor mutation permission. Origin is captured at the actual native entry, before any managed wrapper:

- `Native`: an ordinary native caller, including independent direct native calls.
- `InvalidStandingTerrainRecovery`: the audited Vanilla caller that rescues units from invalid standing components.
- `ExplicitApiRequest`: the exact synchronous frame issued through `TryRelocate`; it is never guessed from a wrapper's return address.

`OriginOwnerGuid` attributes an exact explicit API request to its cooperative caller; it is null for native calls and is not authorization. Raw return addresses and scanners are private implementation details. A rule such as restricting a Lord's home-return fallback belongs in its consumer and can exempt the standing-recovery origin without knowing an RVA. Uninterested unit types execute the displaced originals and continuation without a managed callback. Incoming volatile registers, XMM0–5, flags and the stack height argument are retained across the policy bridge.

## Explicit relocation

Construct `UnitRelocationRequest` with current WorldMutationEvents session/generation, one-based unit ID, exact nonzero GlobalId, native type, controlling player and destination local tile coordinates. These local world identities must not be serialized as multiplayer peer identities.

`TryRelocate(ownerGuid, request, out result, out reason)` requires all of these on its calling thread:

- the existing admitted SimulationTickEvents mutation scope;
- the audited engine monitor already held;
- the same identified, fully started native world, outside an open construction/source batch;
- the original living unit lifetime/type/controller and supported ordinary unmounted state;
- coherent bounded source/destination occupancy with exactly one source membership and no shared suffix/cycle between distinct tile lists.

A world read alone or an engine lock alone grants no permission. The service does not dispatch, acquire locks for the caller, queue later work, perform network transport or grant multiplayer authority. A deterministic synchronized caller is still responsible for its mode policy; this API addition is not multiplayer gameplay qualification.

World, identity and occupancy are checked again after policies immediately before Original. Mounted, portal/transport, hidden or nonzero unclassified auxiliary-occupancy states are refused; handling such states needs their own audit. Neighbor units in an occupancy list may legitimately be mid-step, so their inactive packed Current/Next caches are not treated as a generic corruption test.

A cancelled or rejected request has `NativeInvoked=false`. `NativeReturned` says whether the admitted void Original returned normally; it is not by itself a successful move. `VerifiedRelocated` checks current/next tile pose, fine coordinates, height, midpoint reset, original identity and resulting source/destination membership. Same-target success is allowed. No status promises a resumed job, a valid subsequent route, new crew or a physically changed coordinate. Inspect `Status`, `NativeInvoked`, `NativeReturned` and the reason separately.

## Native update and validation

The implementation remains bound to the canonical supported game image. The concrete entry, dependent layouts, caller anchor, occupancy semantics, stack/register contract and source/destination validation are listed in `UpdateToNewDLL.md`. A new native image requires a feature audit; a matching prologue alone does not qualify the unit/grid layout.

The existing APIShared test project includes production-kernel admission/occupancy/identity/origin and sticky-integrity tests plus installed-backend native bridge fixtures. These execute only private fixture pages, including pass-through, veto, full UInt16 type filtering, fifth stack argument, original caller and register/XMM preservation. They are not in-game acceptance of every mod policy or special unit state.
