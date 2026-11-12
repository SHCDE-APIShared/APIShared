# Native gold and virtual goods exchanges

`ModApiClient.GoodsWallet` exchanges the existing game's signed gold field with
owner-qualified virtual stock from `CustomGoodsInventory`. The service creates
no native good ID, storage building, price, market button or recruitment rule.
Use the same owner GUID for the runtime client and the supplied goods client.

```csharp
var result = client.GoodsWallet.Exchange(goods, sequence,
    new[] { new GoodsChange(armor, playerId, +1) }, playerId, goldDelta: -25);
if (result.Status != GoodsWalletStatus.Applied &&
    result.Status != GoodsWalletStatus.AlreadyApplied) Log(result.Reason);
```

Call in a confirmed known world while already holding the actual engine monitor.
Offline/editor operations may run at the consumer's engine-protected boundary.
Real multiplayer additionally requires a shared native tick or authenticated
Chore callback and identical deterministic requests on every peer. The caller
still authorizes control of the player and chooses synchronized prices. The
service sends no commands and does not turn local UI intent into authority.

## Request and replay

A request contains a positive owner-local sequence, 1–32 virtual changes for the
same player, and a signed Int32 gold delta. A positive delta credits gold; negative
debits. The final balance must fit 0–Int32.MaxValue. `TryReadGold` reads the raw
signed value separately from pointer validity: zero and negative gold are valid
read results; they are not unavailable-wallet sentinels.

`Applied` means both sides were published once. `AlreadyApplied` is an exact
retained replay and performs no wallet lookup, affordability test or gold write.
The persisted fingerprint includes normalized virtual changes, native adapter kind,
player and exact gold delta. Changing the price or operation kind for a retained
sequence returns `Conflict` through `LedgerStatus`. Expired receipts remain closed
by the ledger's high-watermark; an expired retry never charges again or asserts
previous success. Persist the sequence's external association as described in the
[ledger guide](CUSTOM_GOODS.md).

Rejected ledger bounds, wallet lookup, insufficient gold, overflow or a changed
world publish no stock, gold, receipt or accepted-sequence change. Input collection
getters run before the inventory monitor; their exceptions propagate, and callers
must not change the collection concurrently. The framework checks current gold
after preparing the ledger, immediately before committing; a prior displayed
quote is not an affordability reservation.

## Atomic boundary

The caller holds the engine monitor, then the adapter acquires the inventory
monitor. Preparation normalizes inputs, checks replay, clones stock/journal state,
reserves bounded journal space and allocates the result before any native write.
The actual critical method, Content publication and disposal are first executed
on an isolated scratch inventory and stack-local gold cell, so first-call JIT
preparation occurs before real money or stock can change. The only real commit
operations are one validated native Int32 gold store followed by
publication of the already prepared managed state. No consumer callback, native
resource helper, allocator or virtual dispatch runs between them. Disposal of an
unpublished prepared lease changes nothing. Reentrant ledger writes are refused
while a native lease exists, and leases are internal, thread-affine and single-use.

The guarantee covers normal operational failures under those cooperative locks.
Asynchronous forced thread termination, unprotected foreign memory access and
process/native-memory faults are outside
it. No compensating refund or retry is guessed after an unknown native outcome.

The wallet does not call `AddPlayerGold`: the Extender helper uses unchecked
addition plus zero clamping. The adapter validates the supported native image,
the 22,588-byte packed resource view, signed gold at offset 0x50C and the checked
one-based resource pointer. No wallet hook or competing native interception is
installed. Unsupported layouts fail this service independently.

## Persistence and world lifetime

A ledger generation binds to one exact native-world token on its first exchange
attempt. Reset or successfully restore it before use in another world. Resetting
loses replay history and therefore belongs only at a real mission boundary.
Restore the ledger with the native save that carries its corresponding gold, then
retry an original accepted exchange only with its original sequence and payload.
The adapter installs no second archive and automatically debits nothing on restore.
Capture ledger state under the engine monitor in the existing VersionedSaveData
protocol; mark gameplay records SimulationRelevant and preserve incompatible bytes.

Old virtual-only operations continue to emit the exact schema-1 snapshot format.
A published native-intent operation switches that ledger generation to schema 2,
which persists up to 256 canonical opaque intent bytes per receipt. New readers
accept schemas 1 and 2; older schema-1 readers reject schema 2. Keep those loaded
bytes and gate gameplay or supply an explicit consumer migration. A failed native
exchange does not switch formats. Reset starts a fresh schema-1 generation.

The trusted internal prepared-commit seam is also available to the sibling storage
adapter, whose different intent kind records its physical-storage request. It is
not a public arbitrary-callback transaction API. Independent mods continue to use
the public owner-bound methods and shared DLLs.

## Verification

Content tests run the actual preparation and codec paths: schema-1 retention,
schema-2 restore/replay, changed intents, rejected bounds, disposal, reentrant writes
and foreign-thread disposal. Installed tests use the real packed SDK resource
struct on synthetic unmanaged records, including zero/debt reads, failure before
commit, changing funds, overflow, world replacement and restored/reset generations.
Cecil checks the compiled critical section for one direct gold store and the known
preallocated publication, whose body contains no calls or allocations. These
fixtures do not verify UI feel, physical storage execution or live multiplayer.
