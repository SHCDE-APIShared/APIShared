# Worker production modifiers

`APIShared.Economy.WorkerProductionModifiers` owns three shared channels for
actual output base amounts, the fixed production percentage and the production
percentage contributed by each fear level. It preserves ingredient
withdrawals and the existing Extender Pre/native/Post chain. Consumer mods own
their settings and localized presentation; APIShared owns composition, exact
precision validation, native protection and admission state.

## Desired profiles and admission

Call `TryRegister(ownerGuid, registrationId, out registration, out diagnostic,
order)` once. A valid unique identity is accepted even before native availability.
The process-held handle starts inactive. `SetProfile(profile)` records detached
desired state; `SetProfile(null)` removes only that owner's contribution. Supplying
a profile never starts native execution. Invalid desired state remains visible
and blocks admission; it never silently reveals the previous valid rules.

```csharp
using APIShared.Economy;
using APIShared.Modifiers;
using SHCDESE.Interop;
using System.Collections.Generic;

if (WorkerProductionModifiers.TryRegister("example.mod", "production",
    out var handle, out string reason))
{
    handle.SetProfile(new WorkerProductionProfile(
        new Dictionary<eChimps, WorkerProductionValues> {
            [eChimps.CHIMP_TYPE_WOODCUTTER] = new WorkerProductionValues(
                ValueModifierContribution.Factor(1.25m),
                ValueModifierContribution.Factor(2m))
        },
        fixedBonusPercent: ValueModifierContribution.FixedValue(0)));
}
```

Base contributions distinguish human and AI ownership. The 17 producer types are
listed by `SupportedWorkers`. Quarry Grunt means the stone loaded onto its Ox.
Both apple-producing paths share one role. Baker means bread output, not flour
pickup. Miller modifies its actual flour output of one; the productivity result
discarded during Vanilla wheat pickup is not reintroduced. Ingredient quantities,
work cycles and existing stocks are not multiplied or refunded.

An optional `GameplayModActivationProfile` is evaluated by the shared service
against the prepared mission mode. A recognized disallowed context contributes
nothing. Before the real mode exists, admission commits a finite immutable launch
bundle containing the resolved arithmetic for every recognized mode, launch
variant and multiplayer flag. This includes Tutorial for unconditional owners;
an optional policy can deny that owner without removing the mode for others.
These hypothetical inputs are arithmetic cases, never game-mode authority. The
actual loader must select its exact frozen member before activation. One owner's
profile never restricts another owner.

Selecting a new saved-precision source invalidates the previous prepared admission
object even when its denominator leaves the exact arithmetic unchanged. Preparation
then builds a fresh candidate and revision; an older candidate cannot be activated
merely because its digest still matches. Saved fractions are neither reset nor
rounded by this invalidation.

Owners are ordered by ascending priority, ordinal GUID and ordinal registration
ID. Each channel uses [staged numeric composition](VALUE_MODIFIERS.md). Ordinary
factors precede the winning fixed value and explicit `AfterFixedValue` factors
follow it. The fixed-bonus baseline is 50 percentage points. That channel rounds
once away from zero at midpoint and rejects values outside -100..1000. It retains
Vanilla's mode and per-call bonus flag. Wheat, flour and bread remain ineligible
for this fixed contribution.

### Fear levels

Per-level fear rules require APIShared 0.40.0 or later.
Supply `fearBonusPercentByLevel` on the four-argument `WorkerProductionProfile`
constructor. Each key is a native signed fear level from -5 to -1 or 1 to 5.
Neutral level 0 always contributes 0 and is invalid as a profile key. Each level
starts at `max(-10 * level, 0)` percentage points and composes independently.
Omitted levels retain their Vanilla contribution. For example, a factor of 2 at
level -3 changes +30 to +60. A factor alone at level +3 operates on 0, and a
fixed value there can supply either a bonus or a reduction. Contributions affect
actual output, including wheat and bread. The separate miller flour output and
wheat or flour pickup follow their existing rules.

```csharp
// After successful registration, set a profile on the returned handle.
handle.SetProfile(new WorkerProductionProfile(
    baseAmounts: null,
    fixedBonusPercent: ValueModifierContribution.None,
    activation: null,
    fearBonusPercentByLevel: new Dictionary<int, ValueModifierContribution> {
        [-3] = ValueModifierContribution.Factor(2m),
        [3] = ValueModifierContribution.FixedValue(-25m)
    }));

if (WorkerProductionModifiers.Status.TryGetResolvedFearBonusPercent(3, out int percent))
{
    // Present the prepared fear contribution in percentage points.
}
```

Fear contributions round once at midpoint away from zero and must fit
-100..1000. The fear contribution and the separately eligible fixed production
bonus add together. Admission requires `fearBonus + min(fixedBonus, 0) >= -100`
at every level, so every supported call can produce a nonnegative quantity.
The lookup reads the immutable prepared result, including 0 for the neutral
level. It returns false for an invalid status, an unresolved mode-dependent
profile, or an input outside -5..5. It does not read the current fear of a player.

### Exact base amounts and validation

Base amounts are composed exactly. Fractional credits accumulate per unit in
production-owned persistence, independently of the signed native bonus remainder.
No decimal multiplication, callback, registration lock or arbitrary-precision
arithmetic runs for an individual worker. Preparation produces a 0..100 input
lookup for each role and ownership category and validates the combined precision
of those rules and saved credits. Their exact common denominator must fit Int64.
Miller's actual-output site always selects baseline one; its other lookup slots
are reserved identity entries rather than additional supported Miller inputs.

The original native arithmetic additionally limits a transformed integer base to
`floor(3276700 / (100 + max(50, highestFearBonus) + max(fixedBonus, 0)))`, covering
Vanilla productivity through 150, configured increases, bonus-ineligible calls
and mode 0. With Vanilla fear, the limits include 21844 at a fixed bonus of 0 or
less, 16383 at 50 and 2849 at 1000. Fractional values must also fit when their
credit produces the next integer. Miller's separate actual-output channel permits
0..32767. Validation rejects negative, oversized or unrepresentable base amounts.

`Status` is an immutable preflight result with `CanStart`, request-scoped
`IsAvailable`, `ResolvedFixedBonusPercent`, `Digest`, `ServiceKey`, and typed
`Issues`. Fixed-only requests need fixed-bonus protection. Base contributions,
effective fear changes and retained production state need the full native backend.
Issues include `Code`, `LocalizationCode`, exact `ActualValue`, `Limit`, technical
`Diagnostic` and, where applicable, the role/category/input or `FearLevel`.
Consumers must surface blocking issues to the player. Peer-readiness presentation is separate
in `PeerReadinessCode` and `PeerReadinessDiagnostic`.

`Status.Registrations` includes immutable owner/registration/priority snapshots in
composition order, including disabled registrations with a null `DesiredProfile`
and profiles excluded by the prepared mode. The profiles have detached input
collections. Keeping a status retains those desired rules even after a handle is
changed; peer and rejected-launch notifications preserve the same snapshots.

Use `Status.TryGetResolvedBaseAmount(worker, isAIPlayer, inputAmount, out amount)`
to read the prepared composition without reproducing its arithmetic. The result
has `Whole`, `FractionNumerator` and `FractionDenominator`: a canonical proper
fraction such as 1/4, regardless of the larger common denominator used internally
for saved credits. Integral results use 0/1. This is the output base before native
productivity, the fixed bonus and accumulated per-unit credit, not the currently
executing mission's output. Inputs are 0..100; Miller supports only actual input
1. Invalid requests, unsupported inputs and unresolved mode-dependent bundles
return false with a default zero amount. Earlier statuses keep their own resolved
values; each status retains an existing immutable compiled table rather than
allocating thousands of presentation objects.

`TryRegisterObserver` installs a process-lived, optionally replayed observer.
Callbacks are ordered, exception-isolated and outside registry locks. They run
on the mutation/preparation caller thread; consumers must dispatch their own UI
updates. Peer-receipt presentation changes do not change the arithmetic revision.

The mission coordinator prepares one immutable rule set before peer/save
admission and activates exactly that prepared revision at the native mission
checkpoint. A status digest is arithmetic identity, not proof that peers have
acknowledged it. Configure identical desired profiles at synchronized boundaries;
thread safety alone is not gameplay synchronization. Runtime values outside the
audited native input/productivity domain are faults requiring a blocked world and
visible termination, not an alternative production calculation.

The neutral APIShared agreement coordinator uses the existing message transport
to exchange protocol/native support, the exact launch-bundle commitment, wire
mode and sidecar requirements/capabilities. A host/roster-bound challenge and
receipts qualify a launch; profile changes immediately invalidate that approval.
Prepared save/map admission also binds the selected production-record digest and required schema.
Consumers do not implement a second per-player production-readiness protocol.
Local launch rejections additionally advance `RejectedLocalLaunchSequence` with
a `RejectedLocalLaunchCode`, so a consumer can show one localized dialog for each
actual rejected attempt. These presentation notifications do not alter prepared
arithmetic or call UI from APIShared.

## Received production goods

`ProductionGoodsReceived` (APIShared 0.52.0) is a post-only event for proven
production credits into the stockpile, granary or armoury. Register once with
`TryRegister(ownerGuid, registrationId, callback, out handle, out diagnostic)`.
The process-held handle starts disabled; `SetEnabled` changes logical demand.
There is no replay or cancellation. Callbacks run synchronously on the simulation
caller, outside registration locks, ordered by priority, ordinal owner GUID and
registration ID. Exceptions are isolated. Consumers own mode permissions,
synchronized activation and any gameplay rewards; the event is not a network
transport. Dispatch UI work separately.

For gameplay rewards, supply `onMissionAdmitted` and `onMissionEnded` when
registering. Admission receives the session ID and authoritative mode under the
shared engine gate, before its first advancing run. Snapshot prepared settings
and enable demand there. Retirement closes the run gate before notifying the
same session. Managed `MissionStarted` may follow native simulation startup and
must not arm or reset a reward consumer. Callbacks run once per admitted session;
the required owner's admission exception blocks world release.

Declare required support from synchronized desired settings with
`handle.SetRequiredForMission(required, activationProfile)`, separately from live
observer demand. The shared production admission gate checks that requirement
against the actual mode before releasing any world, including local saves and
the editor. Null activation permits all recognized modes. Consumers should also
use lobby launch guards to show a localized rejection before loading begins.
Check `IsAvailable` before admitting a feature which requires complete receipts.
`Diagnostic` explains missing managed/native source support, including a failed
ImprovedHunters Tanner integration. Observer registration alone does not prove
availability. Unavailable capture leaves the existing original game paths intact.

Each immutable `ProductionGoodsReceipt` contains `PlayerId`, `Good`, `Amount`,
the storage building ID/GlobalId, the transport unit ID/GlobalId, and the economic
`ProductionRole`. IDs are one-based. Stone deliveries identify the actual Ox or
Quarry Grunt carrier while using the Quarry Grunt production role. These are
historical identities, not retained pointers. `Amount` is the actual positive
credit and already includes output-base factors, productivity and applicable
production bonuses. Do not multiply it again when calculating a reward.

Only final production-delivery call sites provide provenance. Start goods,
purchases, gifts, refunds, local production buffers and ingredient transfers do
not emit receipts. Neither raw Wood Logs nor intermediate Cow Hides or Refined
Pitch are usable completed output here. Generic stockpile Post events, transport
statistics and cargo decreases cannot establish receipt success. The native game
can decrease cargo even when storage rejects the operation.

APIShared captures the real carrier before foreign callbacks, claims the source
at SE-wrapper entry, and observes effective arguments around the single Original
invocation after mutable Pre. Successful native return, stable storage identity
and an exact positive local credit must agree. Unrelated nested additions never
inherit production provenance. Ambiguous readbacks emit no receipt. Publication
occurs after the wrapper returns, or from its exceptional exit after an already
proven mutation; no original call is retried. Dynamic weapon recipes and both
ImprovedHunters Tanner routes report the final effective goods selection.

For the existing external Tanner owner, `TryGetNativeDeliveryAdapter` returns a
permanent native entry with the exact SE stockpile primitive ABI, documented on
the method. It is only for audited final-delivery sites, never arbitrary goods
creation. With no enabled observer it forwards entirely natively. ImprovedHunters
uses it in both permanent routes and calls `ConfirmExternalTannerAdapter` only
after successful stub publication. If that plugin is absent APIShared owns the
ordinary Tanner source itself; the two owners never patch the same span.

The event is additive and does not change existing modifier registrations,
Extender events, production arithmetic or saved production formats. Native source,
ABI and SE-wrapper fixtures do not replace live game and real multiplayer
acceptance. See the receipt section in [native update contracts](../UpdateToNewDLL.md).

## Legacy fixed-bonus facade

`WorkerProductionBonusOverrides` retains its public registration identities,
-100..1000 whole-percentage validation and unavailable-registration failure.
`TryRegister` succeeds only when the original fixed-bonus native cohort is ready;
the newer profile API can retain desired state while support is unavailable.
The facade's `CompatibilityKey` identifies only that original cohort, not the
additional base-output/persistence service or a peer receipt.

### Registration and units

A process-held legacy registration starts inactive. `SetPercent(null)` removes
its contribution; `SetPercent(50)` is an explicit `FixedValue(50)`. It participates
in the same ordered staged composition as profiles. An out-of-range value throws
and preserves the previous contribution. A rejected publication rolls that
registration back; it does not silently apply part of an invalid request.

Legacy synchronized immediate percentage publication is retained when a concrete
compatible calculation is available. Pending base and fear edits stay staged,
and the running world's wire mode stays frozen. Before a real mission mode exists,
mode-dependent desired profiles remain staged until the loader selects the
admitted bundle member. A legacy percentage change retains the admitted fear
table, base table and credits, and revalidates their combined bounds.
The fixed-only availability path may operate without the full base cohort only
when no base contribution or retained extended state requires that cohort.

For base one and neutral fear, 50 gives 1,2,1,2; 0 gives one each time; -50 gives
1,0,1,0. The fear contribution remains additive. For example, +50 from fear and
-50 here produce the ordinary base quantity. Native mode and per-call bonus eligibility
remain authoritative. Changing settings does not clear existing native remainder
credits, so -100 can still release an earlier accumulated item. Ingredients remain
consumed even if the completed output is zero.

## Mission and multiplayer responsibility

All consumers must stage identical desired profiles at synchronized settings or
mission boundaries. Thread safety alone is not gameplay synchronization. The
shared owner prepares a finite launch bundle, selects its actual mission member,
restores saved credits and opens simulation only after the complete admission
checks succeed. Unknown modes and missing peer receipts keep simulation admission
closed. A late peer confirmation can release that pending admission; it cannot
change the already chosen world protocol. Invalid runtime input or state faults
and retires the world instead of running a replacement calculation.

### Neutral shared agreement

APIShared registers its production message channel regardless of consumer demand
or native availability. Its protocol runs over the existing sender-authenticated
`MessageChannel`; it is not an ExtraFeatures setting or `[SyncPerPlayer]` report.
The current authenticated shared lobby snapshot supplies the human roster.
Challenges and receipts bind the current host, lobby, roster and fresh nonce.
They compare native support, resolved launch-bundle digest, native/extended wire
mode, sidecar capability and required schema, plus the selected archive's production
record digest. Missing reports never mean an explicitly unavailable service.
Older or missing production-protocol peers cannot qualify a multi-human launch.

Agreement applies even with no profile, ExtraFeatures disabled, or a resolved
bonus of 50: permanent completion guards can still affect historical batches.
Matching unavailable fixed-bonus services can retain ordinary Vanilla behavior
only if no requested rules or saved history need them. Base output or retained
extended credits require full compatible support on every participating peer.
A known solo Steam roster validates its local requirements without a peer echo;
unknown or unresolved rosters remain unready.

`LobbyLaunchGuards` invokes the common policy before Ready/Start. Its immutable
`RequiresPeerAgreement` distinguishes Vanilla's command-specific solo Skirmish and
single-player Coop branches from fresh network lobbies; in-game multiplayer flags
cannot classify a lobby that has not started. Unready/unlock remain usable.
Prepared save loading checks the same guards after settings preparation and fresh
fingerprints, before host release, client ACK and final native admission.

The canonical selected local map is staged before Ready/Play as well as before
map preparation. Map and save coordinators wait for production agreement before
GO/ACK and recheck it at final admission. Their existing file fingerprints and
record comparison continue to apply. Actual restored production bytes must match
the frozen requirement and denominators. Cleanup of a completed preparation does
not withdraw the active world's frozen descriptor. Changes for a following world
do not renegotiate the protocol of the running world. No new per-frame production
reporter or private roster detector is installed.

### ExtraFeatures presentation

ExtraFeatures is one profile consumer. Its host-owned whole-percentage setting
is locked during a mission and uses its normal synchronization, preset, reset and
save-settings paths. Enabled values other than 50 stage a fixed contribution with
ExtraFeatures' activation policy. Setting 50 or disabling ExtraFeatures stages
`null`, leaving other owners' ordinary factors and fixed values visible.
ExtraFeatures owns no second native hook or production-readiness transport.

The UI shows the resolved shared bonus, blocking issues, and a warning outside
search/topic filters, with local details. It does not label an invalid request's
fallback 50 as an effective result. `RejectedLocalLaunchSequence` and
`RejectedLocalLaunchCode` identify actual rejected local attempts for a localized
dialog. These presentation updates do not change arithmetic revisions or prepared
rules. Runtime-fault snapshots are latched immediately on the observer's caller
thread and displayed once through deferred Unity presentation, even if world
retirement clears the live fault before that presentation runs. APIShared's
production domain itself does not construct consumer settings UI.

## Native and Extender integration

The service retains the existing Extender Pre/native/Post chain and mutable event
parameters. Pre changes to the base quantity are inputs to the shared base table;
Post changes to the returned quantity remain Post changes, outside that table.
Inputs outside the audited 0..100 domain require a new audit, not silent clamping.

An Extender Pre subscriber can redirect `UnitId`. Fear-level lookup and the
signed native bonus remainder follow the redirected target. Shared base
role, Human/AI selection, fractional credit and batch provenance belong to the
original physical producing worker or Ox, which receives the returned cargo.
A process-held, validated wrapper context preserves that original actor across
nested synchronous calls and restores it on exceptional exits. It does not change
the Extender field type or the event's target. This distinction also prevents a
redirected target from leaving an empty physical worker without its completion.
Miller's separate actual-output site uses baseline one after manufacturing;
its wheat-pickup helper result remains discarded as in Vanilla.

Field workers keep the outgoing route Vanilla already created. After successful
arrival, completed empty cargo enters the full existing work-finished branch
before storage search/deposit. Workshops finish manufacturing and animation, then
skip their first storage search when empty. Work statistics, pauses, next ingredient
selection, farm continuation and invalid-workplace handling retain their native
completion branches. No worker state is reset from the yield event.

Reduced stone batches of one through seven may depart; zero retains the normal
wait/return flow. Quarry input-stack thresholds and work counters stay unchanged.
A zero reserve cannot create a driver load. Negative extra-output values are not
drawn as positive yield symbols, without rewriting the stored native value.
These guards also protect already produced batches after disabling an owner or
changing to 50/mode 0. Guard publication is permanent and logically activated;
partial installation cannot enable negative production or incomplete safeguards.

## Zero output, transport and persistence

There are two independent fraction domains. Vanilla retains its signed Int16
bonus remainder in the existing raw WORD; save section 3015, Chore56 and checksums
preserve its bits unchanged. The Extender's unsigned view must be interpreted as
signed when inspected. New exact base fractions and batch provenance use a private
32-byte sidecar per unit slot, keyed by slot and GlobalID. Profession changes
preserve the credit while normalizing role-specific provenance; slot reuse cannot
inherit another unit's credit. Save/checksum/pack normalization uses copies and
never mutates one peer merely because it saved.

The versioned `APIShared_WorkerProduction` archive record has entry `state`, WPS1
schema 1 and scope SaveGamesAndMaps. It contains a validated row table. Candidate
bytes are decoded privately, then the whole table and restored native identities
are validated before any row is installed. Saved denominators participate in the
same exact precision compilation as desired rules. Nonempty restored state keeps
extended support required even after all contributing profiles are removed.
Native unit records and public Extender field layouts remain unchanged.

A world with a negative fear contribution uses the existing extended wire and
save contract so a reduced load stays associated with the carrying Ox even when
an Extender subscriber redirected the yield calculation. A world without base
contributions, negative fear contributions or retained extended data can use the
original Chore56 payload and checksum contract. An extended world appends one
validated row to each transferred unit and includes canonical sidecar state in checksums.
Incoming extended packets are fully checked under the engine monitor before the
native record can be changed. Invalid tails and unsupported compressed transfers
fault the world. The chosen native/extended wire mode is frozen for the mission
and never downgraded midway through it.

An absent production record, or an explicit empty entry set, is a legacy archive.
Malformed present payloads or unreadable archives are rejected. There is no native
mandatory-sidecar marker that can recover deliberately stripped archive metadata;
this limit must not be confused with the guarantees for a correctly written
extended save. Existing native reduced-batch guards remain separate from the new
base fraction format.

### Atomic saves and visible failure

Extended normal/editor, synchronized and trail restart-info saves use one common
critical native writer boundary. The existing participant-identity save scope
remains its sole hook owner: production enters inside that scope's real engine
monitor and protected identity, before its cached native Original. Its own native
cohort observes only write/close, and cannot become available without that existing
writer scope. Before any native open/truncation, a validated immutable sidecar
snapshot is frozen under the same monitor. Only the
physical output destination is redirected to a sibling temporary file; the
Extender's logical save context and Vanilla map/header identity remain unchanged.

Commit requires successful native writes and close, successful capture of this
exact snapshot, and rereading the temporary archive to verify the actual record
byte for byte. Only then does the existing atomic replacement helper replace the
destination. Short/error writes, close failures, omitted/stale sidecars, capture
or replacement failure preserve the old destination and return failure. A faulted
world cannot save. Ordinary legacy saving retains the original path.

Managed save continuations do not publish names, clean editor state, workshop
previews/uploads or later success effects after a newly failed critical save.
The same dialog remains retryable and failure is shown locally. This validates
the production snapshot and actual I/O outcome, not every compressed native
section's semantics. Details and hash-bound update obligations are maintained in
[the native update contract](../UpdateToNewDLL.md#worker-production-bonus).

## Other production consumers and migration

ImprovedHunters' existing Pre base amounts remain inputs before the shared bonus
calculation. Tanner completion supports its armour and meat paths without
accessing private ImprovedHunters tables or replacing adjacent hook spans.
Co-installed execution still requires the separate in-game acceptance below.

A future Tweaker integration should translate its desired worker/Human-AI output
multipliers into this profile API, retain its own settings and activation policy,
and remove its former yield Pre scaling, Post clamp and `MillerFlourPatch` as one
coordinated migration. Leaving those mechanisms enabled would scale twice, force
reduced Ox loads back to eight, or compete for the Miller output sites. Its old
private remainder bookkeeping is not the shared persisted credit format.

An unmigrated Tweaker production implementation is therefore not an alternative
provider behind this service. Its Post changes cannot be reconstructed from a
profile digest, and native site ownership may reject overlapping patches. There
is no heuristic detection/rewrite that claims to merge arbitrary Pre/Post changes
or old private fractions. This feature does not modify the external Tweaker;
that handoff and its behavioral/save migration need their own acceptance.

## Verification limits

Compiler and native execution fixtures cover exact staged composition, invalid
precision, saved denominators, generated hooks, register/stack preservation,
original-actor versus redirected-target behavior, zero completion, sidecar
checksum/pack validation and critical-save failures. Managed fixtures cover
profile/preset locks, warnings, fault latching and coordinator ACK/GO/admission
boundaries. These are executable boundary checks, not proof of live Unity
rendering, every co-installed hook, Steam delivery or host/client gameplay.

On 2026-11-09 a local single-player skirmish verified the installed yield hook.
For a ten-unit base, fear levels 0, -3, 3 and 4 yielded 10, 16, 20 and 0 with
the test profile. The same run verified miller delivery with free storage and
a production save, reload and restart.

The yield calls in that run were isolated probes. Complete worker cycles with
unavailable, full or removed storage, animation and ingredient handling,
reduced stone reloads, ImprovedHunters integration, editor coverage of the fear
rules and real multiplayer require separate gameplay checks.
