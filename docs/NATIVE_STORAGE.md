# Native pooled storage for custom goods

## Usage

`ModApiClient.NativeStorage` registers virtual goods that occupy native Armory or
Granary space. Each good retains its owner GUID and local ID; no native `eGoods`
slot is invented. APIShared owns one private goods ledger and per-building
allocations together. Consumers cannot mutate that inventory independently of
physical allocations.

Register once on initialized Unity startup, after API readiness and before the
first mission. Both owner GUIDs and local IDs follow the printable-ASCII limits of
[`CustomGoodId`](CUSTOM_GOODS.md). Choose stable IDs rather than translated labels.
The first registration installs one process-lived capacity cohort and versioned
save handler; merely obtaining the client does nothing.

```csharp
if (!api.NativeStorage.TryRegisterGood("medium-armor", NativeStoragePool.Armory,
        out var armor, out string reason))
    Log(reason);
```

Armories provide **50 pooled slots**, including all native local counters and all
owners' registered virtual goods. Granaries provide **250 pooled slots**. Their
capacity comes from the audited aggregate rules, not `r_MaxGoodStackAmount`.
Stockpiles have different per-good rules and are outside this contribution.

### Protected observation and exchange

`TryObserve(buildingId)` requires the actual engine monitor and a confirmed known
native world. It returns an immutable snapshot with unsigned GlobalId, owner,
pool, native occupancy and combined virtual occupancy. This is a momentary view;
an exchange revalidates the building's alive state, type, owner and identity.
Ownership can change without changing GlobalId.

Call `NativeStoredGood.TryExchange` inside the same protected authoritative
simulation boundary. A positive quantity deposits; a negative quantity withdraws
from that exact building. The signed gold delta is explicit: negative buys,
positive sells, zero handles a verified delivery or consumption without reading
or writing gold (existing debt therefore does not block a free delivery). Supply pricing,
player authorization and native-success evidence in the consumer.

```csharp
// Already inside an authorized native tick or authenticated Chore callback.
if (api.NativeStorage.TryObserve(buildingId, out var store, out reason))
    armor.TryExchange(store, operationSequence, quantityDelta: 5, goldDelta: -100,
        out var status, out reason);
```

The operation takes the whole lot or none. It cannot withdraw one good merely
because another good occupies that building. All owners share the physical pool;
one owner cannot spend another owner's goods. Gold is the observed building
owner's checked signed native balance. The caller remains responsible for
authorizing control of that player.

Positive sequences belong to the registering owner. `TryGetLastSequence` reads
the current high-watermark in an authorized simulation context. Allocate and
execute in one deterministic stream; that read reserves no number. Retained exact
retries return `AlreadyApplied` without another gold or goods effect. Retain the
original snapshot, sequence, quantity and price for an in-world retry; re-observing
a destroyed or captured building cannot recover that old observation. Changing
the building, price, player or quantity conflicts. Expired sequences reject and
never execute again. Automatic storage-loss reconciliation also consumes owner
sequences, so allocate them at execution time, not from an earlier GUI observation.

Real multiplayer writes require a native-tick or authenticated Chore mutation
context as well as the engine monitor. The service transports no commands and
does not authenticate the sender. Consumers register identical catalogs and make
equivalent ordered decisions on every peer.

See the [compiled example](../examples/ThirdPartyMod/NativeStorageExample.cs).

### Atomic multi-store exchanges

Use `api.NativeStorage.TryExchangeBatch` when a purchase or sale must succeed
across several stores together. Supply `NativeStorageChange` values containing
the exact registered good, original storage snapshot and signed quantity delta,
plus one aggregate signed gold delta. A 100-unit purchase can deposit 50 in each
of two Armories and charge once. Looping individual exchanges cannot provide that
all-or-none result. The consumer selects stores, distributes the lot, sets prices
and authorizes the common player; the framework supplies no market command engine.

```csharp
// Already authorized and holding the actual engine monitor; both observations are current.
var changes = new[] {
    new NativeStorageChange(armor, firstArmory, 50),
    new NativeStorageChange(armor, secondArmory, 50)
};
api.NativeStorage.TryExchangeBatch(operationSequence, changes, goldDelta: -100,
    out var status, out reason);
```

A batch accepts **1–64 physical changes**, with at most **32 nonzero aggregated
good/player totals**, all belonging to the submitting client's owner, one player
and one exact native world. Distinct goods and supported pools can share a batch.
Duplicate exact good/store rows, zero row deltas, foreign/unregistered/dormant
handles, conflicting identities for one slot and mixed players/worlds reject.
An individual good may have a zero net total across stores when another account
changes; an **entirely net-zero batch, including a pure-gold request, rejects**.

Every store must supply its own withdrawals. Capacity is checked against the final
combined native and virtual occupancy, including other owners and goods, without
publishing an intermediate row. Neutral or decreasing virtual occupancy can drain
or rearrange valid claims during foreign native overfill; virtual claims alone
still cannot exceed the fixed pool limit. One failed row, capacity check or payment
refuses the requested whole lot. A zero gold delta performs no wallet lookup or
gold write, so existing debt does not block a free stock operation.

Use the same caller-held engine monitor and authoritative context as single-store
exchanges. Real multiplayer still requires a native-tick or authenticated-Chore
mutation boundary and equivalent commands/catalogs on every peer. Frozen saves,
native add-goods Pre/Original/Post chains, overlapping prepared storage/cargo
commits, blocked restoration and unsupported worlds refuse the operation. The
batch supplies no new command transport or mutation authority.

Sequences share the owner's existing storage stream, including automatic loss
maintenance. Read `TryGetLastSequence` at execution and use the next positive
sequence while retaining the protected boundary; the read reserves nothing.
Refuse exhaustion at `Int64.MaxValue` rather than wrapping. A retained batch receipt
is checked before reconciliation or live store/wallet lookup on an already-bound
world. Identical retries return
`AlreadyApplied` even after a store vanished, without repeating payment or restoring
later lost stock. Keep the original changes, snapshots, sequence and aggregate
gold; input order is immaterial, while a changed valid distribution or price
conflicts, including changes to an individually net-zero good. A prior load's
snapshots remain invalid.

The journal retains **at most 1,024 operations globally**, rather than every owner's
latest receipt forever. Evicted sequences return `ExpiredOperation` and never
execute or assert success. An earlier tick or command may already have evicted a
receipt. New-operation loss reconciliation can independently remove lost stock and
consume sequences even if the requested batch later refuses. Do not silently
allocate another sequence for an expired retry.

The [existing commit limits](#public-limits) apply: allocations, checks and warm-up
finish before the optional one raw gold store and prebuilt private publication.
No consumer callbacks or native helpers run within that commit. Native weapon,
recruitment and horse purchases still require their own qualified native-success
and payment boundary. See the [public batch declarations](../src/Economy/Storage/NativeStorageBatch.Contracts.cs)
and [compiled opt-in helper](../examples/ThirdPartyMod/NativeStorageExample.cs).

### Read one good in one store

After `TryObserve`, call the exact registered handle's
`NativeStoredGood.TryGetQuantity(store, out quantity, out reason)` to read that
good's committed physical allocation. `store.VirtualQuantity` is the combined
occupancy of all virtual goods and owners, so it cannot answer this per-good query.
True with zero is a valid empty allocation, including a freshly observed captured
or reused store. False returns zero and a reason and means the quantity is unknown
or unavailable, rather than a confirmed empty store.

```csharp
// The caller already holds the real engine monitor; this helper issues no command.
if (api.NativeStorage.TryObserve(buildingId, out var store, out reason)
        && armor.TryGetQuantity(store, out int quantity, out reason))
{
    // quantity is this good's confirmed allocation, including a valid zero.
}
```

The query revalidates current world, live slot, unsigned Global ID, owner and pool.
Wrong-pool, stale, unregistered/dormant, blocked or pending observations fail. All
pre-load snapshots must be reacquired in the restored world. Matching staged
restoration may activate on the first protected read; an already-bound quantity
read does not reconcile losses or consume operation sequences.

The caller-held engine monitor and confirmed known world are required for reads,
including in multiplayer. A protected quantity read needs no native-tick or
authenticated-Chore mutation scope. It grants no player authorization or write
authority; exchanges retain their existing authoritative simulation requirements.
The [compiled helper](../examples/ThirdPartyMod/NativeStorageExample.cs) is opt-in
and must be called from an existing protected boundary.

Consumers must require the provider release/build exporting this query; the batch
API alone does not establish its availability.

### Lifetime, losses and saves

The complete ledger and physical allocations belong to one exact native world.
Staged load data is applied only to its matching mission/world receipt. Queries
may finish that matching activation; `IsCurrentWorldReady` is a nonmutating
observation. A new blank editor map can initialize empty without an archive
callback. A loaded map or save cannot silently take that fallback.

Restoration requires the archive's positive `VersionedSaveLoad.MissionSessionId`
to match the synchronously executing mission attempt. A ready mission, filename
or callback order cannot supply a missing association. Unknown (zero), wrong or
no-longer-executing associations block storage before decoding, including
`NoRecord`; they do not mean that the current world is empty. Readable original
records remain passive byte for byte and existing stock remains untouched.
Storage reads, mutation and capture refuse that world. The same blank-editor
world cannot clear the refusal by taking the no-archive fallback; a genuinely new
explicit blank editor world retains its existing initialization path.

This intentionally tightens the previous behavior, which borrowed the globally
active mission's ID. Integrations that bypass the tracked mission-load owner must
use a supported tracked load rather than infer an association themselves. This
is a managed ownership check, not proof of live Extender ordering, native-world
validity, import, enrollment or private-ledger retirement.

Destroyed, reused, captured or retyped buildings lose their associated virtual
stock. Reconciliation updates physical rows and private player totals together;
unchanged ticks perform no snapshot cloning. Registered goods absent from a loaded
catalog start empty. Saved goods whose owner is currently absent remain dormant
and consume their original physical space while this service is active. This is
not an automatic migration of Medium/Fish's separate consumer save formats or
their old capacity policies.

One existing `VersionedSaveData` record stores separate bounded `ledger` and
`stores` entries with `SaveGamesAndMaps` and `SimulationRelevant`. The store schema
validates keys, exact identities, pool limits, canonical order and counts. Private
ledger quantities must agree with physical allocations and contain no orphan
holds. Malformed or incompatible pairs do not partially replace the state.
Unreadable original archive data is retained and the feature blocks rather than
silently writing an empty successful replacement. Unknown occupancy must not be
treated as free capacity.

Normal managed saves hold the engine monitor across native serialization and the
synchronous Extender mod-capture callback. Manual storage captures both entries in
that boundary and rechecks the world before writing. Registered custom workshop
outputs and external cargo use the existing critical writer to freeze and verify
their dependent state before replacing the destination. Cargo-only worlds do not
need an invented production sidecar. Unreadable passive records may now refuse a
save when unknown cargo cannot be safely checkpointed; the
[compatibility contract](#material-save-policy-compatibility-change)
describes this material behavior change and the retained readable-schema-1 policy.
Direct DLL callers bypassing the common writer and process/native failures remain
outside these guarantees. Save-byte agreement is not later command transport.

## External production cargo

The same `NativeStorageClient` can own an external producer's undelivered quantity.
`TryCreateCargo` creates a lot, `TryGetCargo` reacquires it, `TryDepositCargo` moves
quantity into physical storage, and `TryDiscardCargo` records explicit loss.
The lot fixes registered good, producing player, issued quantity and owner-local
creation sequence. Creation changes no stored stock or gold; remaining quantity
only decreases. See the [public declarations](../src/Economy/Storage/NativeStorageCargo.Contracts.cs)
and [compiled example](../examples/ThirdPartyMod/ExternalCargoExample.cs).

Every cargo method, including reads, requires the caller-held real engine monitor,
a confirmed **single-player/editor world**, and the qualified critical save owner.
Real multiplayer refuses. Calls also refuse within native add-goods Pre/Original/
Post chains, frozen critical saves, blocked restoration or a replaced world.
Register the exact owner-matching good at initialized Unity startup before the
first world. GUIDs provide cooperative namespace isolation, not sender
authentication; callers must use their own GUID. Declare a minimum APIShared
release containing these new contracts. Storage availability alone does not imply
that an older assembly exports cargo methods.

### Creation, custody and arrival

The consumer verifies real production and authorizes player 1–8 before creation.
A framework receipt does not prove that a native boat, worker or building produced
anything. `TryGetLastCargoSequence` reads this owner's separate cargo creation
high-watermark and reserves no number. Allocate a new positive `Int64` sequence
and execute in one protected order. Acceptance closes lower gaps; failure does not
advance it. Storage commands, automatic native deliveries and storage-loss
reconciliation use the existing storage stream, so they cannot consume cargo
creation keys.

An identical retained creation retry uses the original sequence, good, player and
issued quantity and returns `AlreadyApplied` with **current** remaining quantity.
It never refills a partly delivered lot. Changed identity conflicts; an expired
creation ID cannot issue more cargo. Keep the engine monitor through storing the
returned creation key in the consumer's existing actor/voyage save state.

Framework remaining quantity is authoritative. Save custody and creation keys,
and transfer the same key from boat to carrier. Do not keep another authoritative
integer `Cargo` or another inventory for the lot. UI quantities are observations
of `NativeCargoSnapshot.Remaining`.

The consumer proves carrier custody, actual arrival and player authorization.
After that proof, `TryObserve` supplies an exact destination. Deposit requires the
lot's pool and producing player and takes the requested positive whole quantity
or refuses it. It atomically decreases cargo and increases physical stock, using
the same native-plus-virtual pool limits. It does not read/write gold, debit an
external native cargo field, move actors or make an unrelated consumer save update
atomic. Keep related custody updates inside the consumer's existing protected save
boundary. Granary storage does not make a good edible or add ration consumption.

### Checkpoints and bounded retries

`Remaining` is a compare-and-decrement checkpoint. Copies cannot duplicate its
quantity. Keep the **original cargo snapshot, original destination snapshot and
quantity** for an in-world deposit retry. Use the returned snapshot after success.
The latest successful deposit/discard receipt is retained per lot:

| Request | Outcome |
| --- | --- |
| Current checkpoint, valid new operation | `Applied`; returns new remaining quantity |
| Exact latest retry | `AlreadyApplied`; no second cargo or stock effect |
| Valid changed intent at that latest checkpoint | `Conflict` |
| Older, unretained checkpoint | `ExpiredOperation`; no effect or success claim |

A retained delivery retry can succeed after its destination vanished; new deposits
still require live binding and room. Re-observing the building does not reconstruct
an old intent. Once a later operation succeeds, an earlier checkpoint is outside
the latest-only promise. Never convert an expired retry into a new creation.

`TryDiscardCargo` loses all undelivered quantity at its checkpoint without refunding
deposited stock. Retain that checkpoint for retries. GC, `Dispose`, timeouts, actor
death and voyage cancellation do not implicitly discard anything; the consumer
chooses that policy. Destruction/capture of a destination reconciles its stored
stock as a loss and does not refill previously delivered cargo.

There are at most 1,024 active plus terminal lots across the world. Active quantity
is never evicted. Creation at a full table evicts only a terminal record, choosing
lowest creation sequence then ordinal owner; all-active tables return `LimitReached`.
Owner high-watermarks persist after eviction/load. Each retained lot keeps its
latest receipt, not unlimited arrival history. Issued quantities are positive
`Int32`, remaining stays within issued quantity, and sequence exhaustion fails
closed. Existing good/physical-row/save-entry limits remain in effect.

### Cargo lifetime and saves

Every opaque cargo and destination snapshot from **before a load is invalid
afterward**, even if the same creation key exists. Reacquire with
`TryGetCargo(savedSequence)` and observe a new destination in the restored world.
A missing/expired key does not authorize recreating a cached quantity or assuming
delivery succeeded. The consumer still reconciles its actor's saved custody.

The `ledger`/`stores` payload remains stores schema 1 when no cargo history exists.
Successful creation switches stores to schema 2 permanently for that world's history,
even after lots drain or are discarded. Ordinary unenrolled checkpoints keep their
exact version-1/reader-1 or version-2/reader-2 envelopes and verification rules.

Ordinary public registration remains current version 2 and does not join a writer
cohort. An explicit owner-bound `client.CriticalSaves.TryEnroll` with
`RequiresNativeStorage = true` before the first storage registration/world instead
requests the process-lifetime protected cohort. A false enrollment leaves the
ordinary default unchanged; accepted enrollment is permanent. Missing or failed
shared-source startup blocks the required cohort, with no unprotected fallback. Its protected container is version 3/minimum reader 3, with a framework-reserved
`critical-cohort` entry; the stores bytes remain schema 1 or 2. Older reader 2
reports `TooNew`, keeps the original bytes and cannot certify or save that cohort.
Legacy 1/2 records remain readable; cargo history still requires a container and
minimum reader of at least 2. A missing participant or mismatched protected marker
keeps the original record passively and blocks replacement. First state publication
waits for the full matching load pass; early callback reentry cannot activate a
partially accepted cohort. Protected state activation and `IsCurrentWorldReady` also
require a known single-player/editor world; a real-multiplayer load remains passive
before stock can be published or exchanged. Ordinary reader-2 multiplayer behavior
is unchanged. No additional capacity hook or fallback is installed. See the
[owner-bound enrollment and compatibility contract](SAVE_DATA.md#opt-in-protected-save-participants).

The critical writer freezes physical and cargo state plus version, refuses
reentrant mutations, verifies capture/status/native I/O/archive bytes, then replaces
the destination. Cargo-only worlds need no invented worker/workshop record.
Matching staged cargo receives this protection even before a first query/tick
activates it. Saved absent-owner goods/lots remain retained and dormant.
Path restoration and all selected checkpoint releases are attempted independently.
A protection-cleanup fault after atomic commit preserves the actual committed
writer result; see [critical-save cleanup outcomes](SAVE_DATA.md#critical-save-cleanup-outcomes).
Internal record provenance/capture support does not itself migrate a consumer ledger.

### Material save-policy compatibility change

Unknown/`TooNew`/unreadable passive storage records, or missing entries in an
existing record, may now require
a checked checkpoint and refuse saving when matching state cannot be validated.
Earlier manual-storage behavior could follow the raw native save path. Consumers
may therefore observe save failure where the prior implementation reported raw
success; unknown cargo must not be overwritten as an empty successful owner.

A readable stores magic and schema-1 header remains positive no-cargo evidence;
it does not make malformed storage usable. An enrolled cohort still requires a
valid complete checkpoint. Genuine `NoRecord`/out-of-scope results retain the
existing empty-state initialization when associated with the exact load attempt.
With explicit protected opt-in, state-only/no-cargo worlds also require the
qualified writer and owned pre-capture context and write a version-3 container.
That process cannot save real multiplayer through the SP/editor-only protected
freeze, even without cargo. Multiplayer snapshot/authority qualification remains
unavailable; its native mode guard is retained. Ordinary unenrolled physical stock
keeps its original real-MP scope and legacy save path. A default reader-2 runtime
loading protected3 keeps it `TooNew`/passive and blocks replacement, never downgrades.
Missing or malformed enrolled state cannot take a raw-success path.
An archive callback can outlive a failed startup because registrations are
process-lived. It never activates storage state. Restored, unknown, malformed or
unassociated data retains conservative capacity and save demand; saving refuses
before Original while that owner is unavailable. Only an exact causally associated
`NoRecord`/`OutOfScope` clears this demand and permits the original empty-world path.
An old stores schema-1 header is not proof of absent virtual occupancy.

Without an enrolled cohort, ordinary schema-1/2 checkpoint bytes and registry unknown
retention stay exact. Protected opt-in has a material SP/editor scope and reader
policy despite additive public signatures; assess it separately from a passing
API compatibility baseline. Enrollment prepares each owner's private checkpoint
and does not migrate its stock, cargo or actor records. An actual consumer's
lossless adapter and protected cold-load confirmation remain required before
retiring that private ledger.

## Importing an existing owner ledger

Typed import is an explicit SP/editor migration operation for an owner-bound
critical participant jointly enrolled with shared NativeStorage. It does not
change ordinary reader-2 registration or multiplayer behavior. Register the real
goods and the existing versioned record first; use its LegacyReader and migrations
to decode old data. From that participant's exact Prepared callback call
NativeStorageClient.TryPrepareImport with physical allocations and final cargo.
No old native world is read. Unknown/wrong causal association, NoRecord, passive
or already protected originals, foreign handles and copied/stale receivers refuse.

Preparation copies bounded values: up to the existing physical-row and cargo-record
limits, with at most 32 aggregated good/player physical accounts. It publishes no
stock, cargo or archive bytes, but successful preparation does publish a
conservative pending-import barrier. Discarding a successful plan does not reopen
empty capacity: the world stays passive until every current pending plan commits
or the actual registry advances to a replacement load/unload. Failed preparation
does not change that metadata. Preparation of the same exact input returns the
same plan; a changed intent refuses.

At a confirmed matching native world after the complete load pass, hold the actual
engine monitor and call TryCommitImport. The service checks the exact original
window as well as the positive mission association, world, all original cohort
members, live building slot/GlobalId/owner/pool, joint native/virtual capacity and
current state identities. A mission attempt may contain multiple archive windows;
the attempt ID alone never authorizes import. Existing allocations needing loss
reconciliation must be reconciled independently before retry: a refused import
does not publish activation or maintenance losses.

The entire physical/cargo candidate and receipt are prepared before one publication.
Refusal leaves shared stock, native gold, original archive and caller state intact.
Source generation is pinned by an internal registry guard across only preallocated
assignments (engine, then import metadata, then registry lock). Generation observers
run after the registry lock is released. Load/unload clears old plans, receipts and
world references; ignored nested old-world unload does not erase replacement plans.

NativeStorageImportPlan.Cargo exposes creation sequences before commit, so the
consumer can preallocate its replacement actor state. Those values are not live
cargo snapshots and cannot mutate a future lot before publication. On success,
perform only nonthrowing preallocated assignments under the same engine monitor to
switch every consumer caller to the shared stock and its owner-private actor state.
No retirement callback runs in the library. Exact retry in the same original
window/world returns AlreadyApplied without issuing stock or cargo twice.
There is no in-world undo after success; reload the complete prior archive when
recovery is needed. Other owners may still be pending, so wait for
IsCurrentWorldReady before using shared state. Do not fall back to a competing
private capacity or stock authority while that barrier is closed.

All related private actor records and the single shared all-owner storage record
must be in the protected cohort. The original destination remains unchanged until
the existing critical writer verifies and atomically commits all of them. An
in-memory import, capture bytes or a matching digest alone does not establish a
durable migration. A later protected original must restore its shared state rather
than importing retained legacy entries again.

Keep work-in-progress and base productivity inputs in the consumer's actor record;
only observed final undelivered quantity belongs in imported cargo. Validate actor
and building lifetime according to the actual producer: a completed batch may
outlive its worker, and carried goods may outlive their source workshop. Neither
is market or recruitment stock before deposit. All worker, capacity, load,
trade and recruitment callers still need one-authority integration tests before
the old ledger or hooks can retire. An older raw-record consumer cannot interpret
a new versioned envelope; document that material downgrade limit. This API does
not establish native route, live coexistence, multiplayer import, UI, audio or
visual acceptance.

## Public limits

This is a storage/payment backend. It adds no rack art, food consumption, workshop
recipes, hauling policy, native unit recruitment, sound or market statistics.
The consumer must verify a real delivery or payment trigger before exchanging.
A refused exchange does not debit cargo, award achievements or acknowledge an
external producer.

The commit guarantee covers normal operational failures under conforming locks:
all validations, allocations and publication warm-up precede the raw gold store
and prebuilt ledger/physical-state publication. Forced asynchronous thread
termination, process/native faults and unlocked foreign writes are outside it.

This service refuses its own exchanges throughout the native add-goods wrapper's
entire Pre/Original/Post chain, including nested calls. That preserves the native
arrival-admission assumptions. Arbitrary foreign callbacks rewriting quantity,
good, identity or native stock can violate producer accounting independently;
this storage backend does not itself supply a lossless worker queue or rewrite
Vanilla's failed-admission behavior. Registered custom workshop outputs use the
[qualified shared producer boundary](WEAPON_PRODUCTION.md#produce-a-registered-custom-good),
which commits cargo and physical stock together. Other producer integrations still
need their own verified completion/arrival policy; the
[external cargo adapter](#external-production-cargo) supplies bounded quantity and storage
commits for that integration without inventing the evidence.

Existing private Medium capacity detours overlap these helpers. Live-byte and
ownership checks fail closed on that conflict. Migrate the consumer onto the
shared service before installing both; do not layer another native detour or
subtract the same virtual stock a second time. Fish's historical independent
100-per-Granary capacity is a separate policy, not native Granary parity.

The catalog is bounded to 128 goods and allocations to 16,384 rows; each save
entry is at most 1 MiB. The private journal retains bounded recent receipts plus
persisted high-watermarks. Lost-stock reconciliation fails closed if an owner's
Int64 sequence is exhausted. No native enum/table expansion is supplied.

## Implementation and verification

The [pure state/codec](../src/Economy/Storage/NativeStorageState.cs) uses the
Content library's trusted internal prepared commit. Only prebuilt publication
follows the native gold store. Native hot paths read an immutable marked-building
index; empty demand follows the complete original path.

Batch operations reuse that prepared exchange and publication. A kind-5/version-1
intent stores a 34-byte versioned SHA-256 fingerprint of the canonical complete
physical changes and gold intent. It fits the existing opaque Content schema-2
receipt field; readers preserve it without interpreting its new kind. Store
schemas, outer reader requirements and existing single-store kind-2 intent bytes
remain unchanged. Older assemblies do not export the new batch method, so
consumers must declare a release containing that API. The new method adds no save
policy or native hook.

The [native owner](../src/Economy/Storage/NativeStorageNativeRuntime.cs) coordinates
Armory admission/free count, Granary free count, the existing Granary finder
comparison and the Script Extender's managed add-goods Original call. The Extender
retains its native entry and Pre/Post order. No second B6100 native detour is
installed. The Granary finder still performs its original aggregate recomputation
and reachability search. See [the native update contract](../UpdateToNewDLL.md#native-pooled-storage).

Core fixtures execute the production state and codec. After warm-up, 1,000
unchanged reconciliations with two marked-index reads each allocate zero bytes;
prepared publication also allocates zero bytes. A separate timing loop on .NET
10.0.8 took 67.216 ms for 10,000 such reconciliation/read iterations with one
claimed store. This synthetic result excludes Unity/Mono, native callbacks and
consumer work; it establishes no game frame-rate improvement.

Installed-reference tests call the production SDK-layout and pristine native-byte
validators and exercise the actual SE wrapper transformation. Two Windows x64
fixtures execute the production generated stub against synthetic native callees
in bounded owned memory, checking original-call count, callback arguments,
stack/home space, volatile GPR/SIMD restoration and final comparison flags.
Simulated publication failures and these isolated checks do not establish live
native installation, renderer behavior, actual hauling, or multiplayer gameplay
acceptance. Those checks remain distinct from build and CI results.

External cargo fixtures additionally execute creation/checkpoint/eviction and
schema-2 restoration, owner/world/context failure, frozen versions, passive-load
classification and cargo-only appended archive/atomic-replacement paths. They do
not demonstrate a live external producer, carrier arrival, consumer actor-save
reconciliation or multiplayer repair. External cargo adds no native hook or
producer-supplied transaction callback.
