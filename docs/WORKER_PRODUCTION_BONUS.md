# Worker production bonus

`APIShared.Economy.WorkerProductionBonusOverrides` coordinates the fixed production
bonus used by Vanilla's worker-yield helper. It does not multiply completed stockpile
transactions, remove existing goods, change fear levels, or refund ingredients.

## Registration and units

After APIShared native initialization, call `TryRegister(ownerGuid, registrationId,
out registration, out diagnostic, order)` and inspect its result. Registrations start
inactive and remain rooted until process exit. `registration.SetPercent(value)`
accepts whole percentage points from -100 through 1000; `null` disables just that
owner. Registrations are ordered by ascending order, ordinal GUID and ordinal local
ID. The last non-null value replaces the fixed contribution; an empty selection uses
Vanilla's 50. Values are not added together. Invalid values preserve the old selection.

For a base amount of one and neutral fear, 50 gives outputs 1,2,1,2; 0 gives one
each time; -50 gives 1,0,1,0. Fear productivity above 100 percent remains additive,
so +50 from fear and -50 here give the ordinary base amount. Vanilla's mode and
per-call bonus flag remain authoritative. Wheat, flour and bread do not request
this fixed contribution. Other mods' changes to the helper's base amount still run
through the existing Extender Pre/native/Post chain.

The admitted numerical range is audited for Vanilla productivity through 150 and
base quantities through 100, including current configurable hunter/tanner output.
Foreign producers outside these bounds require their own arithmetic audit.

## Mission and multiplayer responsibility

Publish settings only at synchronized mission boundaries, identically on all peers.
Thread-safe registration is not a gameplay synchronization mechanism. `IsAvailable`
means the entire native calculation, zero-output completion, stone transport and
display protection was published. `Diagnostic` describes failure. `CompatibilityKey`
is an ordinal service-contract/native-hash identity, empty while unavailable; it
does not transport reports or prove agreement.

Consumers should exchange this key using their existing authenticated readiness
channel and prevent startup when required reports are missing or native protection
differs. Historical negative batches make this relevant even with a current value
of 50 or a disabled consumer. Distinguish native protection from the consumer's
ability to publish an override: a failed registration does not disable already
installed historical guards. Non-Vanilla rules additionally require every peer's
publication capability.
ExtraFeatures uses APIShared's existing per-player preset synchronization and
`LobbyLaunchGuards` before Ready/Start and during prepared multiplayer-save admission. Native initialization is isolated from other
APIShared features; unavailable worker protection must never enable a partial policy.

Its ephemeral report distinguishes the actual native service key from the ability
of its adapter to publish a contribution. At 50 or with ExtraFeatures disabled,
all participants must still report matching native protection; matching unavailable
services retain Vanilla. An enabled non-50 setting additionally requires every
adapter to be ready to publish. Missing reports never count as unavailable reports.

ExtraFeatures freezes the percentage at the first initialization checkpoint, before
native simulation can resume a save. It reevaluates admission at each checkpoint
and at mission start: unknown modes stay inactive, and real multiplayer requires
current matching peer reports. A gate-opening notification alone cannot activate
the contribution before that checkpoint's admission check.

`LobbyLaunchGuards.TryRegister(owner, id, callback, out diagnostic, order)` installs
one shared managed lobby interceptor. Callbacks return a localized blocking reason
or null/empty to allow. Order is priority/GUID/ID; the first rejection wins and a
throwing guard fails closed. They run synchronously on the Unity lobby thread, outside
registration locks, and must not start another lobby action. Unready, unlock and
unrelated commands remain available. Registrations are permanent and predicates
control demand; existing managed hook chains run once when admitted. The broker
provides admission and announcement, not a second settings/network protocol.
Use the immutable `context.RequiresPeerAgreement` to distinguish actual network
admission from Vanilla's local start branches. It is false for Play with
Vanilla's skirmish flag or COOP_START with that receiver's single-player Coop flag.
Prepared multiplayer saves use `Command == "HostLoadGame"`, `Operation == Start`
and a null `Lobby` receiver. The existing save-readiness coordinator evaluates the
same guards after settings preparation and fresh fingerprint checks, before host
release, client acknowledgement and final native-start admission. Its freshly
validated roster requires agreement with multiple humans; a solo Steam host has
no peer to compare and receives `RequiresPeerAgreement == false`. Rejections use
the existing save-preparation cancellation/error path, without another hook or
transport. No registered guards preserves ordinary save loading.
Ready/ReadyLock and missing or unknown button-receiver evidence require agreement.
The broker captures these command-specific facts before callbacks. In-game multiplayer
flags are still false in a fresh network lobby and cannot replace this evidence.

## Zero output, transport and persistence

Field workers retain the outgoing route Vanilla already created. At arrival, zero
output skips storage search/acceptance, deposits and delivered-goods statistics and
enters the complete native work-finished path. Missing storage before production
remains a Vanilla prerequisite. Workshops finish manufacturing/animation and then
skip their first storage search when empty, preserving ingredients, work accounting,
rest and the next ingredient/workplace decision. No immediate worker-state mutation
is made from the yield event.

Reduced stone batches may contain one through seven stones. Both Ox departure
checks admit positive reduced loads; zero uses its normal wait/return path. Input
stack sizes and quarry work counters remain unchanged. Zero reserve cannot produce
an artificial driver load. Negative extra-yield fields are not drawn as positive
yield symbols, and are not modified to implement that display rule.

Vanilla keeps signed fractional remainders in a raw WORD per producing unit. Its
unsigned Extender view must be interpreted as signed Int16 when inspected. The
existing save section 3015, Chore-56 transfer and multiplayer checksum retain these
bits; no extra save section or packet is introduced. Existing fractional credits
remain, so a switch to -100 may still release a previously accumulated final item.
Normal unit-slot clearing removes old carry when a slot is reused.

An already produced empty or reduced batch still needs its completion protections
after loading with 50 percent, disabling a consumer, or entering a different mode.
The signed negative extra-output word identifies such native batches: with
Vanilla's productivity floor and nonnegative fixed bonus, valid native production
cannot create that value. Zero-output guards additionally require empty carry;
stone guards use the producing Ox record, including the separate driver path.
No provenance flag is added to saved records. A separate process-only readiness
word enables these historical guards only after every native hook has validated;
partial publication leaves both the default calculation and safeguards dormant.
Without an override, ordinary Vanilla-reachable production remains unchanged.

## Verification limits

The native execution fixtures cover actual yield bytes, generated production hooks,
register/stack preservation, signed carry, and raw save/checksum contracts. They
do not establish actual Unity rendering, live co-installed hooks or host/client
gameplay acceptance. New/load/restart, zero and reduced-load production, current
ImprovedHunters integration, editor and real multiplayer remain separate game checks.

See [the native update contract](../UpdateToNewDLL.md#worker-production-bonus)
for addresses, signatures and validation requirements.
