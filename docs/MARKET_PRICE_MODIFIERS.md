# Shared market base-price modifiers

`APIShared.Economy.MarketPriceModifiers` composes prices in native base-price
units: one lot of five goods, separately for buying and selling. It owns the
current price table and reuses APIShared's existing native market helper owner.
Consumers must not install competing price hooks or repeatedly rewrite the table.
See [shared numeric stages](VALUE_MODIFIERS.md) for the common arithmetic.

## Registration and lifetime

Call `TryRegister(ownerGuid, registrationId, provider, out handle, out reason,
order: 0)` during the extender's `LibraryLoaded` startup phase, after APIShared
publication. The audited native image, real engine monitor, mission lifecycle
and save-data registration must be available; failure returns `false` and a
diagnostic reason. Owner GUID and registration ID form a unique ordinal identity.
The returned `MarketPriceModifierHandle` starts disabled and remains rooted for
the process. `SetEnabled` changes logical activation; it never removes hooks.

Configure every multiplayer peer identically at a synchronized settings or mission
boundary. Call `MarketPriceModifiers.Refresh()` after changing immutable policy
state or after an intentional direct table write. Activation changes also refresh.
No periodic table scan occurs. Outside a completed mission, refresh is inert;
the configured policies are applied when the mission starts.

Providers run outside the registration lock, under the actual engine monitor.
They must be pure, deterministic, fast and nonrecursive. Do not register, change
activation, refresh, trade or modify game state from a provider. Exceptions isolate
that provider's contribution; other providers still run. `CallbackFailures`
counts isolated errors and diagnostics are rate limited.

`MarketPriceModifierContext` contains `Good`, `Direction`, `IsAIPlayer` and
`OriginalBasePrice`. It exposes no pointer. `IsAIPlayer == false` selects the
shared current-table projection. `true` selects the AI projection of the existing
buy/sell helpers; it is not a general per-player table or transaction context.

## Composition and native behavior

Contributions are ordered by `order`, ordinal owner GUID, then ordinal registration
ID. The last applicable fixed value wins. Before factors multiply the baseline;
a fixed value replaces that result; After factors multiply the selected result.
The shared calculator uses overflow-safe intermediate arithmetic and rounds only
once, away from zero. Prices are nonnegative. New composed buy base prices are
limited to 6,553 and sell base prices to 6,554, including After contributions.
These protect the native signed-short 25-good displays: buy multiplies the lot
price by five; sell divides by five before multiplying by 25. Unmodified
legacy absolute table values retain their existing behavior.

The current table supplies manual transactions, auto-trade, HUD and valuation.
The existing AI helpers temporarily receive their AI base-price projection around
their original call, then restore the current table even if the call throws.
No completed transaction total is substituted. The original caller retains its
native quantity arithmetic, rounding, bookkeeping and affordability checks.
For example, a base price of 76 remains 76 for a native lot purchase, while a
division-first five-good helper computes `(76 / 5) * 5 == 75`; auto-sale paths can
multiply before division instead. The broker intentionally preserves these
different native results.

An existing direct table writer has no registration order. A detected foreign
absolute value therefore acts as the final fixed value, preserving its authority
over all Before factors. An explicit After factor may modify it, subject to the
same safety limits. Direct writers should call `Refresh` after a change. An AI
helper also detects a changed queried cell before selecting its projection.
Prefer a registered fixed-value provider for new integrations.

## Persistence and ownership

The save-data key `APIShared.MarketPriceModifiers.v1` records original/default
bases, detected foreign absolute values, last applied output and ownership.
Recognized saved output is never used as a new multiplier base. Loading applies
the currently selected consumer settings once to those stored bases, including
native load paths that reset the table to defaults before consumer initialization.
The broker does not choose between saved and current user settings; consumers
continue to use their existing modsettings selection system.

On disable or mission end, owned cells are restored only while they still match
the broker's output. Later foreign writes are preserved. Malformed payloads are
rejected with a diagnostic. Saves without broker data preserve nondefault table
prices as foreign absolute values; the broker does not guess a baseline by
reverse-dividing an earlier mod's multiplier. Binary payloads have an exact bounded
length and version, and are independent of any consumer's types.

## Legacy veto compatibility and ExtraFeatures migration

`MarketPriceEvents` keeps its existing public signatures, original-call counts,
Pre/Post ordering and sticky veto behavior. A legacy Pre veto's `ReplacementTotal`
is still the completed helper total, and remains authoritative. It is never
converted into a base-price fixed contribution or multiplied by the new stages.
Post callbacks still observe the vetoed result.

ExtraFeatures now registers its general and per-good multipliers as a single
Before contribution. When its AI exception is selected, only that ExtraFeatures
contribution returns `None` for the AI context. Other mods' registered factors,
fixed values and After factors remain effective. This intentionally replaces the
earlier blanket default-price veto, which could erase other mods' AI changes.
ExtraFeatures currently registers no After factor.

## Read-only composition status

`MarketPriceModifiers.TryGetCompositionStatus(good, direction, isAIPlayer,
out status, out reason)` supplies a detached `MarketPriceCompositionStatus`
for settings explanations. Call on any thread outside a provider; the query may
wait for the existing engine monitor. It does not initialize the service, register
or enable anything, evaluate providers, refresh prices, trade, observe/adopt a
foreign write, or change table ownership. It returns `false` with a diagnostic
outside an initialized completed mission, before a completed cell reconciliation,
after that cell's latest failed refresh, for invalid inputs, or if the table read
fails. A failed query returns no status. No native availability is simulated.

The result distinguishes:

- `ComposedBasePrice`: the last completed result for the requested shared-table
  or AI helper projection, in base-price units per lot of five.
- `LastComposedTableBasePrice`: the shared-table result of that reconciliation.
- `ObservedTableBasePrice`: the table cell sampled now, independently of the
  cached projection. `HasUnreconciledTableChange` reports a difference without
  adopting it. It is not proof of a particular foreign writer. A reentrant query
  during an AI helper can sample its temporary substitution.
- `OriginalBasePrice` and `ForeignBasePrice`: the baseline and previously
  reconciled foreign fixed input used for this calculation. A newer sampled
  table value does not silently replace either input.
- `Contributions`: immutable provider results, including `None` and isolated
  failures, with exact owner GUID, registration ID, order, value and stage.
  `SupersededByFixedValue` identifies Before factors and fixed contributions
  replaced by the selected fixed stage. Failed providers supplied no value.

The contribution list preserves **registration order**, not a sequence of
per-row rounded operations. All Before factors multiply the baseline; the last
ordered fixed contribution replaces that result, with a reconciled foreign
absolute value taking the final fixed position. All After factors multiply that
selected result regardless of their registration position. One rounding and then
the final safety clamp follow. `UsesSafetyBounds`, `MinimumBasePrice`,
`MaximumBasePrice` and `WasClamped` describe that last operation. Passthrough
baseline/foreign values retain their existing unbounded legacy behavior; an
identity After factor does not change the foreign/no-After bypass. Query-time
clamp inspection uses only captured values and exact shared arithmetic.

`isAIPlayer: true` selects the cached contextual AI projection. Its observed
value still names the shared table, not a separate player table, a completed
helper total or the result of a future transaction. A legacy `MarketPriceEvents`
total veto remains authoritative and is not predicted by this base-price status.
Native quantity arithmetic, player authorization and transaction bookkeeping are
also outside it. Do not label the cached AI projection as an observed effective
transaction price.

Results may be retained across refreshes and mission changes; later changes never
mutate an earlier result. Queries allocate detached copies. Explicit refreshes
reuse private per-cell/projection trace storage, growing capacity only when the
enabled provider count grows. No extra trace array is created for every refresh.
This status is local explanatory information, not a synchronized settings change.

```csharp
if (MarketPriceModifiers.TryGetCompositionStatus(good,
    MarketPriceDirection.Buy, isAIPlayer: false, out var status, out var reason))
{
    // Consumer owns formatting/localization and its setting/tooltip presentation.
    ShowSettingValue(status.ObservedTableBasePrice);
    ShowComposition(status.ComposedBasePrice, status.Contributions,
        status.ForeignBasePrice, status.WasClamped);
    if (status.HasUnreconciledTableChange)
        ShowUnreconciledTableValue(status.ObservedTableBasePrice);
}
else
    ShowUnavailable(reason);
```

These UI function names illustrate consumer-owned presentation; they are not
APIShared methods. This is the market vertical of issue #47, not completion of
all cross-mod explanations. Worker production already exposes prepared contextual
status. Health, popularity/fear, population and invocation-dependent outcomes
need their own context/lifetime explanations; this market result does not model
them or replace existing native-site conflict metadata. No popup or central
conflict collector is added.

## Example

Register independent contributions with stable identities, retain the handles,
then enable them after publishing immutable configuration. These providers use
the same calculation on every peer:

```csharp
using APIShared.Economy;
using APIShared.Modifiers;

// Errors must be handled; shown as an exception here for a concise example.
MarketPriceModifierHandle Register(string owner, string id,
    System.Func<MarketPriceModifierContext, ValueModifierContribution> provider,
    int order = 0)
{
    if (!MarketPriceModifiers.TryRegister(owner, id, provider,
        out var handle, out var reason, order))
        throw new System.InvalidOperationException(reason);
    return handle;
}

var factor = Register("example.balance", "buy-factor", c =>
    c.Direction == MarketPriceDirection.Buy
        ? ValueModifierContribution.Factor(2m) // BeforeFixedValue is the default.
        : ValueModifierContribution.None);
var fixedA = Register("example.prices", "buy-base", c =>
    c.Direction == MarketPriceDirection.Buy
        ? ValueModifierContribution.FixedValue(70m)
        : ValueModifierContribution.None);
var fixedB = Register("example.override", "buy-base", c =>
    c.Direction == MarketPriceDirection.Buy
        ? ValueModifierContribution.FixedValue(80m)
        : ValueModifierContribution.None, order: 10);
var finalFactor = Register("example.final", "buy-final-factor", c =>
    c.Direction == MarketPriceDirection.Buy
        ? ValueModifierContribution.Factor(1.5m, ValueModifierStage.AfterFixedValue)
        : ValueModifierContribution.None);

factor.SetEnabled(true);
fixedA.SetEnabled(true);
fixedB.SetEnabled(true);
finalFactor.SetEnabled(true);
// The higher-order fixed value wins: 80 * 1.5 = 120, regardless of registration order.
// Very large After factors still stop at the service's final safety limit.
```
