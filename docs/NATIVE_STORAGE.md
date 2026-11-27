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

### Lifetime, losses and saves

The complete ledger and physical allocations belong to one exact native world.
Staged load data is applied only to its matching mission/world receipt. Queries
may finish that matching activation; `IsCurrentWorldReady` is a nonmutating
observation. A new blank editor map can initialize empty without an archive
callback. A loaded map or save cannot silently take that fallback.

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
synchronous Extender mod-capture callback. This service captures both entries in
that boundary and rechecks the world before writing. Direct DLL save callers
bypassing that wrapper, filesystem failures and joint file-commit acknowledgments
are outside this guarantee. Save-byte agreement is not later command transport.

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
need their own verified completion/arrival policy.

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
