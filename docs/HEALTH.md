# Shared maximum health

For consumer authors, APIShared 0.28.0 provides `APIShared.Health.HealthModifiers`.
APIShared owns the coordinated maximum-health writes; consumers supply policies,
classification and synchronized settings. Do not also rewrite these objects' HP.
The [staged arithmetic](VALUE_MODIFIERS.md) is shared with the economy services.

## Registration and ordering

Register after Script Extender `LibraryLoaded`, after APIShared initialization.
`TryRegister(ownerGuid, registrationId, provider, out handle, out reason, order)`
returns a process-rooted, initially disabled handle. Identities must be nonempty
and unique within the health registry. Ascending order, ordinal owner GUID and
ordinal registration ID determine the winning fixed value: the last wins.
`handle.SetEnabled(bool)` changes logical participation. Configure deterministic
settings before mission initialization on every peer; the service does not send
settings or infer multiplayer authority. Callbacks run on the simulation caller,
outside the registration lock. They must be bounded, deterministic and read-only.
An exception or nonpositive HP fixed value discards only that callback's result.

`HealthModifierContext` exposes kind, one-based game ID, GlobalId, unit/building
type, controlling player and AI classification at initialization, original maximum
HP and initialization reason. It contains no raw pointer or retained game object.
All numbers are internal HP, not displayed HP. `None` leaves an object untouched;
`Factor(1)` explicitly selects its saved original basis, including on load.

```csharp
using APIShared.Health;
using APIShared.Modifiers;

HealthModifiers.TryRegister("Example.Author", "health/default", c =>
    ValueModifierContribution.Factor(1.5m), out var normal, out var reason);
HealthModifiers.TryRegister("Example.Author", "health/absolute", c =>
    ValueModifierContribution.FixedValue(60000m), out var first, out reason, 10);
HealthModifiers.TryRegister("Example.Author", "health/winner", c =>
    ValueModifierContribution.FixedValue(80000m), out var last, out reason, 20);
HealthModifiers.TryRegister("Example.Author", "health/final", c =>
    ValueModifierContribution.Factor(2m, ValueModifierStage.AfterFixedValue),
    out var after, out reason);
// Check each registration result before using its handle.
// When enabled, 80000 replaces baseline * 1.5, then * 2 requests 160000.
// A building will still be capped at its domain-specific safe maximum.
```

## Frozen initialization lifetime

The basis is captured after native initialization has completed. Vanilla owner HP
modifiers, Lord profiles and earlier type-default changes therefore form part of
the basis. Unit type initialization queues concrete identities; HP writes wait
until the enclosing simulation path has completed. Recruitment, worker assignment,
disbanding and same-type reinitialization start new generations even when game ID
and GlobalId are unchanged. Buildings are queued by the existing spawn event and
processed after integration. A single inventory reconciliation covers completed
map start/load. There are no repeated world/HP scans or owner-change monitors.

Each generation applies once, preserving the current health fraction and updating
unit percentage/bar caches. Dead, zero-current and zero-maximum objects are never
revived. Later ownership, policy and type-default changes do not rewrite an
initialized object's maximum. Enablement changes affect future initialization;
they are not a live HP editor. Consumers decide which types participate and which
modes are allowed. ExtraFeatures disables all eight HP settings in the editor.

## Safety and siege towers

Positive HP uses midpoint rounding away from zero, then domain bounds. Units are
limited to 21,474,836 internal HP for signed native `HP * 100` readers. Ordinary
buildings are limited to 32,767 and to
`Int32.MaxValue / max(100, positive wood cost, positive stone cost)` for native
repair arithmetic. The request remains unchanged in consumer settings.

A siege tower unit and its later building proxy are one object. The unit's policy
also determines the proxy; building policies are not applied again. Both original
bases are frozen for that unit generation, including a save before its first HP
tick. An integer binary search selects the largest permitted unit target no
greater than the request; the proxy is rounded proportionally. Both individual
bounds and `unit maximum * proxy maximum <= Int32.MaxValue` must hold. A late
proxy receives the frozen target only after both reciprocal game/global links
match; it does not rewrite the existing unit. Native damage arithmetic is intact.

## Save data and migration

The bounded binary archive `APIShared.Health.v1` retains bases, kind/game/global/
type identity, generation and tower linkage bases/targets, even with neutral
`Factor(1)` settings. Completed loading applies the selected settings once from
these bases. The existing consumer modsettings system chooses saved or current
settings; this service does not override that choice or divide saved maxima.
Repeated loading therefore does not compound multipliers. Invalid archives fail
closed rather than treating already scaled maxima as fresh bases.

`TryRegisterLegacyBasisProvider(owner, id, reader, out reason, order)` accepts
verified `HealthLegacyBasis[]` from a consumer's old save format. A null result
means absent/stale/invalid archive; an empty array means a valid empty archive.
Readers must verify archive identity and return original positive Lord maxima,
unit IDs and GlobalIds. APIShared validates the live Lord identity and knows no
consumer-specific serializer/type. Errors are isolated. ExtraFeatures imports
its former `ExtraFeatures.LordHealth.v1` through this contract and no longer writes
that archive or HP directly.

Old saves without shared bases never enable soldier, civilian or building scaling,
including future spawns and subsequent resaves. Valid imported Lords remain
eligible. An existing Lord without a valid imported record remains untouched,
including after resaving; a positively observed new Lord initialization can supply
a new native basis. This eligibility is saved explicitly; no reverse-division guess exists.

## Verification limits

The production-source health fixture checks initialization, freeze, injuries,
death, generation reuse, pending saves, repeated loads, legacy exclusion, corrupt
data, repair limits and the linked tower bound. Backend tests validate the private
Indirect/10 detour and full continuation against the canonical native image.
These tests do not replace real new-game, spectator, editor, save-selection and
matching host/client acceptance. Native update details belong in
[UpdateToNewDLL](../UpdateToNewDLL.md#shared-health-initialization).
