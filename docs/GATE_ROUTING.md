# Gate routing

## Access-rule contributors

`GateAccessRules` is a passive registry. It installs no hooks, starts no scans and
contains no default enemy-gate rule. A routing provider (currently BugfixesAndQoL)
evaluates it while publishing immutable player masks. Registration alone has no
gameplay effect without such a provider.

Register once with your mod GUID and a stable registration ID. The copied query
contains a verified player, gate Building/Global ID, original owner, capturer and
the preceding access result. Entries are evaluated in ordinal GUID/ID order;
the last valid explicit `Allow` or `Deny` wins. `Unchanged` retains the result.
An invalid identity never reaches a callback. Exceptions discard only that
contribution; an optional publisher error reporter cannot change the decision.

Callbacks execute synchronously on the snapshot-publisher thread, outside the
registry lock. Keep them pure, quick and deterministic. Do not query paths, issue
commands, mutate native state, register further rules or invalidate from inside
a rule. Publish your own immutable rule data first, then call
`Invalidate(ownerGuid, registrationId)`. Providers compare `Version` around
snapshot construction and before publishing or reusing cached routes.

Each mod synchronizes its own gameplay settings/data between multiplayer peers.
Neither local UI state nor diagnostic settings may affect access. There is no
unregister operation: return `Unchanged` when your feature is inactive.

**Allow removes only the extra routing restriction.** Native physical closure,
collision, building identity checks and the ordinary search result remain
binding. Permission does not prove a climb transition. Deny applies to additional
gate-passage masks; it does not globally close a building or suppress attacks.
Drawbridge crossing is a separate feature.

The independent [example](../examples/ThirdPartyMod/GateAccessExample.cs) shows
both an allowance and a restriction without depending on BugfixesAndQoL types.

## Advanced native provider

`APIShared.GateRouting.Native.GateRoutingNativeHooks.TryRequest` validates the
installed native contracts and reserves one owner GUID/registration ID. It does
not install hooks. This low-level backend is for a routing provider, not ordinary
rule contributors. Existing `EnemyGatePathPolicyBridge.TryRegister` remains
passive and keeps its old contract.

The explicitly invoked `InstallComparisons`, `InstallMovement` and `InstallDescent`
methods own their audited native spans permanently. They retain delegates and
transactions; installation must not be retried after a failed publication.
Callbacks follow the exact native ABI documented on their declarations and must
not throw. The capturer context secures the comparison in R11b; Rflags is not a
valid decision transport. A comparison may add rejection but cannot turn a
native rejection into access. The descent callback only bypasses the audited
premature route-clear; subsequent native step checks remain authoritative.

`GateNativeSearchScopes` owns the shared scope layout and pairing primitives.
Generic slots require 2048 bytes (64 x 32), tactical slots 8192 (64 x 128).
Masks are immutable 320800-byte direction arrays. Buffers and callbacks must live
to process exit. Each successful enter must have a same-thread leave in finally;
a collision preserves native behavior. Logical activation changes data only.

The backend does not install the existing builder/attack/building/cursor-command
function detours. The current provider connects those through their existing
owner and the public policy bridge. Providers must audit other installed owners
before acquisition. A callback must invoke its corresponding original exactly
once where its contract requires an original call. Returned original delegates
are available only after successful movement installation.

## Compatibility and validation

The temporary route-observer bridge retains its existing `Current` identity.
`CurrentRequested` is an additive demand-aware accessor; existing registrations
start enabled. Only the registered observer can use `SetObservationEnabled`.
Consumers retain captured observers for paired completion and check demand before
creating diagnostic contexts, strings or route copies.

All additions are additive; existing gate-policy and observer interfaces are
unchanged. No existing registration implicitly installs these hooks. Native
byte/layout contracts, actual RedBird displacement and permanent publication
remain authoritative. Unknown builds fail acquisition.

Independent registry tests cover ordering, duplicate keys, passive registration,
invalid identities, per-contributor failures and invalidation. Consumer fixtures
cover immutable masks, cache identities, inline register/flag preservation and
native execution. Local migration builds retain the current test version; the
release minimum must be raised to the eventual API release containing this API
before distribution. Do not infer availability from an older package sharing a
local development version.
