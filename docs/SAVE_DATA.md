# Versioned save data

`APIShared.Savegames.VersionedSaveData` stores one versioned record per mod in a save, on top
of Script Extender `ModSaveDataAPI`. It adds what the raw handler leaves to every mod: an
explicit record version, migration steps owned by the mod, entries from a newer mod version
that survive an older version's saves, documented results for missing, damaged or too-new
data, and exactly one load notification per load. It does no work outside save and load.

Contracts are documented at the declarations in
[VersionedSaveDataContracts.cs](../src/Savegames/VersionedSaveDataContracts.cs) and
[VersionedSaveDataRuntime.cs](../src/Savegames/VersionedSaveDataRuntime.cs). The compiled
[SaveDataExample](../examples/ThirdPartyMod/SaveDataExample.cs) shows a complete registration.

## Usage

Register once during plugin startup (`Awake` or the Extender's `LibraryLoaded`), before the
first load whose record you need, and never from a save or load callback:

```csharp
var registration = new VersionedSaveDataRegistration(
    ownerGuid: MyPlugin.Guid,
    saveDataIdentifier: "Author.MyMod",     // ModSaveDataAPI identifier, file-name safe
    currentVersion: 2,
    minimumReaderVersion: 1,                // version 2 only added entries
    knownEntries: new[] { "settings", "garrison/" },
    capture: Capture,
    loaded: OnLoaded);
registration.AddMigration(1, entries => { /* turn a version-1 record into version 2 */ });
if (!VersionedSaveData.TryRegister(registration, out var handle, out string reason)) Log(reason);
```

A record is a set of named byte arrays (entries); the mod chooses each entry's encoding.
`knownEntries` lists the names this build owns. An item ending in `/` owns every name that
starts with it, so `garrison/` covers `garrison/17`. Entries outside the known set are not
delivered and are written back unchanged.

**Capture** runs on the Extender's save thread for every in-scope save. Write each known
entry with `writer.Write(name, bytes)` and return true. Known entries you do not write are
deleted from the save, so a destroyed building disappears with its entry. Writing nothing
still writes an empty record, which replaces older data. Return false to write nothing for
this save; the Extender then keeps the entry its archive already holds.

**Loaded** runs once at the end of every Extender archive load (`OnLoadSave` or `OnLoadMap`,
in their Post notification) with that load's result for your record, including `NoRecord`
when the save has none. Store the entries and apply them when your mission state is ready.
Use `NoRecord` and `OutOfScope` to reset state from an earlier mission: the Extender never
calls the `onUnload` callback of `ModSaveDataAPI` in 2.14.1 (see
[implementation](#script-extender-behaviour-it-relies-on)).

**Optional early preparation.** Set `registration.Prepared` only when the consumer
must decode its record before native initialization. It runs after the complete
Pre-phase archive read, including `NoRecord`/`OutOfScope`, on that load's caller.
Migrations run once at this earlier point for opted-in registrations. `Loaded`
still runs exactly once in Post with the same result and independent entry arrays;
registrations without `Prepared` retain their previous migration and callback timing.
Early preparation does not mean the saved native world is available: decode private
candidates there and use an appropriate lifecycle boundary for native application.
An exception is isolated and preserves the original record bytes. Consumers which
require protection before simulation must independently refuse native initialization
when preparation or application fails; throwing this callback alone is no load veto.

### Opt-in protected save participants

`client.CriticalSaves` explicitly enrolls an existing versioned record in the shared
protected writer. Merely obtaining the client does no work. Register and enroll on
initialized Unity startup, before any world or archive load and outside save/load
callbacks. The handle must belong to the same exact owner, provide `Prepared`, use
`SaveGamesAndMaps`, declare `SimulationRelevant`, and own a known marker entry. The
selected protection floor must be at least the record's declared minimum reader
and no greater than its current version.
Select a floor which excludes every historical consumer reader that cannot restore
the coupled protected state, raising the consumer record version when necessary.
APIShared can validate the numeric range, not the meaning of an older mod's reader;
retaining an unknown marker alone never makes that reader safe for downgrade.

```csharp
var options = new CriticalSaveOptions("critical-save", minimumReaderVersion: 3,
    prepare: PrepareCheckpoint, release: ReleaseCheckpoint);
// Set true before enrollment when this record also depends on NativeStorage.
options.RequiresNativeStorage = false;
if (!client.CriticalSaves.TryEnroll(handle, options, out var participant, out string reason))
    Log(reason);
```

Enrollment snapshots the options, including `Modes` (default
`SinglePlayerAndEditor`) and `RequiresNativeStorage` (default false). There is no
multiplayer mode. A required NativeStorage join must be requested before its first
registration. Any enrolled participant refuses this process's real-multiplayer
saves, including state-only records; this is an explicit process-lifetime opt-in.
Saving refuses while a required shared-source join is pending. A false enrollment
publishes neither participant metadata nor a native mode request. Accepted enrollment
is permanent even if later shared-source startup fails; that process remains
blocked until restart/reconfiguration. Enrollments and
callbacks live for the process, with no unregister, `Dispose` or asynchronous
cancellation. An enrolled save needs the qualified native writer and owned logical
save context; enrollment alone cannot manufacture that context.
Every archived participant also needs a complete readable origin whose positive
mission association matches the current native world. Zero or mismatched source
session IDs refuse saving, including state-only cohorts.

An enrolled source reading its original protected marker requires the **exact
ordered participant configuration**: owner GUID, record identifier, reserved marker
and reader floor. Adding or removing an enrollment, or changing a marker/floor,
makes that original passive and blocks saving. This includes adding a participant
whose record is `NoRecord` in the old archive: the still-present original partners
reject the changed cohort. No cohort-membership migration is supplied. Retain the
original participant configuration to load and save its protected archives.

This enforcement requires a registered owner to read an original protected record.
Removing APIShared or all original providers supplies no such check. A new cohort
whose members all receive `NoRecord` cannot discover protected entries belonging
to unregistered removed providers; no protection is promised for that configuration.

`Prepare(context, ref state)` freezes the participant's private checkpoint on the
save caller's thread under the actual engine monitor and outside registry locks,
in ordinal owner GUID then identifier order. The ordinary versioned capture must write that checkpoint,
without writing the reserved marker: the framework injects it. Returning false or
throwing vetoes destination replacement for this attempt. Every entered preparation
gets one `Release(context, state)`, even after false, an exception or null state.
Releases run in reverse order and independently; cleanup failures preserve the
first failure and any result already committed. Callbacks must finish synchronously
and must not start another save or load. Context flags and nullable path describe
the logical factory call, not the native temporary or destination filename.

Inside the exact current `Prepared` callback, call
`participant.TryReadPreparedProtection(load, out bool protectedRecord, out reason)`.
It inspects the registry's original envelope before migrations or legacy decoding
could synthesize a marker. A successful false result includes an absent or
unprotected record. Marker presence at the reader floor is provenance evidence,
not complete-cohort confirmation or native-state publication. The receiver cannot
be reused from `Loaded`, another thread or a later callback. For an actual newly
created editor world, `client.CriticalSaves.TryConfirmCreatedWorld(participant,
context, out reason)` accepts only the current `EditorCreated` mission and engine
boundary; it acknowledges metadata without initializing consumer state.

A protected bundle being prepared or delivered (`Written`) does not prove durable
replacement. The writer checks the actual archive records before replacement;
original protected records can be confirmed on a later complete matching load.
Ordinary versioned registrations and ordinary NativeStorage reader-2/multiplayer
saving retain their defaults. Requesting shared storage opts that process into
[its container-3 save policy](NATIVE_STORAGE.md#material-save-policy-compatibility-change).
No enrollment imports a legacy ledger or retires a consumer's stock/cargo authority.
Keep private ledgers until a complete lossless consumer migration is verified; no
Byzantine MED2 migration is enabled here. See the separate opt-in helper in the
[compiled example](../examples/ThirdPartyMod/SaveDataExample.cs), and the public
[contracts](../src/Savegames/CriticalSaveContracts.cs) and
[owner-bound client](../src/Savegames/CriticalSaveClient.cs).

### Associating archive callbacks with a mission attempt

`VersionedSaveLoad.MissionSessionId` associates an archive window with its mission attempt.
It is the process-local attempt captured once at the start of the archive window
from the owned lifecycle operation executing on that archive caller's thread.
`Prepared` and `Loaded` receive the same value for every outcome, including
`NoRecord`, `OutOfScope` and failures. Several archive windows, including nested
loads within an operation, can share one attempt ID. It is not a unique archive
invocation ID, and it is not stored in the save.

Lifecycle observers queue reentrant notifications, whereas archive callbacks run
synchronously. A callback that starts another load can therefore publish its
incoming data before that load's `BeforeLoad` notification reaches your observer.
Associate staged data with this captured ID and match later lifecycle notifications;
do not depend on the last initialization notification you received. A late end of
an older attempt must not retire data associated with its replacement.

Zero means no owned executing scope was established on the archive caller's thread.
This includes loads outside that scope and foreign-thread archive calls. A direct
nested load inside an owned operation may carry the outer attempt's ID. Never fill
in zero using the latest ready `Current` mission. `Current` may already describe a
replacement, and null can mean either pending initialization or no ready mission.

The ID supplies association only. It proves neither successful initialization nor
a current native world, original archive provenance, save commitment or permission
to import/mutate state. Native use still requires the appropriate protected lifecycle
boundary. Consumers that reference this getter must require the provider release/build exporting
it; no numeric release version is assumed here. Existing consumers that ignore it
retain their prior behavior, record formats and callback ordering.

**Scope.** `SaveGames` (default) reads and writes gameplay saves only. `SaveGamesAndMaps`
also writes map editor saves and reads every map load, for data that belongs to the map,
such as a theme chosen in the editor.

**Existing data.** A mod that already stores data under an identifier can keep that
identifier and set `LegacyReader`: data without the record signature is passed to it, and
the entries it returns continue through the migration steps from the version it reports.
Without a legacy reader, unversioned data reports `Unrecognized` and is kept unchanged, so
choose a new identifier instead.

## Record-version migration policy

This is the policy for versioned save records:

1. **One version per record format.** `currentVersion` starts at 1 and grows by one whenever
   the set or meaning of entries changes. Every save writes `currentVersion`.
2. **Declare who can read it.** `minimumReaderVersion` is the oldest version that reads this
   build's records correctly. Keep it unchanged when a version only adds entries; raise it
   to the current version when an existing entry changes meaning or encoding. A reader below
   it gets `TooNew` and keeps the record byte for byte.
3. **Names keep their meaning.** Prefer a new entry name to an incompatible change of an
   existing entry; retire the old name with a migration step that removes it.
4. **Migration steps go one version at a time.** `AddMigration(n, step)` turns version `n`
   into `n + 1`. Steps run in ascending order from the record's version up to the current
   one; a version without a step needs no change. A record from a newer version runs no
   step. A step that throws makes the load `MigrationFailed`, and the record is kept.
5. **Steps must respect entries that already exist.** An older version writes back the
   unknown entries of a newer version, so after a downgrade and upgrade a version-2 record
   can already contain version-3 entries. A step must not overwrite such an entry.
6. **Kept entries can be stale.** Unknown entries are copied verbatim while an older version
   plays on. Validate references between entries when you restore.
7. **Unreadable data is never destroyed by guesswork.** Data that is too new, unrecognized or
   failed to migrate is written back unchanged by every save of that mission. Only data
   with the record signature that fails its checksum or structure (`Corrupt`) is replaced,
   because no version can read it.

The savegame settings record that APIShared itself writes
([SavegameModSettings](../src/Savegames/SavegameModSettings.cs)) is not covered by this
policy: it accepts only schema version 3 and has no migration
(`SavegameModSettings.TryDeserialize`). Capture, validation and the reader share one set of
limits (1024 mods, 16384 settings or Trail rules per mod, 1 MiB per value), so every settings
map it captures loads. Changing that record or lowering a limit changes what existing saves load.

## Results

| Load status | Delivered to Loaded | Later saves of the same mission |
|---|---|---|
| `NoRecord` | nothing | capture |
| `Restored` | known entries after migration | capture plus the kept unknown entries |
| `OutOfScope` | nothing | capture in gameplay saves; map editor saves are not written |
| `TooNew` | nothing | the loaded bytes, unchanged |
| `Corrupt` | nothing | capture, which replaces the damaged data |
| `Unrecognized` | nothing | the loaded bytes, unchanged |
| `MigrationFailed` | nothing | the loaded bytes, unchanged |

If the Loaded callback throws for a restored record, later saves of that mission also keep
the loaded bytes, because the mod did not take them. `VersionedSaveDataHandle.KeepsLoadedRecord`
shows this state; `LastWriteStatus` reports the last save (`Written`, `KeptLoadedRecord`,
`NotWritten`, `CaptureFailed`, `TooLarge`). Failed captures and records over a limit return
nothing to the Extender, which keeps the entry its archive already holds. Every callback
exception is isolated, logged and counted in `VersionedSaveData.CallbackFailures`; nothing is
thrown into the game or the Extender.

## Public limits

- One record holds at most 16384 entries (`MaximumEntries`), the per-mod settings limit of the
  savegame settings record. Writer and reader use the same limit, so every record written is
  readable.
- An entry holds at most 1 MiB, a record at most 8 MiB, an entry name at most 256 characters.
  Kept unknown entries count toward the limits; a record over a limit is not written.
- Identifiers are unique ignoring case, because the Extender's archive entry names are.
- Thread: registration is thread-safe. Capture runs on the Extender's save thread; legacy
  readers, migration steps and Loaded run on the thread that raised the load event. APIShared
  adds no dispatch.
- Ordering: Loaded callbacks of different registrations run in ordinal owner GUID, then
  identifier order. For save loads entered through APIShared's mission lifecycle, the saved
  mod settings are applied before the native load
  ([SavegameModSettings preparation](../src/Savegames/SavegameModSettings.Preparation.cs)), so
  Loaded sees them; the mission start notification follows Loaded. Capture order across mods
  is the Extender's handler order and is unspecified; captures must not depend on each other.
- Lifetime: registrations last for the process and cannot be removed. Kept and unknown
  entries belong to one mission: they are dropped at the next load and at a map unload
  outside a load.
- Multiplayer: every peer reads its own save copy, and client copies can differ from the
  host's. Records declared `SimulationRelevant` are compared before a multiplayer save loads
  (see [multiplayer record comparison](#multiplayer-record-comparison)); other records are not.
  Nothing is synchronized or repaired.
- Load paths: the result `Source` names the Extender event (`SaveGame`, `MultiplayerSave`,
  `MapToPlay`, `EditorMap`). Multiplayer saves load through `OnLoadMap`, whose Extender
  `LoadContext` reports a map; `MultiplayerSave` is chosen while APIShared's confirmed
  multiplayer save load runs or when the native multiplayer-save flag is set, and the
  `LoadContext` flag is never used. Which other menu paths raise which event, and the thread
  of the save and load callbacks, are not yet verified in the game: single-player and
  multiplayer save loads, map and campaign starts, editor loads and saves are pending
  in-game checks, as is the multiplayer record comparison with two players.

## Prepared multiplayer save lifetime

`APIShared.Savegames.MultiplayerSavePreparation` observes the existing save-load
coordinator; it does not initiate a second load or send another protocol. Register
an owner-bound observer on Unity during initialization. The current snapshot is
immutable and may be retained or read from another thread. Notifications and
optional current-state replay run on Unity, outside registry locks, in deterministic
priority/owner/ID order; observer failures are isolated.

Each snapshot identifies the SDK attempt, revision, lobby/host, local role, detached
human roster, native save name/checksum/time, and this computer's checked local path
and SHA-256. The latter is **not** a checksum to compare with another computer's
whole save. Original native save checks and declared-record comparison remain in
force. `Preparing` is published before the first readiness evaluation, including
solo hosts that can proceed synchronously from the original call. The coordinator
waits until all `Preparing` observers have returned before its first evaluation.
A synchronous tick requested by an observer cannot bypass that delivery boundary.
If an observer starts another save, that operation's entry continuation runs after
the queued notifications, outside observer dispatch; it does not wait for a render
frame. Subsequent `Released` and `NativeLoading` observers still finish synchronously
before admission is revalidated or the ordinary native loader is invoked. Cancelled
or replaced operations cannot execute an already queued entry continuation.

The subsequent phases are `Acknowledged`, `Released`, `NativeLoading` and `Ended`.
Not every peer reaches every phase. End reasons distinguish cancellation,
replacement, timeout, rejection and ordinary scope completion. Scope completion is
not evidence that a mission started: use the mission lifecycle and advancing native
ticks for that. `Current` is cleared when an operation ends; queued callbacks can
already observe a newer operation, so always match operation ID and revision.

Use `LobbyLaunchGuards.TryRegisterReadiness` to return a typed `Ready`, `Waiting` or
`Rejected` result. Waiting preserves the existing bounded save preparation, before
client acknowledgement and host release. Final native admission remains strict.
For ordinary buttons a waiting result still blocks that click. Existing string
guards keep their original rejection semantics. The guard's optional `PreparedSave`
snapshot supplies this same checked provenance and is null for ordinary buttons.

A dialog preparation that publishes consumer data should keep an immutable local
candidate bound to the selected file. Transfer it to the matching SDK attempt from
`Preparing`, not from `AttemptFinished`: a solo host may have progressed further
before the dialog callback returns. Discard untransferred candidates on failed
attempts. On cancellation/replacement/timeout, withdraw only the ending operation's
temporary overlays. After mission ownership transfers, stale preparation cleanup
must not restore data over the new mission. Dialog `Confirmed` means its original
callback returned, not that multiplayer loading or simulation completed.

## Multiplayer record comparison

Set `SimulationRelevant = true` on a registration whose record holds simulation state that
must be identical on every player, for example unit identities or building state that the
mod's simulation reads. Leave it false (the default) for local, view or presentation data,
and keep such data in a separate record. At most 64 registrations may declare it.

**What is compared.** When the host selects a multiplayer save, the save readiness agreement
([SavegameLoadCoordinator](../src/Savegames/SavegameLoadCoordinator.cs)) reads, on every
player, that player's own copy of the selected save and takes the SHA-256 of each declared
record's stored bytes (or notes that the record is absent or unreadable). Clients send their
list with their readiness confirmation; the host compares each list with its own and releases
the load only when every player's settings fingerprint and declared records match.

**What is not compared.** Undeclared records; mod settings (the agreement already brings
every client's host settings to the saved values and compares their fingerprint); anything in
memory, such as state of an earlier single-player mission or of the lobby; other saves; the
rest of the save file, whose bytes can legitimately differ between players. Players can
therefore change settings and play single-player freely before loading a shared multiplayer
save. Loading that save then replaces earlier state: the load reports each record with
`Restored` or `NoRecord`, and kept entries of the earlier mission are dropped.

**On a difference.** The load does not start. Every player sees an explanation naming the
player, the mod (owner GUID) and the record, and whether the record differs, is missing on one
side, is unreadable, or is declared by only one side (different mod versions). At most three
differences are listed. A hash comparison detects differences only; it neither copies nor
repairs data.

**Protocol version.** The readiness packets carry protocol version 2. A player whose
APIShared build speaks the first, unversioned protocol, or a later one, is named in a message
saying the versions differ, and the load is blocked. A build that cannot read version-2
packets cannot confirm either; the host's 60-second timeout then names the players that did
not confirm.

**Cost.** Nothing runs per tick. Per multiplayer load request, when at least one record is
declared and another player is present, each player reads its save's mod data archive once
and hashes the declared records. On an 8 MiB test save with a 256 KiB declared record this
took about 6 ms, against about 12 ms for the whole-file SHA-256 the agreement already takes
twice per player (`SavegameReadinessProtocolTests`: warm file cache, .NET Framework test host,
not the game's Mono runtime; not a frame-time measurement). Each declared record adds its
owner GUID, identifier and 32 bytes to the readiness packets, about 70 bytes for typical
names; the channel limit allows 64 records with names at their maximum length.

## Implementation

### Tracked origins and enrolled captures

The registry's internal provenance/capture protocol backs the public
[owner-bound enrollment](#opt-in-protected-save-participants). It relates an
existing mod record to a protected checkpoint; it does not import legacy stock.
Ordinary registrations retain their scope, migration, callback and unknown-record
behavior. Tracking alone activates no record schema or native hook. Public
critical enrollment explicitly joins the qualified writer, and a requested
NativeStorage join selects its protected container.

A tracked source binds the exact owner and process-lived registered handle before
the first archive load. Its origin describes the registry's private original
record, including explicit absence and a digest, rather than a consumer-supplied
copy. During `Prepared`, origin access requires that exact receiver, generation
and managed thread. Other registry callbacks mask that delivery. Completion also
requires the complete load/Post window to succeed; callback failure, a new load or
unload revokes it. An origin is metadata, not permission to inspect or mutate the
native world. See [origin lifetime](../src/Savegames/VersionedSavePreparedOrigin.cs).

An independently enrolled critical source must use `SaveGamesAndMaps`, declare
`SimulationRelevant`, own a known reserved marker entry and support the selected
cutover reader. Only that explicit enrollment reserves the entry: its capture
must not write the marker through `VersionedSaveWriter.Write`, which throws
`ArgumentException` for the reserved name. Unenrolled registrations acquire no
reserved names. The framework injects a copied marker, retains unknown entries
and reads confirmation from the private original envelope, before a legacy reader
or migration could synthesize it. An unreadable record, stale origin or marker
below its required original reader/version floor cannot authorize confirmation.

[The capture bundle](../src/Savegames/VersionedSaveCriticalCapture.cs) reserves
all participating slots before calling captures outside the registry lock. It
freezes bounded bytes once without marking any record `Written`. Actual Extender
transport serves those bytes once on the same save thread and marks delivery.
The bundle requires exact logical context: save-file flag, editor flag and ordinal
nullable file path. A synchronized save can legitimately have a null path; the
native destination or temporary file cannot stand in for this context. Duplicate
delivery, nesting, context mismatch, load/unload or a failed capture invalidates
the bundle. The save owner must verify every actual archive record against its
frozen bytes and statuses before committing, then dispose on the owning thread.
Preparation or `Written` alone is not an archive-commit certificate.

NativeStorage keeps its ordinary public registration at current reader 2, with no
writer enrollment. Public critical enrollment with `RequiresNativeStorage`, made
before its first storage registration or world, instead requests reader 3, the
known reserved marker and a process-lifetime writer cohort. A pending storage
requirement refuses saving until the actual join exists. Tracking alone adds no
native save demand.
Joined state-only sources do add demand, even without cargo or extended production.
The cohort uses ordered owner GUID/identifier/marker/floor identities, with arbitrary
GUIDs supported. A stored marker for an unavailable or different cohort is kept
passively as `MigrationFailed`; it cannot authorize a smaller replacement cohort.
A joined source cannot use an ordinary callback to omit its marker or downgrade.
If any original record is protected, all cohort members must have matching original
protected records; a missing or legacy partner makes the complete cohort passive.
NativeStorage delays first state publication until this entire matching load pass
has finished, even during reentrant Prepared/Loaded queries. Early unavailable
results do not permanently block a valid in-progress load.

NativeStorage's protected outer record is version 3 with minimum reader 3; its
`ledger`/`stores` payloads retain stores schema 1 or 2. Reader 2 reports `TooNew`
and retains the protected bytes. Legacy records remain readable, including a markerless future record version with
an older compatible minimum reader; unknown entries keep their existing retention.
Only a present marker or a minimum reader at the protection floor requires the
protected cohort contract. Explicit opt-in changes saving for that process:
state-only worlds require the qualified writer and owned context, and newly
protected saves cannot be consumed by the prior reader. The protected freeze is
SP/editor-only: real-multiplayer saving refuses even without cargo. Neither
synchronized save metadata nor removing the mode guard would supply the missing
native multiplayer snapshot/authority qualification. Ordinary unenrolled storage
retains its existing physical-stock MP path, version selection and unknown-entry
rules; no opt-in means no forced format bump. An ordinary reader-2 runtime sees
protected3 as `TooNew`/passive and cannot downgrade it to a legacy record.

The public enrollment above supplies participant coordination, not a lossless
consumer adapter. This is not legacy import or one-state cutover. Consumers must
retain private stock/cargo ledgers until every stock, production, payment,
recruitment and load/save authority has a verified lossless migration. In particular,
the old Byzantine MED2 handler does not read this protected framework record.

### Critical load-pass finalization and compatibility

Cold critical-marker reads and enrolled capture now wait until every `Loaded`
callback in the same load generation has returned. Closing the transport window
alone is insufficient. Previously, a callback could observe `windowOpen=false`
and read a critical marker or freeze critical records while that callback, or a
later callback, could still reject or replace the loaded state. Such operations
now refuse; an affected protected save must keep its previous destination and
wait for the complete matching load pass. A nested load or unload cannot finalize
the replaced generation. A failed source's existing kept-record policy remains.

This tightens behavior for internal owners attempting critical capture or marker
reads from `Loaded`; additive signatures and test counts do not make it behaviorally
compatible with that timing. Use the completed load pass. `Prepared` token
completion retains its earlier meaning, and exact active-`Prepared` marker reads
remain supported. This finalization is not native-world or writer ownership.

### Internal factory/frame implementation

The [owned factory correction](../src/ScriptExtenderFixes/SaveContext/SaveContextRuntime.cs)
can pass its exact normalized transport context to an explicitly opened,
thread-scoped [factory frame](../src/Savegames/VersionedSaveCriticalCapture.cs).
Preparation tokens bind the returned bundle to that callback's current preparation
scope; a previously frozen or foreign bundle cannot substitute. Standalone captures
keep their existing behavior. The frame retains its first failure even if the
Extender catches a callback exception.

The [critical writer](../src/Economy/WorkerProductionCriticalSaveRuntime.cs) retains
its expected frame before calling Original. Before replacement it requires the
expected and current thread-local identities to match, including null: a frame
appearing during an unframed call also refuses replacement. An active frame needs
once-delivered frozen records and exact completed-archive verification.

The existing native owner caches the pre-capture reader at startup. Behind its
demand, nesting, engine-monitor and fault guards, it queries owned logical context
before freezing state or opening a native file, then opens its own frame. All
source readiness is checked after storage freezing and before Original. The
actual ensuing factory must match the pinned flags and ordinal nullable path;
its preparation tag must produce the new bundle. The owned frame rejects factory
dispatch while participant freezing is incomplete; the owner completes preparation
only after readiness checks and refuses an observed/failed frame before Original
can open any file. Standalone internal frames retain their immediate contract.
Missing dispatch, mismatches,
failed or omitted records, and changed frame identity prevent replacement.
Frame release is attempted independently after every other cleanup; its failure
cannot mask the primary error or erase an already committed native result.

An explicit newly created editor world has no archive callback. Only NativeStorage's
existing exact `EditorCreated`/world/session boundary, after successful reconciliation
and the world recheck, may mint a distinct created-world origin. It has no original
bytes, digest, Prepared receiver or cold-marker authority. A complete created cohort
must share that world/session; mixing it with archive origins refuses. Load/unload
revokes it. Archive captures still require the full matching EndLoad pass; a missing
callback on a loaded world cannot become a blank-world token.

No additional native hook, consumer migration/import or cutover is enabled. These
managed/file fixtures do not prove native-route reachability in the game. Private
ledgers remain until the full cutover requirements above are met.

### Internal managed Trail producer metadata

The internal extension in the [existing factory owner](../src/ScriptExtenderFixes/SaveContext/SaveContextRuntime.cs)
provides expected metadata for one reviewed managed Trail producer. Its current
binding targets the reviewed game assembly and installed Script Extender **2.14.3**
(`SHCDESE` SHA-256 `5cb1c33be5463086505a17f5c48aaa357d733cfff7240e101c821f0e3c668498`).
It adds no public pre-capture or enrollment API. A different dependency needs its
own binding qualification; unchanged source alone does not qualify shipped IL.

`EditorDirector.SaveCustomTrailMap` first loads `mappath` in editor mode and,
only after successful loading, calls `EngineInterface.CreateTrailMission` with
output `trailpath` and encoded restart data. The expected logical context is
`IsSaveFile=false`, `IsMapEditorSave=false`, with the loaded **source-map path**.
That path follows the actual load chain's `Encoding.Unicode` round trip and
trailing-NUL trim, retaining embedded NULs. It is not a canonical filesystem path.
Neither the output Trail path nor a redirected native temporary path substitutes
for it; native `mapSave=0` alone does not identify this managed route.

The producer and wrapper share a caller-thread value-frame stack, restored in
`finally` around the real wrapper body and its existing engine monitor. A matching
producer is required; unidentified or nested wrapper calls mask outer metadata.
The existing synchronized policy retains priority and its `(true,false,null)`
context. Early queries are pure snapshots: they neither enter the factory nor
start critical capture. The first actual factory closes the query window. Every
factory preserves its existing normalized callback payload. When an internal
critical frame is active, a first transport that differs from the pinned Trail
values records failure in that frame, preserving its first error. A normal save reentered by an archive handler keeps its own
flags and path. Extender exception isolation does not cancel the native write.

This scope supplies managed invocation metadata only. It grants no native writer,
world-lifetime, capture, commit, import or cutover authority and does not
authenticate raw P/Invoke/native-export bypasses. The existing protected owner
still needs its exact invocation, world and verified transport/archive contracts
before any native replacement. The dormant enrollment and private-ledger limits
above remain authoritative. Normal/editor optional-provider work requires an
official upstream Script Extender addition; this extension supplies no private
third-party build or installation route.

### Critical-save cleanup outcomes

The existing protected writer remains the sole native save owner. A capture,
native-I/O or archive-verification failure before destination replacement leaves
the old destination and reports failure. Cleanup independently attempts native
path restoration and every selected production, workshop and storage release;
one cleanup exception cannot skip the others or leave the saving guard active.
The original transaction failure remains primary, with secondary cleanup faults
reported separately and bounded.

Successful atomic destination replacement is irreversible. A later cleanup fault
preserves the actual successful writer result and reports protection failure to
the existing world-safety owner only for the retained exact world/generation. It
cannot claim rollback, an unwritten file or fault a replacement world. This is an
intentional failure/result behavior correction, including for existing demanded
cargo/production saves; additive signatures alone do not establish compatibility.
The no-demand path continues directly to the original writer. Managed cleanup
fixtures do not establish live native path restoration or installed-game behavior.

### Record format

Each record is the complete `ModSaveDataAPI` entry for its identifier
([codec](../src/Savegames/VersionedSaveRecordCodec.cs)). All integers are little-endian.

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | Signature `APSV` |
| 4 | 2 | Envelope version of the writer (1) |
| 6 | 2 | Oldest envelope reader (1) |
| 8 | 2 | Header size (32) |
| 10 | 2 | Reserved, 0 |
| 12 | 4 | Record version |
| 16 | 4 | Minimum reader version |
| 20 | 4 | Entry count |
| 24 | 4 | Payload length |
| 28 | 4 | CRC-32 of bytes 0 to 27 and 32 to the end |

Entries follow in ordinal name order: a 2-byte name length, the UTF-8 name, a 4-byte data
length and the data. The same state always gives the same bytes. A future writer that changes
this layout or exceeds a version-1 limit must raise the oldest envelope reader, so version-1
readers report `TooNew` and keep the bytes rather than reporting `Corrupt`.

### Script Extender behaviour it relies on

At Script Extender v2.14.1 (`src/SHCDESE.BepInEx/`):

- One handler per identifier; a second registration returns false
  (`API/ModSaveDataAPI.cs:129-133`). Archive entry names are compared ignoring case
  (`:194`, `:248`).
- The save callback runs from the save-file write hook
  (`Detours/BulkMapLoaderDetours.cs:70-108` to `API/GameMapArchiveManagerAPI.cs:361-389`).
  A null or empty result writes nothing (`API/ModSaveDataAPI.cs:184-188`), and the archive
  loaded with the save stays in memory until the map unloads
  (`API/GameMapArchiveManagerAPI.cs:105-111`, `:324-342`), so its entry is carried into the
  next save.
- Loads: `OnLoadSave` and `OnLoadMap` load the archive in both their Pre and Post phase
  (`API/GameMapArchiveManagerAPI.cs:113-143`, raised at
  `Detours/BulkMapLoaderDetours.cs:178-185` and `:355-363`), and each load delivers every stored
  entry (`API/ModSaveDataAPI.cs:228-267`). A load without the entry delivers nothing
  (`:250-254`).
- `ModSaveDataAPI.InitializeSubscribers` (`:80-89`) has no caller, so its `onUnload` callback
  is never invoked.
- An exception from a load event subscriber would make the Extender's detour call the native
  loader again (`Detours/BulkMapLoaderDetours.cs:188-192`, `:367-371`); the binding catches
  everything.

### Load window

[The registry](../src/Savegames/VersionedSaveDataRegistry.cs) opens a window in its
`OnLoadSave`/`OnLoadMap` Pre handler and closes it in the Post handler. Whichever of
APIShared and the Extender's archive loader subscribed first, exactly one of the two
deliveries of a load falls inside that window, so only that one counts and the other is a
duplicate. The window opening drops the previous mission's kept entries; closing it decodes,
migrates and reports each registration once, including `NoRecord`. A map unload inside the
window is part of the load (the game unloads the old map while loading); one outside a load
drops the kept entries, as the Extender drops its archive. The subscriptions start with the
first registration, so the service is dormant until a mod uses it.

The savegame settings load entries (the mission lifecycle's save and `StartSave` hooks with
`SavegameLoadCoordinator`) wrap only save loads, from outside the native load, and agree on
settings before a multiplayer load is released. The Extender delivers stored entries only
inside its `OnLoadSave`/`OnLoadMap` notifications, which also cover map and editor loads, so
those notifications remain the window. The coordinator's confirmed multiplayer save state
classifies the source of an `OnLoadMap` load.

### Tests

`VersionedSaveDataTests` (Core.Tests) cover the round trip, the pinned byte layout, unknown
entries across a downgrade and upgrade, deletion of unwritten known entries, the migration
chain, failed migration, corrupt and too-new records, legacy import, `NoRecord` and mission
boundaries, both subscription orders, isolation and ordering of several mods, every limit on
writer and reader, scope and registration validation, the bounded set of simulation-relevant
declarations, and the single-player to multiplayer-save transition (earlier single-player
state and differing local records do not reject a shared save, the multiplayer load replaces
that state, a different declared record is named). `SavegameRecordComparisonTests`
(Core.Tests) cover every kind of difference, the explanation, peer list validation and the
host's acceptance rules. `VersionedSaveDataRuntimeTests` (APISharedTests) bind to the
installed Script Extender handler table and feed the binding the real event argument types;
`SavegameReadinessProtocolTests` cover the version-2 packet, protocol-1 recognition and reply,
later versions, malformed lists, and digests read from save files built with the Extender's
own archive class. The Prepared-origin and enrolled-capture partial fixtures
exercise receiver/thread/generation provenance, revocation, original-marker
confirmation, frozen-byte delivery, context mismatch, duplication and record
verification through the actual registry. `NativeStorageCargoSaveTests` also
exercise independent cleanup and primary/postcommit result preservation through
the managed cleanup entry. None of them runs the game; in-game checks are listed
under [Public limits](#public-limits).

## Required atomic simulation records

`VersionedSaveDataRegistration.RequireAtomicWrite` is additive and defaults to false.
Register opted-in records after native initialization and inspect
`VersionedSaveData.IsAtomicWriteAvailable`. Registration fails when the shared protected
writer is unavailable. Existing registrations retain their capture and retention behavior.

For an in-scope save, the existing native writer owner holds the actual engine monitor,
captures each required record once before creating the temporary native file, and reuses
the complete frozen record during the Extender archive callback. Capture must be brief:
copy consumer state under its own lock, in engine-monitor then consumer-lock order.
Capture must be read-only with respect to simulation and must not issue gameplay calls.
Never acquire the engine monitor while holding that consumer lock elsewhere. Callbacks
must not start another save or wait for another thread. The writer verifies complete native
I/O, archive delivery and exact saved bytes before atomically replacing the destination.
False/throwing captures, kept unreadable records, stale/omitted archive data and write
failures reject replacement; they cannot silently retain an earlier required snapshot.
Empty successful records are valid. No additional work runs per simulation tick.

If a save has any `RequireAtomicWrite` demand and an enrolled
[`CriticalSaves` cohort](#opt-in-protected-save-participants), all participant freezes
finish first; the cohort's capture callbacks then run once under the engine
monitor before native file creation, and their bytes are reused. This mixed opt-in
case moves those callbacks earlier than their ordinary lazy factory timing.
The actual `SaveContext` factory dispatch and once-only archive transport remain
required, with the retained logical context, archive generation, live world/epoch
and applicable required-registration set still matching. A prepared snapshot
does not itself prove transport or commitment. Without required-atomic demand,
enrolled records retain their factory-time capture; ordinary unenrolled records
retain their existing timing.

For early application, `NativeWorldRestoreEvents.TryRegister` shares the existing raw
restore owner. Register with a stable owner GUID and local ID after native initialization;
check `IsAvailable` and the returned reason. Participants run after successful raw native
restoration and existing internal participants, before AI restoration and the first native
simulation fold, under the actual engine monitor. Order is numeric then ordinal owner/ID.
Callbacks run outside registration locks, without replay, removal or implicit dispatch.
A false result or exception rejects native initialization without retrying the original.
`NativeWorldRestoreContext.Mission` identifies the current pending managed mission and may
be null; `InvocationId` is local diagnostic identity and must not be persisted or compared
across peers. Prepare detached bytes in `Prepared`, apply only matching candidates here,
and avoid applying them again or clearing them in later `Loaded`/mission-start callbacks.
These events do not synchronize or authorize gameplay. A missing old record may explicitly
restore empty metadata while leaving existing native state unchanged.
