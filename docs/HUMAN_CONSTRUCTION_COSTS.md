# Human construction extra costs

`APIShared.Buildings.HumanConstructionCosts` is the opt-in counterpart to the
typed [construction-cost table API](THIRD_PARTY_GUIDE.md#typed-construction-costs).
The table API changes native base costs for both human and AI players. This
service adds paid resources to human construction only. It does not change AI
budgets or infer that every object spawn is a paid building command.

## Registration and configuration

On the initialized Unity thread after native APIShared initialization, call
`HumanConstructionCosts.TryRegister(ownerGuid, registrationId, saveDataIdentifier,
out registration, out reason)`. Use stable plugin-owned identities, register on
every peer in the same order and retain the returned handle for the process.
Registration fails if the audited runtime adapters are unavailable; a refused
registration must not be presented as an enforced feature.

`SetCosts(ConstructionCostTarget, IReadOnlyDictionary<eGoods, int>)` takes an
immutable copy. Resolve targets through `ConstructionCostTarget.FromMapper` or
`FromStructure`; aliases sharing a construction entry share configuration. Only
targets reporting `SupportsHumanExtraCosts` are accepted. Configuration replaces
that target's previous cost map. An empty map removes the target's future charge.

The supported goods are the stored goods offered by UnitCosts, without its horse
linking feature. Gold accepts -10000 through 10000. Other supported goods accept
0 through 100. Unsupported goods, invalid targets and amounts outside these
ranges are rejected rather than silently clamped. Positive values charge extra
goods; a negative gold value is a credit granted after successful construction.

`SetActive` controls future charges. `ClearCosts` removes future configuration.
Neither operation erases payment receipts for objects that were already paid.
The consumer owns mode permissions, host-authorized settings synchronization and
when configuration can change; registration grants no game-mode permission.

For UI, use `HumanConstructionCosts.TryGetEffectiveCosts(target, playerId, out
costs)` rather than interpreting one registration's raw configuration as a live
charge. This returns the immutable sum over active owners only when the runtime
confirms an eligible human and currently chargeable construction. Free-first
woodcutters, granaries and marketplaces, editor placement and AI do not display
Human extra costs. The quote does not authorize a build or prove affordability.

## Payment and refund behavior

The runtime adapters use final native arguments after existing Pre callbacks.
They check base costs plus positive extra costs, reserve extras before entering
the original construction operation, and restore the reservation if that
operation fails. A gold credit cannot finance a base cost or another mod's
positive reservation before construction succeeds. Success must be corroborated
against the newly created native identity; a managed Post notification alone is
not a payment commit.

Each owner keeps its actual payments separately. A receipt records the object
domain, slot, native generation, paying player and signed goods. Building and unit
slots are one-based game IDs; decoration slots are direct projectile-array slots.
Native slot reuse cannot claim an older object's receipt.

Only an authorized self-demolition by the same current human owner can consume
a receipt. Combat death and unrelated deletion retire claims without a refund.
An ownership mismatch retires the old payer's claim. Refund previews are read-only;
consumption removes every owner contribution before goods are booked, preventing
re-entrant notifications from paying twice.

Refund calculation uses the effective current demolition percentage and per-good
factor supplied by the runtime, including registered
`ConstructionRefundPolicies`. Stored payment amounts do not change when later
cost settings change. Fractional quantities truncate toward zero. Regular material quotes retain SE
multiplication order and direct unsigned conversion before integer bounds,
matching SE's CIL without additional float rounding. Desktop .NET and Unity Mono
can differ at fractional boundaries; in-game SE parity defines the result.
Stationary-unit quotes retain native integer percentage arithmetic. Amounts are quoted individually rather than
through a combined double factor. Positive paid amounts are returned; negative gold
payments produce a negative offset against the base refund, never a second gold
credit. Resource capacity and delivery remain governed by native resource APIs.

## Save and lifecycle contract

Each registration owns a version-1 `VersionedSaveData` record with the known
`payments/` prefix and `SimulationRelevant=true`. Its bounded binary chunks use
stable object and good ordering, so equal peer histories produce equal bytes.
The format stores no JSON, reflection-generated data or local presentation state.

Archive loading restores candidates without accessing the previous native world.
After completed managed mission start, the runtime validates loaded identities
and current owners. A new mission without a loaded record starts with no receipts.
Old saves, editor maps and malformed histories never invent past payments.
An unreadable newer record that VersionedSaveData must preserve also blocks new
paid construction for that registration: otherwise new receipts could not be
persisted. Starting a fresh mission restores normal recording.
Mission epochs invalidate unfinished transactions, preventing a delayed commit or
abort from mutating another mission's resources.

## Verification limits

`tests/HumanConstruction.Tests` compiles the production transaction, ledger and
binary codec with doubles only at the external lifecycle/save/SDK boundaries.
It covers combined affordability, reservation rollback, signed credits, settings
snapshots, multiple independent owners, repeat callbacks, identity reuse, save
ordering, malformed data, legacy saves and mission changes. These tests do not
replace native adapter, in-game rendering or real host/client acceptance.

## Native origin and callback reentry

Payment history and its version1 format remain unchanged. Native command attribution
requires an unchanged active pending state as well as the existing issuer/mode/identity
checks; completed commands cannot authorize later raw spawns. Complete SE spawn-wrapper
scopes include Pre and Post. Recursive raw spawns cannot acquire the primary payment
identity; a nested paid prefab operation owns its own transaction.

Negative gold offsets are applied only to the native gold-add calls belonging to the
current refund frame. The existing live SE resource chain still receives its normal Pre,
Skip, Original and Post behavior. Independent gifts from resource callbacks or other
callbacks do not consume that offset; nested refunds restore the outer context. The
material float conversion order and stationary-unit integer arithmetic are unchanged.
