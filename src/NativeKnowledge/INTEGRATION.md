# Native field knowledge

This directory belongs to APIShared's runtime source tree. Its public namespace is
`APIShared.NativeKnowledge`; the existing source glob includes it automatically.
It uses only System/System.Core/System.Memory, without Unity, BepInEx, SHCDESE,
publicized assemblies, workspace source links or runtime JSON dependencies.
The initial Findings sources remain historical handoff material, not a second runtime.

Integration status on 2026-10-22: source integration only, with no mod build, installation
or version change at the maintainer's request. Prior isolated fixture results are historical
evidence for the staged sources, not a build or gameplay acceptance of this integration.

## Identity, semantics and safety

Use permanent `NativeFieldIds.Fxxxxxx` identifiers for compiled integrations. The
IDs identify raw storage; current names, proposed meanings and source names are discovery
metadata. `FindByName` is exact, case-insensitive and returns every match. In particular,
`CurrentHealthRaw` is ambiguous across structures. Do not select the first result blindly.

`Meanings[0]` is the preferred interpretation. Other meanings can record competing
hypotheses or rejected history. `Confirmed` applies to the stated scope, not to every
use of the field. Unit state fields are reused by type-specific state machines.
Source names and Ghidra reconstructions are not independent native semantic confirmation.

Default reads require both a confirmed preferred interpretation and an audited storage
binding for the host-verified image. `AllowUncertainMeaning` admits Candidate/Probable
interpretations **only** when the separate storage contract is verified. It never enables
source-only bindings, unknown images, wrong record/type, short records or array spill.
Rejected preferred meanings cannot be read with either policy. Failure returns default(T)
and a specific reason; default(T) alone is not evidence of a successful read.

Exact-storage aliases survive renaming. Different-width historical declarations are
separate related entries, not implicit conversions. Examples include the former combined
`N0000019A` DWORD and the ten old Tribe attacker halfword pairs. `RelatedFieldIds` means
overlapping known storage, not semantic equivalence. New evidence must not silently redirect
an existing ID or a previously published semantic helper to different bits or behavior.

## Host integration and caller-owned views

Use `NativeFieldReader.TryGetCurrent(out var reader)` to acquire the shared immutable
reader. APIShared publishes it in the existing `LibraryLoaded` initialization sequence,
immediately after its existing native hash step. No file is rehashed by this component.
Before publication or after hash failure the method returns false/null. An unknown but
well-formed native hash is retained and reads return `UnsupportedImage`; availability
is not proof of supported layouts. Repeated initialization with the same identity is
idempotent; a conflicting identity cannot replace published state. Static publication
survives startup cleanup without additional callbacks, polling or teardown.

The public constructor remains available for independently verified/offline host views.
It is a caller-attestation boundary, not an image verifier. Do not pass `CapturedNativeHash`
merely because it is available: that constant describes evidence, not the running process.

`NativeRecordView` accepts a complete caller-owned byte span and an exact record-family
ID from `NativeRecordTypes`. It does not acquire native pointers or validate the caller's
label. For live game data the caller must acquire a valid immediate pointer through the
appropriate audited API, use the owning game thread, and retain validity until the read
finishes. A native-backed Span can still fault if the caller supplies stale/invalid memory.
The reader is not a memory-safety boundary for arbitrary pointers. Offline snapshots are
also supported. Do not retain native views across callbacks or map transitions.

All current record origins are the beginning of the named public interop view, **not**
the manager base or the first manager-relative displacement. `GameUnitManager` metadata
uses its own manager origin; its full extent is unverified and no reads are granted.
IDs/slot lookups are outside this component. Unit/building/tribe game IDs are one-based;
public spans use their documented zero-based indexes. No ID arithmetic is guessed here.
Never create/copy/box an `AivSystem` value to obtain bytes: use an audited pointer/span/ref
view if a future catalog entry covers that family.

Example for an already acquired valid Unit byte view (same code works with an owned snapshot):

```csharp
var catalog = NativeFieldCatalog.Default;
if (!NativeFieldReader.TryGetCurrent(out var reader))
    return; // Native initialization/hash publication has not succeeded yet.
if (!catalog.TryGetKey<ushort>(NativeFieldIds.F000054, out var deathMarker))
    throw new InvalidOperationException("Required field identity unavailable.");
var view = new NativeRecordView(NativeRecordTypes.GameUnit, unitRecordBytes);
if (reader.TryRead(view, deathMarker, out ushort raw, out var failure))
{
    // raw is the low death word, not proof of projectile causation.
    // This does not replace APIShared.UnitAccess.IsReallyAlive or identity validation.
}
```

For uncertain capture eligibility use `F000055` and explicitly pass
`NativeFieldReadPolicy.AllowUncertainMeaning` on that read. Native array element indexes
are zero-based **within that field**, not entity game IDs. `RaidCandidateBuildingIds`
returns raw entries from a bounded 100-element allocation; the separate live count
still determines which entries are meaningful. No count/value is silently clamped.

## Evidence and update boundaries

Capture on 2026-10-22: native SHA-256
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`;
published analysis generation `sem/FBCB9319/analysis/g1E2FBFF53E33`.
The generated baseline records Extender 2.14.2 commit
`10628df85cd432184742a50a1dee3969a66cf89e`; later source/installed Extender 2.14.3
was inspected separately. These are capture observations, not a minimum-version policy.

The offline authoring inventory examines all 84 current Knowledge Markdown documents.
Exact qualified references to source fields, explicit reviewed field maps, outpost/worker
offset notes and the later 2.14.3 supplement yield the initial catalog. This is a curated
field inventory, **not** a claim that every unstructured address or every Extender field
has been exhaustively identified. Unstructured prose and generated layout observations
remain discovery sources for future additions. No automatic import grants read permission.

Each entry retains evidence kind, portable source identifier, exact source SHA-256 and
line range. `KnowledgeReference` is only a mention; `ExtenderSource` is a declaration;
neither is promoted to a native audit. Captured source-file changes do not invalidate
unchanged native machine facts automatically. Recheck the affected integration assumption
instead of requiring a frozen workspace. Live game acceptance remains independent.

Two audited raw Tribe slices deliberately differ from source field boundaries:
target building ID at public view `+0x622` and target global ID at `+0x626` derive from
manager stores `+0x64C/+0x650` minus `0x2A`. Original instructions at `0x11FC2C`
and `0x11FC38` establish WORD/DWORD writes. The earlier isolated installed-layout fixture separately checked these bytes and full
record coverage. Its staged evidence remains historical; the integrated tests/build
have not yet been run. Neighboring source names are not aliases of these slices.

Existing native audits cover read-enabled bindings. Unknown width/signedness/extent,
partial state-machine knowledge and later upstream names remain explicitly withheld.
Do not infer a missing writer from selected-flow coverage or assume recovered aliases,
historical similarity or a matching source name establish a new binding.

## Verification and integration status

The game-independent Core.Tests suite compiles the actual production sources and contains
`NativeKnowledgeTests`: raw scalar/array reads, policy failures, stable identities/aliases,
historical split storage and immutable process publication. Authoring identities and the
captured evidence inventory are retained in `tests/NativeKnowledge`; runtime code loads neither.
No test or mod compilation was run during the source integration, by explicit request.

The existing API compatibility baseline includes the additive public surface. Relevant
build, test and gameplay checks remain due at the first authorized build. The staged
Findings fixture had passed its earlier isolated .NET 10/.NET Framework checks; this is
not evidence that the current APIShared working tree builds. No README or version changes
are part of this integration.

The authoring `catalog-identities.json` is an append-only capture ledger for this native
image, not a runtime lookup by offset or an automatic migration strategy. Preserve it when
adding fields. Moved storage must be explicitly mapped to an existing identity in a separately
audited hash binding. Baseline coordinates and source hashes remain provenance metadata;
standalone contributors do not need a neighboring workspace to run Core.Tests.
