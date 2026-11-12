# Custom goods inventory and transactions

## Usage

`APIShared.Goods.CustomGoodsInventory` in `APIShared.Content.dll` supplies a
game-independent virtual inventory. It addresses the stock, capacity, retry and
reservation bookkeeping required by separate goods such as armor or Fish. It
does not assign native `eGoods` values. Reference Content with `Private=false`
from a runtime consumer; offline tools can reference Content independently.

Create one inventory for an authoritative mission and share that same instance
with participants. `ForOwner(pluginGuid)` binds a cooperative namespace. Register
each stable local good ID and its per-player capacity before restoring saved
state. The same local ID under different GUIDs denotes different goods; exact
case matters. Owner IDs use 1–128 printable ASCII characters without spaces;
local good IDs use 1–64. Localized labels stay outside state. New transactions use
strictly increasing positive Int64 sequences per owner, chosen by the consumer's
authoritative simulation; retries reuse the original sequence.

```csharp
var inventory = new APIShared.Goods.CustomGoodsInventory();
var goods = inventory.ForOwner("Example.Author.Supplies");
if (!goods.TryRegister("medium-armor", 200, out var armor, out string reason))
    throw new InvalidOperationException(reason);

// Called only after the consumer verifies this particular delivery.
var deposited = goods.Apply(operationSequence: 1, changes: new[] {
    new APIShared.Goods.GoodsChange(armor, playerId: 1, delta: 2)
});
var reserved = goods.Reserve(2, new[] {
    new APIShared.Goods.GoodsChange(armor, playerId: 1, delta: -1)
}, out var hold);
// After independently confirmed native success: hold.Commit().
// If the external operation was cancelled: hold.Release().
```

See the [compiled consumer example](../examples/ThirdPartyMod/CustomGoodsExample.cs).
No example is automatically activated by adding this library.

### Atomic changes and retry sequences

`Apply` accepts 1–32 lines, covering multiple goods and players of the bound
owner. It sums duplicate accounts before checking bounds, using Int64 arithmetic
so line order cannot cause intermediate Int32 overflow. Opposing lines cancel:
`-5,+5` is a net-zero change even from zero stock. All resulting quantities must
remain between reserved stock and the registered capacity. A failed request
changes no balances, holds, receipts or accepted sequence watermark.

An identical normalized retry with a retained receipt returns `AlreadyApplied`,
without another stock change. Reusing its sequence with another normalized payload
or operation kind returns `Conflict`. New operations must exceed the owner's
`LastAcceptedSequence`. Accepting a sequence permanently closes all lower gaps:
an expired receipt or an unaccepted lower sequence returns `ExpiredOperation`,
never applies again, and does not assert previous success. Rejected requests do
not advance the watermark. A net-zero accepted operation does advance it.

Sequences belong to one owner/inventory lifetime. Consumers serialize allocation
and submission in their authoritative simulation. Reading `LastAcceptedSequence`
does not reserve a number, and independent producers must share the same sequencer.
Persist sequence-to-external-work associations needed for retries. Do not infer
ordering from thread scheduling, local time, random values or native slot numbers.

The catalog holds at most 128 goods, each with eight player balances (IDs 1–8).
The journal holds at most 1024 retained operations; both bounds can be lowered at
construction. When full, the next accepted operation discards one terminal receipt
in deterministic owner/sequence order. The persisted watermark still rejects old
requests, so production can continue indefinitely without an unbounded history.
Active holds never expire. If every journal entry is an active hold, new operations
return `LimitReached` without advancing the watermark. Resolve holds rather than
resetting a live inventory to free space. At Int64.MaxValue the owner stream is
exhausted; sequence wrap/reuse is not permitted.

### Reservations

`Reserve` accepts only negative lines. It holds all requested stock or none.
`GoodsBalance.Quantity` includes holds; `Available` subtracts them. Holds continue
to occupy capacity, and ordinary withdrawals cannot spend them.

A handle belongs to its exact inventory instance and successful restore/reset
generation. `Commit` consumes its held stock once; `Release` frees the hold without
changing Quantity. Repeated same-action calls return `AlreadyCommitted` or
`AlreadyReleased`. Commit after release or release after commit returns `Conflict`;
neither creates a debit or refund. Repeating Reserve with its original sequence
and payload returns a handle and the retained state, including terminal states.
An expired terminal reservation returns `ExpiredOperation` without a handle.
An already-held handle also returns `ExpiredOperation` after its terminal receipt
expires; no outcome is inferred and nothing is debited or refunded.

Input collections are copied before acquiring the internal monitor. Their getters
must not change concurrently and may throw; those exceptions propagate. Internal
validation and publication invoke no consumer callbacks. All methods serialize
on the inventory, run on the caller's thread, and perform no dispatch. Namespace
binding is cooperative isolation, not access control against malicious plugins.

### Persistence and mission lifetime

`Capture` returns private canonical schema-1 bytes containing stable good IDs,
capacities, quantities, owner watermarks, active reservations and recent terminal receipts. Ordering
is ordinal by owner/good/player and owner/operation, independent of registration
or dictionary enumeration. The fixed bounds keep a snapshot within 1 MiB.

Wrap it in an existing [VersionedSaveData](SAVE_DATA.md) entry; do not add a second
archive system. Gameplay records should be `SimulationRelevant`. Register all
saved goods at their matching capacities before `TryRestore`. Extra registered
goods start empty. Unknown goods, changed capacities, invalid quantities,
overbooked holds, duplicate/noncanonical records, trailing bytes and exceeded
local bounds reject the entire load, preserving existing state and handle validity.
Keep incompatible loaded bytes and block gameplay or use an explicit migration;
do not replace them with an empty successful save.

A successful restore invalidates old handles. Recover a saved active reservation
by calling Reserve with its original sequence and payload; `AlreadyReserved` establishes
that the hold already exists. Reconcile any corresponding external native work
before committing it. Restoring the same inventory snapshot cannot establish
whether that native work completed. `Reset` clears stock and receipts and
invalidates handles while retaining registrations. The caller replaces/resets the
inventory at mission boundaries; the Content library installs no unload hook.

## Public limits

Atomicity covers this inventory only. The library never reads or writes native
gold, equipment, buildings, cargo, workshop output, food, prices or recruit slots.
A failed native wallet write cannot be rolled back by a virtual-stock transaction.
Do not advertise `Apply` plus a native payment as an atomic market purchase.

The capacities are per-good bookkeeping bounds, not physical Armory/Granary
occupancy. Building deletion, ownership changes, native stock sharing, cargo,
partial delivery and exact storage identities need qualified adapters. Native
goods/recipe, market presentation and troop recruitment integration remain separate.

Thread safety does not authorize gameplay mutation. Consumers own the engine
monitor, simulation-thread ordering, authenticated command policy and equivalent
catalogs/operations on every peer. No commands or balances are transported.
`SimulationRelevant` checks saved bytes before a multiplayer load; it does not
synchronize later changes. This PR establishes no live multiplayer integration.

## Implementation and verification

The implementation is in [Content/Goods](../Content/Goods/CustomGoodsInventory.cs).
Every accepted mutation prepares a private replacement of stock and journal state
and then publishes it once. Commit/release replace immutable receipts. Restores
parse into private state and recompute held totals from active receipts before
publication. No polling, game lookup, native hook or process-global state is added.

[Content tests](../tests/Content.Tests/CustomGoodsTests.cs) exercise the actual
dependency-free implementation, including rejection, replay, reservations,
corruption, generation changes and concurrency. These fixtures establish ledger
behavior; they do not verify native transactions, visuals or game integration.
