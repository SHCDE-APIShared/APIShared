# Staged numeric modifiers

This guide describes scalar composition for mod authors. Registries still own
their domain's lifetime, activation, ordering, units and final safety rules.
The shared arithmetic types live in `APIShared.Modifiers`.

The [production service](WORKER_PRODUCTION_BONUS.md) reuses the exact staged
composition before final rounding: output-base fractions accumulate in saved
credits, while fixed percentages round once and reject unsupported results.
Its visible preflight checks replace silent final clamping; the pure calculator
and the existing scalar services below retain their documented clamp contracts.

## Composition and use

`ValueModifierContribution` is an immutable value with `Kind`, `Value` and `Stage`.
Use `None` to contribute nothing, `Factor(decimal, stage)` for a nonnegative
factor, or `FixedValue(decimal)` for an absolute value in the receiving service's
units. The default stage is `BeforeFixedValue`. Negative factors and undefined
stages throw; signed fixed values are permitted because popularity is signed.

The complete calculation is:

1. Multiply the baseline by every `BeforeFixedValue` factor.
2. If any fixed value applies, replace that result with the last ordered fixed value.
3. Multiply by every `AfterFixedValue` factor.
4. Round once to an integer, with midpoint ties away from zero, then clamp to the
   receiving domain's inclusive bounds.

For baseline 100, normal factor 2, fixed value 60 and after factor 1.5, the result
is 90. Without the fixed value it is 300. Registration order selects the winning
fixed value; a factor's stage determines whether it affects that value, even when
its registration appears before the winning fixed registration.

```csharp
using APIShared.Modifiers;

decimal result = ValueModifierCalculator.Calculate(100m,
    new[] {
        ValueModifierContribution.Factor(2m),
        ValueModifierContribution.FixedValue(60m),
        ValueModifierContribution.Factor(1.5m, ValueModifierStage.AfterFixedValue)
    }, 0m, 1000m); // 90
```

The pure calculator accepts already ordered contributions. Null means an empty
sequence; bounds must be ordered integers. It does not register policies or read
game state. Decimal inputs are composed as exact rational numbers, so an
overflowing intermediate product followed by reciprocal factors does not lose
information. Clamping happens only after the final rounding.

## Popularity policies

Use [FearPopularity](../src/Economy/FearPopularity.cs) and
[PopularityOverrides](../src/Economy/PopularityOverrides.cs) with their existing
owner-bound process-lifetime registrations. Register after native initialization;
configure synchronized activation at the consumer's supported mission boundaries.
Order remains ascending `order`, ordinal owner GUID, then local registration ID.
Setters detach inputs and publish immutable selections. A setter replaces that
handle's entire configuration, including its previous legacy or typed settings.

`PopularityOverrideRegistration.SetModifiers` accepts source-keyed
`PopularityModifierValues`, with independent Human and AI contributions in whole
report points. `None` leaves that role without a contribution. Legacy
`SetContributions` values participate as fixed values; their public validation
and old-only results are unchanged. Unknown players retain the effective global
SE value. Typed results clamp to -85,899,345 through 85,899,345 whole points before
conversion to native units (`points * 25`).

For fear, `FearPopularityRegistration.SetFactor` adds a genuine relative factor.
`SetLevelModifiers` accepts role-specific factors or fixed points per signed
nonzero fear level. Unspecified levels reveal earlier matching rules, then the
effective SE calculation. Level zero always remains neutral. Unknown players
receive only uniform factors and legacy global policies.

The legacy fear method `SetMultiplier` retains its absolute coefficient contract:
it first calculates `round(fearLevel * coefficient)` in whole report points.
When mixed with typed factors, that already rounded legacy result is a fixed
contribution. It therefore ignores normal factors and accepts after factors.
Its previous overflow fallback remains in force if that coefficient is still
the selected fixed policy. An overflowing coefficient superseded by a later
fixed policy is irrelevant. `SetLevelContributions` and
`SetTargetedLevelContributions` also participate as fixed values. Typed fear
results use the same bounds and conversion as ordinary popularity. Simulation
and report correction capture the same selection and actual SE baseline.

## AI market reserve and sale quantity

The existing [AI market broker](AI_MARKET_SELL.md) retains its callback order,
owner lifetime, reentrancy, thread, enablement and final native safety contracts.
Reserve and quantity contexts add `Contribute(ValueModifierContribution)`.
Contributions resolve after all callbacks. A legacy `Reserve` or `Amount` property
write is a fixed contribution at that callback's position. Property reads still
return the ordered proposed legacy value; they do not resolve staged factors.
Use `OriginalReserve` or `OriginalAmount` for the immutable native baseline.

An exception rolls back the entire failing callback, including typed
contributions and legacy writes, before later callbacks continue. A retained
context remains closed after dispatch, including its new `Contribute` method.
Composition rounds once and clamps to signed Int32; the native broker then
enforces all active OR-combined reserve/stock safety constraints. Typed modifiers
cannot bypass those final constraints.

## Implementation and verification limits

Popularity registries compile reduced rational plans on configuration changes.
Common fear levels and configured exceptional keys use precompiled selections;
the normal integral read path avoids managed allocations. Other signed fear
levels remain supported and may require temporary plan construction. The public
pure calculator compiles on each call and is intended for initialization or
configuration, rather than repeated frame-level calculations.

Core tests cover stage precedence, signed rounding, exact overflow cancellation,
final bounds and callback rollback. Popularity tests cover mixed legacy/typed
policies and Human/AI selection. The native AI market fixture executes production
callbacks, sale/decision code and final safety against the installed hash-bound
DLL in private memory. These fixtures do not replace synchronized multiplayer or
in-game presentation acceptance tests.
