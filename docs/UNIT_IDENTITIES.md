# Exact troop variant identities

`ModApiClient.UnitIdentities` supplies owner-bound identities for variants carried
by existing native units. It creates no native unit type, sprite, troop, purchase,
command or recruitment rule. Register during Unity startup after `WhenReady`,
before the first world is loaded. Every simulation peer must register the same
owner GUID, variant IDs and native carriers unconditionally.

```csharp
if (!client.UnitIdentities.TryRegisterVariant("ranger", (int)eChimps.CHIMP_TYPE_ARCHER,
    out var ranger, out string reason)) Log(reason);
// From your own proven native placement/purchase receipt, while holding the engine monitor:
if (!ranger.TryMark(unitId, capturedGlobalId, out reason)) Log(reason);
```

IDs are ordinal owner-local well-formed UTF-16 text, 1–128 nonblank code units. The numeric carrier
is an existing native type in 0–65535; it does not allocate a new native slot.
Duplicate owner/variant registrations are rejected. Definitions and callbacks
live for the process. Acquiring the client installs nothing; the first valid
registration starts the existing lifecycle, archive and native-tick observers.
The service adds no native hook and scans only marked slots.

## Exact identity and ownership

Marking validates a positive slot, nonzero **unsigned** GlobalId, Vanilla combat
liveness and the native carrier. The slot alone never identifies a unit. A repeat
of the same mark succeeds; another variant cannot replace it or remove it. A
conflict returns a reason naming the holding owner and variant. Registration order
is not a replacement policy: only an admitted deterministic simulation operation
can claim a concrete unit. Origins, appearance cycles, stats, loadout state and
other harmless metadata stay in each consumer's record and reserve no identity.

A paid recruit may still be a peasant when its native purchase returns. Marking
accepts that exact peasant only when `r_TransformIntoUnitOfType` already targets
the registered carrier. `IsPendingRecruitment` distinguishes that state. It
continues through save/load and final recruitment. The service does not establish
that gold/materials were charged or that a particular purchase created the unit.
The consuming recruitment adapter must supply that proof.

Death, slot reuse and an incompatible carrier/assignment/disband retire the mark
at marked-only reconciliation. No mark is removed merely because a delete request
was issued; a still-live exact unit retains it. Owner capture alone does not
rewrite variant ownership. A mod implementing a transformation decides whether
its old identity ends and explicitly marks its replacement. There is no automatic
transfer of another owner's identity or state.

## Context and presentation

`TryMark` and `TryUnmark` require the **already held actual engine monitor** and
a confirmed, known native world. In real multiplayer they also require an existing
shared native-tick or authenticated-Chore mutation scope. Taking the monitor on a
local UI thread is insufficient in multiplayer. Use identical deterministic
callbacks/settings on every peer; the service does not synchronize arbitrary calls,
validate purchases, grant player control or send Chores. Offline/editor callers
may mark from a native/editor operation while holding the monitor.

`TryGetLive` first applies staged data belonging to the current native world,
then revalidates the native identity under the caller's engine monitor. It is the
query for simulation decisions and grants no action authority.
`TryGet` reads an immutable published snapshot for presentation without locks,
allocations or native access. It needs the caller's independently observed slot
and GlobalId. Publication follows mutations and native-tick reconciliation; a
snapshot can be stale until the next tick and is never simulation authority.
`IsCurrentWorldReady` is a lock-free, nonmutating readiness query, true also for a
valid empty world. An earlier NativeLoaded observer may see false before the
identity observer applies restoration. Check readiness and use a later observer or
native tick for presentation; an engine-protected `TryGetLive` can apply the staged
restore immediately. A false `TryGet` can mean pending/unreadable restoration as
well as no matching identity.

World invalidation makes new queries fail immediately, including paused/loading
intervals. `GetMembers` returns a retainable read-only list ordered by slot;
previously returned lists remain point-in-time values after a world ends.

Stable reads and unchanged reconciliation allocate nothing after warm-up. Changes
publish fresh immutable dictionaries/member lists. This deliberately avoids
recycling public results that consumers may retain. There is no position/facing,
AI-state or general unit-table snapshot in this API.

## Save and load

One `VersionedSaveData` record, `APIShared.UnitIdentities.v1`, holds all owners with
scope `SaveGamesAndMaps` and `SimulationRelevant = true`. Editor variants therefore
travel with authored maps. Canonical bytes contain stable owner/variant text,
native carrier, slot and UInt32 GlobalId, sorted independently of registration and
marking order. No process registration index is persisted. The record is bounded
to 16,384 rows and a 1 MiB payload. Exact UTF-8 wire cost, including dormant rows,
is reserved before accepting a mark, so an accepted set remains savable. Unknown
envelope entries follow the existing
versioned-save preservation contract.

`Prepared` decodes complete private candidates without native world reads. After
a successful native load publishes its world identity, `NativeLoaded` (or Start)
validates each candidate against that loaded world's exact unit and carrier/pending
recruitment evidence before exposing it. Dead/stale rows are dropped. Unknown
owner/variant rows remain dormant exclusive reservations, are not returned by
queries, and are written back while their exact native identity remains valid.
This preserves an absent mod's identity without inventing behavior for it.

Preparation is tied to the active session and native-world token. An earlier
NativeLoaded observer's protected live read, mark, removal or save consumes the staged restore first;
later lifecycle delivery cannot overwrite it. Queued stale notifications from a
reentrant replacement cannot erase the new world's prepared rows.

BeforeLoad/end retire the previous world. `NoRecord` and `OutOfScope` restore an
empty set, preventing identity leakage between worlds with reused slots/GlobalIds.
Save capture checks the exact native-world token before and after reconciliation
and encoding, and again before writing; an invalidated world returns no replacement
payload. Malformed loads never partly apply. Unreadable/newer records disable identity
mutation/exposure for that load and retain original bytes through VersionedSaveData.
The service cannot veto a game load; consumers whose simulation requires identity
must gate their own feature if identity registration/restoration is unavailable.

The existing multiplayer record comparison detects differing copies of the saved
identity record before loading; it repairs nothing. It is not a running-state
checksum or registration-catalog agreement. Existing Byzantine/Ranger save formats
are untouched. A consumer migration must explicitly validate and mark its own
legacy identities at a permitted post-load boundary; no legacy record is guessed.

## Verification boundary

`Core.Tests.UnitIdentityTests` exercises the actual registry/codec using a native
reader boundary: unsigned identities, conflicts, paid pending recruits, world
replacement, canceled deletion, dormant records, canonical bytes, malformed loads
and zero-allocation reads. Installed interop validation pins the pending-carrier
field at offset 710, width 2, within the 1168-byte packed SE unit view. These checks
do not establish in-game rendering, purchase correctness or two-peer determinism.
