# Immediate ally transfers and execution events

`APIShared.Economy.AllyTransfers` provides current native storage capacity and one
immediate, bounded gift. It accepts arbitrary consumer GUIDs and has no dependency
on a particular automation mod. Scheduling, rule persistence, fair allocation and
the authority to control the sender belong to the consumer.

## Calling the service

Register synchronized rule changes with `RegisterAuthenticatedChore<T>` and run
periodic decisions with `SimulationTickEvents`. Both publishers admit the shared
simulation-mutation scope. Calls from Unity, a worker thread, a retained old scope
or a transfer callback fail before reading game state. This marker is not a lock
or dispatcher and does not make unsynchronized consumer state deterministic.

`TryGetCapacity(recipientId, good, out capacity, out reason)` returns the current
stored amount, whether a compatible storage exists and its nonnegative free
physical capacity. A full existing storage differs from a missing storage. Gold
requires no building and uses signed-balance headroom. Pending incoming goods are
not included, and a query reserves nothing. Send immutable snapshots to Unity to
display the result.

`TryExecute(new AllyTransferRequest(senderId, recipientId, good, maximumAmount,
minimumSenderRemaining, maximumRecipientAmount), out result, out reason)` checks
living allied Lords, current balances, physical capacity and arithmetic bounds
immediately. It clamps one operation to these bounds and to `short.MaxValue`,
because Vanilla gift notifications carry a signed 16-bit amount. All values use
the game's integer units, including gold. A true return means a normally
completed positive transfer with equal observed debit and credit. A false return
can still contain partial or unknown observations; never automatically retry it.
The operation does not enqueue a Chore, change the viewpoint or synchronize its
caller. Every peer must execute the same operation at the same simulation point.

The supported list matches Vanilla's ally-send UI: ordinary stockpile resources,
food, weapons/armour and gold. Logs, hides and refined pitch are excluded.
`SupportedGoods` and `IsSupportedGood` expose this contract.

## Actual native execution events

`AllyTransferEvents.TryRegister` observes the actual gift primitive, including
Vanilla Chore execution and calls through the safe service. Register after native
initialization with an owner GUID and owner-local stable ID. Order is ascending
order, ordinal owner GUID, then ordinal registration ID. Registrations and the
managed adapter last until process exit. The Script Extender remains the sole
owner of the native transfer hook.

Pre receives the unchanged sender, recipient, raw goods index and requested
amount. It may retain owner-private `State` or veto the entire operation with
`SkipOriginalFunction`; it cannot redirect participants or replace quantities.
A successful veto is sticky. Failing callback mutations/veto/state roll back.
Pre must not mutate economy state. Identical deterministic policies are required
on all peers; callbacks run synchronously on the actual native caller thread.

The boundary is inside the Extender's existing send-goods wrapper, immediately
around its native Original call. Extender Pre handlers therefore run first;
these events describe their final effective arguments. An Extender veto prevents
the primitive and these native-operation events altogether. A service call then
returns a zero-effect veto result. The safe service rejects redirected arguments
and checks its bounds again after Extender Pre. Extender Post still runs according
to the Extender's existing contract; its Boolean return is not a delivery receipt.

Post receives the completion result and private State after normal return, veto
or a caught call-chain exception. `OriginalWasCalled` does not imply a successful
gift. `SenderDebit` and `RecipientCredit` separately describe the net balance
changes during the native call; either can be null when readback was unavailable.
`TransferredAmount` is present only for equal nonnegative deltas after normal
completion, or zero for a veto. Unequal deltas, nested transfers and unavailable
reads remain `Uncertain`; an exception is `Faulted`, without retry or rollback.
These are observed net changes, not attribution of arbitrary foreign side effects.

If the native Original delegate throws, the adapter publishes its faulted receipt
and then preserves that exception through the Extender wrapper. Its legacy Post
is skipped as before. The safe service returns the faulted receipt, including any
measured partial effects; it never retries the operation.

Recursive native gifts run their original exactly once without recursive events
and mark the outer attribution uncertain. Starting a service operation from a
transfer callback is rejected. Observers cannot assume a Vanilla invocation came
from a mouse click: other code can use the same command path.

`Commands.GameActionEvents` remains useful for local command intent before enqueue,
but is not execution confirmation. SE add/subtract events describe individual
resource operations without a paired sender/recipient; gold gifts write balances
directly and do not pass through those events. Market-price events are valuations,
not gifts or market transactions. No second owner is added to these existing hooks.

## Native update evidence and limitations

Evidence was rechecked on 2026-11-04 against the installed canonical DLL SHA-256
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
The feature is hash-bound: resolving a matching function prologue on a new build
does not establish its implicit global/record layout, capacity semantics or
notification contract. Unknown builds refuse this service while independent
Vanilla and APIShared behavior remains available.

| Reference RVA | Contract | Complete byte SHA-256 |
| --- | --- | --- |
| `0xD7AD0` | 585-byte `void(recipient, good, amount, sender)` gift primitive | `206BDE68C11D963B744BF671B185935EC32EF05CB1EBB3A479E308095CC03612` |
| `0xC5AD0` | 319-byte `int(buildingManager, recipient, good)` capacity query | `978F663FC2E2F1FAF8CF89FE73BFD17ABD9FA728F13A57AF1325828DB9E1DDA9` |

The complete pristine bodies pin native semantics. The loaded Extender already
hooks `D7AD0` using its `c_game_player_ai_send_good` signature. APIShared installs
no competing native hook and does not require this live entry to be pristine.
It validates the loaded managed wrapper and replaces only its single native
Original delegate invocation with the shared adapter. The enclosing Extender
Pre, argument changes, veto and Post remain in their original order. Capacity
remains an unpatched, validated read-only query. The primitive's only direct
native code caller is `1A907` in Chore handler `1A6F0`.

The managed ally UI calls GameAction 1063, which checks inventory/capacity and
queues Chore 113 mode 2. The 18-byte payload is a signed-short mode followed by
four signed-int values. Handler `1A6F0` eventually calls `D7AD0`. Enqueue-time
capacity can become stale; the immediate service avoids this command delay by
performing fresh checks in the synchronous simulation context.

Capacity follows `C5AD0 -> BFD30/C6FC0/BD470`: goods type selects storage;
Armouries share 50 slots, Granaries use 250 food slots, stockpiles use native
per-good stack capacities. Allocation follows `B58B0 -> B5E90/C6EA0/B6000/B6100`;
removal follows `B7D80 -> C1CE0` and updates local/global totals and visuals.
No faction branch occurs in these paths. Native gifts store goods immediately;
they do not add to incoming-delivery reserves. Gold is transferred directly and
capped by sender funds. The safe service also checks recipient signed headroom.

The gift then values goods through `CEB90`, updates gift statistics, clears a
matching request and enters `3F870`'s Lord/speech response. Reception presentation
uses `2940 -> 1A4190`, `EDF0 -> 1A4270` and the 10-entry `1C8D0` message ring.
The native 30-second reception and 20-second AI speech throttles affect local
presentation, not quantities. Statistic arithmetic retains Vanilla's unchecked
Int32 behavior. Existing SE resource-policy callbacks can alter or veto add/sub
effects; the result deliberately exposes mismatched deltas rather than promising
atomicity in the presence of such policies.

Correction on 2026-11-05: the earlier inspection missed the Extender's own
`D7AD0` signature hook. A pristine live-entry requirement consequently disabled
the service at startup. The adapter now reuses that existing owner. SE also
retains downstream add/sub resource hooks. Tests cover arithmetic, event
completion and the installed managed wrapper integration; they are not game,
editor, save/load or real two-peer acceptance evidence.
