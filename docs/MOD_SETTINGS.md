# Mod settings integration

Consumer workflows for mission settings, personal values, presets without SerpsModsHost,
settings search, load/save requesters and settings locked during a mission. General
registration, synchronization and lobby presets are summarized in the
[integration guide](THIRD_PARTY_GUIDE.md).

**Contents**

- [Mission settings preparation](#mission-settings-preparation)
- [Settings participant diagnostics](#settings-participant-diagnostics)
- [Presets without SerpsModsHost](#presets-without-serpsmodshost)
- [Locking an individual setting during a mission](#locking-an-individual-setting-during-a-mission)

## Mission settings preparation

`MissionModSettingsPreparation` belongs to APIShared.dll and consumes the schema-3
`MissionModSettingsEntry` from APIShared.Content.dll. Reference both assemblies;
ship neither with a consumer mod. Call on the Unity thread after settings registration,
without concurrent endpoint changes. Obtain compatible endpoints through participant
discovery and keep authorization, source selection and lifecycle in your integration.

- `CaptureEntry(endpoint, selectMode)` visits host metadata and asks for the existing
  `PublishedPresetValueMode`. ModDefault omits the property, Player records its name,
  Fixed reads and copies its value. Personal properties are not inspected. Normalize
  the assembled document through `MissionModSettingsJson` before writing it.
- `CaptureCurrentSnapshot(endpoint)` overlays current host values on a disabled
  mission snapshot, or the current working snapshot for an APIShared model with an
  application backend. It preserves other baseline entries and does not refresh the backend.
- `ValidateEntry(endpoint, entry)` converts known fixed overrides without live getters,
  mutation or application. Unknown properties are ignored; schema validation is separate.
- `MaterializeSource(workingEndpoint, entry)` starts from mod defaults, copies known
  Player values, then converts and serializes known Fixed values. Missing player values
  retain defaults. A null entry selects defaults without requesting a player snapshot.

Returned snapshots and captured mutable values are detached. Provider failures, getter
errors and conversion errors propagate without returning a partial result; no method
enters/exits a mission, applies a working snapshot, writes files or registers sources.
Null provider snapshots and selected null player buffers throw `InvalidDataException`.
Snapshot factories must still honor their existing non-mutating contracts.

Mission encoding is preserved: supported scalars/flat sequences remain JSON values;
other property types use the existing MessagePack/base64 marker. In particular float
and enum properties retain their legacy mission representation. This is intentionally
the mission codec's value domain, distinct from loose personal-preset JSON encoding.

For example, capture `var entry = MissionModSettingsPreparation.CaptureEntry(endpoint,
name => PublishedPresetValueMode.Player);`, put the entry under your mod GUID in a
`MissionModSettingsDocument`, then normalize/serialize it with `MissionModSettingsJson`.
Only the caller's authorized mission integration should later apply a prepared snapshot.

### Personal values and mission-independent Host settings

Requires APIShared 0.21.0 or later.

`[PresetLocal]` and `[SyncPerPlayer]` are always personal. Mission entry, source
reset, materialization, save loading, restart and exit never restore older values
for them. Accepted edits update the working settings immediately. The personal
`settings.msgpack` is written once edits pause for about half a second, and in any
case before mission settings are entered, at launch or restart preparation, at an
explicit preset save, at each mission start and end and at game exit; a failed write
is retried at the next of these points. Only a crash within that pause can lose the
last edit. A personal persistence backend still receives each edit immediately;
per-player synchronization still uses its existing authority and convergence rules.
A read-only mission locks only mission-dependent Host settings. Preset loading and
source reset retain their existing read-only mission gates.

Mod authors may additionally mark a `[SyncHostOnly]` property with
`[MissionIndependent]`. It stays host-owned and synchronized; only the host can edit
and persist it. Clients must never persist its received value. Mission sources
cannot capture, prescribe or restore it, even from older full snapshots. For dynamic
Host descriptors set `DynamicPresetSetting.MissionIndependent = true`; the default
is false. `PresetSettingDescriptor.MissionIndependent` reports the effective policy
and is automatically true for Player/Local values.

Bind marked Host controls to `CanEditMissionIndependentHostSettings`, outside any
container disabled by `CanEditHostSettings`. Continue guarding their setters with
`CanMutateSetting()`. The marker changes settings ownership only; it does not grant
additional gameplay-mode permissions. `GetHostProperties` still returns all Host
settings; use `GetMissionHostProperties` for mission authoring and savegame data.
ExtendedData excludes marked properties from its authoring selectors and exports.

Loading a mixed normal preset in Customize changes mission-dependent Host values
only in the draft, while Player/Local and marked Host values are personally saved.
An explicit reset to Mod defaults may reset those personal values too. Applying a
Map/Trail/Savegame source preserves them. Leaving Customize restores only the
previous mission-dependent Host values; personal preset modified status is retained.

An `IModSettingsApplicationBackend` exposing personal properties should also implement
`IModSettingsPersonalPersistenceBackend.PersistPersonalValues`. It receives a detached
subset of personally owned keys and must validate and persist them atomically without
applying mission values or discarding/replacing pending startup work. Failure leaves
its own store unchanged and propagates; APIShared restores rejected personal desired
values without reverting the mission Host draft. The interface is optional: without it APIShared
logs one warning and persists personal values only in the preset storage.
`RequiresRestart` still governs application, independently of personal persistence.
Legitimate personal writes rebase the existing restart provenance fingerprint while
preserving the prepared Host snapshot and source identity.

Player/Local persistence needs no opt-in. Normal Host defaults and the wire/storage
schemas are the same as for mission-dependent settings.

### Applying and leaving a mission context

Retain one `MissionModSettingsSession` for your coordinator. Pass your compatible
endpoint snapshot, a validated `MissionModSettingsDocument` and an explicit
`MissionModSettingsApplicationKind` to `Apply`. The returned dictionary reports
pruned unknown properties; the supplied document is pruned in place. Missing
mentioned providers block replacement before the previous context is left.
GUID identity is ordinal for participants, document entries and optional starting
snapshots, independent of the dictionaries' comparers. Differently cased GUIDs
remain distinct: an absent provider is rejected and another GUID's rules or
starting values cannot be substituted by a case-insensitive lookup.

| Kind | Baseline and resolution | Editing |
|---|---|---|
| Playable | Disabled mission baseline, personal Player values, then Fixed | Read-only |
| Preview | Playable, but skip participants absent after pruning | Read-only |
| Editable | Playable | Editable |
| Authoring | Captured working values, backend Player values, then Fixed | Editable |
| AuthoringTest | Authoring | Read-only |
| Materialized | Captured working values without resolving the document again | Read-only |

Authoring values are captured before leaving the old context. Personal Player
values resolve after exit; backend own values are refreshed first. Non-backend
authoring retains captured Player values. Optional starting snapshots replace the
captured baseline per participant. Matching unconsumed restart snapshots retain
precedence except in Materialized. Existing `ModSettingsApplication` restart/source
restoration is reused; the session creates no second registry, journal or transport.

Use `Editable` to open Customize with the same resolved values as `Playable`.
Keep that draft through Ready/Unready and apply `Materialized` only at the actual
start boundary. Only mission-dependent Host values belong to that temporary draft.
Player and Local values remain personal throughout preparation, launch and exit. A consumer which reopens a rejected start must retain a detached draft and
re-enter it through `Authoring` with starting snapshots and no original overrides;
applying the original document again would discard edits. Source documents and
personal files are not modified by these transitions. Trail-maker authoring can
continue to use `Authoring` for its current-values baseline.

APIShared models apply internal snapshots under the Extender's `InitialLoad` origin.
Property/UI notifications remain enabled, but intermediate values are neither sent
nor persisted. Inclusion and authority are captured before the first setter; after
successful application, eligible host properties use the existing Extender sender
and personal values use the existing per-player convergence. Snapshot callbacks
still run after application, and their failures propagate to the owning session.
Reflected setters are applied independently: a setter that rejects its value receives
the code default, and the other settings are applied regardless. The default is
recorded in the working copy and a loaded preset then counts as modified; a setting
that rejects even its default is reported and keeps its requested value for the next
load. Leaving a mission or restoring personal host settings rethrows such a failure
after the other values, so the owner keeps its context and retries; the third
attempt completes and reports the settings that kept their current values. Dynamic
providers and application backends keep their complete-configuration validation.
Personal persistence uses the owned working snapshot, never a mixture of current
mission values and received host values. On exit a confirmed active client lobby
retains host values until lobby departure; stale host flags left by local Coop do
not prevent restoration. Personal restoration does not publish host values.

The process-lifetime managed Extender receive hook copies packet data and transport
sender on the raw callback and posts to the validated Unity FIFO also used by
`Network.TryPostPresentation`. Application rechecks lobby/session identity and calls
the original Extender receiver, preserving its sender checks and join protocol.
Stale queued work is discarded: leaving the lobby or ending/replacing a mission
invalidates it, while starting a mission from the same lobby keeps it current.
Inside a multiplayer start (new game or save) the queued settings are applied on the
Unity thread after Vanilla received its last lobby packets and before the map loads,
as the Extender's synchronous receiver would; their dispatcher turns then do nothing.
Missing dispatch never falls back to the network thread. Registration requires the signature-checked hook to be available. This
changes incoming Extender settings delivery to queued Unity execution, including
settings registered directly with the Extender; outbound formats and direct
registration ownership remain unchanged.

`beforeApply` is a required preparation callback if supplied: it runs once after
all snapshots are ready, before any new endpoint enters. A failure blocks entry.
Do not call Apply/Exit/ConfirmStarted recursively on the same session. Conversions
are prevalidated before exit; later preparation/application failures clean up to
personal state, without recreating the previous temporary mission context.
Cleanup includes a partially failed Enter, attempts every owned endpoint and uses
the exact instances originally entered. Failed cleanup owners remain for retry;
errors are aggregated rather than hidden. Retry a failed Exit before applying a new
context; values from an incompletely cleaned context are not captured as a new draft. APIShared models retain their active
context when personal restoration throws, making a later Exit retry effective.
Providers must tolerate Exit after a
partially failed Enter. An incomplete cleanup is not a successful replacement.

On a matching actual successful mission lifecycle notification, call
`session.ConfirmStarted(context.SessionId)`. Apply or a dialog callback returning
is not evidence of mission start. Bind a new lifecycle ID for an accepted restart.
On authoritative end/cancellation call `session.Exit()`; retain across an explicitly
accepted replacement if your content lifecycle requires it. The caller still owns
`ModSettingsApplication.EnterContext`, launch preparation and global context exit.
The session neither subscribes to game events nor uninstalls hooks.

`MissionModSettingsCreatorRules.Capture(endpoint, entry, onError)` creates the
existing `TrailCreatorRule` save contract for host properties. Failed fixed values
retain the preceding default/Player rule, with the existing 1 MiB property limit;
optional diagnostics are isolated. ExtendedData supplies which active document is
authoritative. Savegame materialization uses the same rule implementation while
preserving its existing strict-trail/Customize/lock semantics and wire format.

## Settings participant diagnostics

Call `SavegameModSettings.GetParticipantDiagnostics()` on the Unity thread after
LibraryLoaded and settings registration. The read-only result contains
`ModSettingsParticipantInfo` entries ordered by ordinal plugin GUID: display name,
`ModSettingsParticipantStatus`, diagnostic, accepted endpoint and host properties.
Metadata is copied; endpoints and PropertyInfo targets remain live objects.
Use `IsCompatible`/`Status` for decisions; a provider's exception message can be empty.
Re-query when registrations or schemas change. This is an inspection of the existing
lobby/configuration registries; it does not register another provider or apply settings.

The default query uses `ModSettingsParticipantPurpose.AutomaticSavegame`.
For Map/Trail authoring, pass `ModSettingsParticipantPurpose.MissionAuthoring` to
the overload. A plugin's savegame-only exclusion remains effective for automatic
saves and does not exclude it from authoring. For such excluded endpoints,
authoring retains its historical current-value read/serialization checks before
checking the disabled snapshot, unless an explicit dynamic endpoint already represents
that GUID. That endpoint keeps its historical precedence without probing the shadowed
panel. Ordinary accepted endpoints acquire no new
current-value probes. Unknown purposes throw before accessing discovery sources.

The same discovery drives `GetCompatibleParticipants()` and automatic savegame
preparation. Lobby endpoints must expose persistent host properties and a complete
disabled mission snapshot. Inspection calls that snapshot factory once per eligible
lobby endpoint; factories must not apply settings. The automatic query does not
additionally probe current host values. Missing GUIDs, duplicate panels, plugin exclusions, missing typed
endpoints, invalid property schemas and invalid snapshots have separate statuses.
Reflection, factory and decode exceptions reject that participant and leave other
participants available. A failure of the discovery source itself still propagates.

Explicitly registered dynamic configuration endpoints keep their existing contract,
independent of their optional UI panel. They are identified by `IsConfigurationEndpoint`.
Rejected UI panels may therefore coexist with an accepted dynamic endpoint for one
GUID. Two accepted providers claiming one GUID produce `ConflictingProvider` with no
usable endpoint. The diagnostic query reports this conflict; automatic operations and
`GetCompatibleParticipants()` continue to throw rather than choose an arbitrary provider.

ExtendedData uses the MissionAuthoring outcomes for its existing Map/Trail compatibility UI. Its
legacy explicit opt-out marker and content-specific panel selection remain local.
The public Core compatibility helpers stay available.

## Presets without SerpsModsHost

Derive your settings from `APIShared.ModSettings.PresetLobbyModSettingsViewModel` and register through `LobbyModSettingsPresetRegistration.Register`. The Script Extender displays the mod tab; APIShared owns discovery, personal preset files, persistence and lobby convergence. SerpsModsHost and ExtendedData are optional consumers, not prerequisites.

Classify saved properties with Script Extender `[SyncHostOnly]` or `[SyncPerPlayer]`, or APIShared `[PresetLocal]`. Each per-player property needs a public compatible `<Name>Data` array for slots 0..8. Keep setters behind `CanMutateSetting()` and call `OnPropertyChanged` after actual changes. Configure additional per-player rules through `ConfigurePerPlayerLobbySettings`; do not implement another roster poller.

For registered APIShared participants, personal values may change before a stable local slot exists or while leaving a lobby. The scalar, UI notifications and personal persistence remain local; only the outbound per-player broadcast waits. After the roster and transport mapping agree, the existing coordinator publishes the latest full personal snapshot into the resolved slot. No temporary value is assigned to slot 1.

Host-only changes also wait for a current lobby observation and a matching local transport slot. Only property names are retained: each pending property is published once with its current value through the original Extender sender. Host publication does not wait for other players' identities or personal reports. This also applies to participants without per-player settings. Publication rechecks host authority and lobby identity; leaving, replacing the lobby or starting a mission discards that lobby's pending work. Changes without an active lobby stay local, and the ordinary join sync supplies current host values to new peers. Settings registered outside APIShared retain their original transport behavior. The existing lobby observer reports a local player ID of zero until its roster and transport mappings agree, so a later transport resolution wakes waiting publishers even if the roster itself did not change. Replacing the actual lobby instance also notifies observers, even when the public snapshot values remain equal.

Automatic save restoration prepares host settings before loading. Multiplayer saves
are identified by their actual load entry, including a host playing alone. Existing
Extender property sync and application-backend transports carry the values; APIShared
checks current human peers' fingerprints before releasing the host load request.
The save provenance comes from the authenticated host, so client save copies need
not contain APIShared metadata. Personal properties and presets remain local.
Coop content does not imply multiplayer: the mission entry determines transport,
and the central mode reconciliation validates saved provenance against native data.
Load preparation times out after 60 seconds; missing or changed participants cannot
silently release a stale request. Setters still notify runtimes normally. Do not
interpret a submitted packet as an acknowledgement of a complete settings snapshot.
The same agreement compares each player's stored copy of versioned save records declared
simulation-relevant and blocks the load with the mod and record named when they differ; see
[multiplayer record comparison](SAVE_DATA.md#multiplayer-record-comparison).

The reset selector may also expose `ModSettingsWorkingSourceKind.Personal` with
`ModSettingsWorkingSourceRegistry.PersonalSettingsId` (`personal-settings`) while
Map/Trail sources are available. This copies the participant's personal snapshot
into the editable mission-dependent Host working copy; it neither exits that context
nor writes personal settings or preset files. The mission source remains available
for a later reset. Host/client ownership and read-only mission locks still apply.
A provider offering this source must capture detached personal snapshots through
`System_CreatePlayerMissionPresetSnapshot()` before applying any target, preserve
authoring rules, and commit backend application within `Apply`/`ApplyMany` (including
restart staging). The common UI deliberately skips its usual pre-refresh and second
commit for this source so a failed reset can restore the original mission draft.

Bind host/client UI sections to `CanEditHostSettings` and `CanEditClientSettings`. Bind common preset/source actions to the public `System_*` properties and commands; do not replace them with a second persistence engine. Use nonempty tooltips with `ToolTipService.ShowDuration="60000"`. Settings-search metadata is optional when the control title/tooltip layout is unambiguous.

For an activation hint, bind the host/client content containers' `Opacity` to
`HostSettingsOpacity` / `ClientSettingsOpacity`. They return `0.65f` while the
corresponding discovered activation switch is off, otherwise `1f`; a scope without
an activation switch stays fully opaque. Keep activation switches, role headings,
search and preset actions outside those containers. These Unity-thread presentation
bindings notify after activation changes and snapshot application. They do not
disable editing or change stored values: keep the existing editing-rights bindings
and allow users to prepare values while a mod is inactive.

The example includes a minimal host/local settings tab. Use localized labels and override `ResolveSettingsUiText` for translated common actions in a production mod. APIShared supplies English fallbacks. Persistence uses the documented file formats and `__Serp*` keys with arbitrary mod GUIDs.

### Mission-start settings log

APIShared automatically logs published preset endpoints, including local-only mods
and external working-copy integrations. No additional consumer registration is
needed. Successful new maps, save restores, explicit restarts and editor starts
produce one Info block per process-local mission session. Failed attempts do not
produce a block, and replayed start notifications do not duplicate it. This output
is independent of the optional diagnostics switch and uses the normal BepInEx
logger with millisecond timestamps; the disk listener must include Info.

After the BepInEx/timestamp prefix, parse the exact `MODSETTINGS_BEGIN ` or
`MODSETTINGS ` marker followed by one complete compact JSON object. Both objects
contain integer `schema: 1` and `session`; session IDs are process-local, so combine
them with the enclosing BepInEx process-start boundary when reading appended logs.

The header contains `start` (`NewGame`, `LoadedSave`, `EditorCreated`, `EditorLoaded`),
boolean `restart`, `mode` and `variant` (the names of the existing mission-mode
enums), `role` (`host`, `client`, `local`, `unknown`), nullable `localPlayer` and
the number of endpoint rows in `mods`. Unknown mode/authority is not guessed.
For example, omitting the normal logger prefix:

```text
MODSETTINGS_BEGIN {"schema":1,"session":12,"start":"NewGame","restart":false,"mode":"CustomGame","variant":"Standard","role":"local","localPlayer":1,"mods":1}
MODSETTINGS {"schema":1,"session":12,"mod":"Foreign.Author.Mod","host":{"EnableMod":false},"player":{"EnableClientFeatures":true,"Preference":2},"local":{"ShowMarkers":true},"disabled":["host"]}
```

Each endpoint row identifies the target plugin by its exact GUID in `mod`.
The optional `context` object describes the personal or temporary Map/Trail/Save
working context, its existing source identity, selected preset, modified/editable
state and persistence scope. Its storage path, successful/failed write counters,
last outcome and pending-migration flag describe actual personal-file publication;
temporary changes do not count as personal writes. Context display remains visible
when a different preset or reset source is loaded into an editable mission copy.
These fields reuse controller state and perform no additional file or world reads.
`host`, `player` and `local` group the existing persistent setting scopes and use
exact, case-sensitive code keys, ordered ordinally. Mods are ordered by GUID.
All current values, including defaults and full arrays, are included; UI helpers,
commands and companion arrays are not settings. Player values come from this
client's own properties, never other players' companion slots. Configuration
backends are read through `ReadActiveValues()`, never desired or pending values.
The read-only capture runs on the Unity thread and does not alter presets,
personal files, authority or synchronization.

The controller's existing host/client activation switches determine shortening.
An inactive group retains only its activation property, if that property belongs
to the group. `disabled` explicitly lists shortened groups; the client switch
shortens both `player` and `local`. A shortened local group can therefore be `{}`
when its switch belongs to `player`. Groups without settings are absent, and
groups without a discovered activation switch are not assumed disabled.

Values use the existing JSON codec: numbers are culture-independent, enums use
their underlying numeric value, booleans remain booleans, strings are escaped,
and null remains null. Lines are not truncated. Parsers should accept additional
metadata fields within this schema and distinguish an omitted value from null.
Failed reads/encodings omit the affected value and add an `errors` array of
objects with `scope`, `code: "read_failed"` and, for individual settings, `key`.
A whole-endpoint or active-backend failure uses `scope: "mod"` without `key`;
no desired-value fallback is used. Technical exceptions are logged separately
as warnings. One endpoint failure does not suppress the other endpoint rows.

This records current configuration, not proof that each configured feature runs
in the current mode. Additional consumer-owned feature conditions are not inferred.

### Explicit settings-search support

Global search includes only tabs that opted in through `ModSettingsSearchCatalog`.
`LobbyModSettingsPresetRegistration.Register(...)` already registers the resolved
XAML source and the preset ViewModel's local exact-key navigation. Existing preset
consumers need no second registry or export method.

A mod with its own settings ViewModel can opt in independently of presets and
SerpsModsHost. On the Unity thread, resolve the same XAML filename used for its tab,
then call:

```csharp
ModSettingsSearchCatalog.RegisterSource(settings, absoluteXamlPath, log, modName);
```

An optional fifth argument is `Func<string, string, bool>`: it receives an exact
entry key and its current localized title. Return true after applying a local
filter. Without a callback, or if it fails, the host simply opens the tab. The
callback must not traverse, realize, measure or focus other views. See the
[standalone example](../examples/ThirdPartyMod/SettingsSearchExample.cs).

Register the same ViewModel object as the tab, preferably before publishing the
tab. Metadata comes exclusively from the XAML document and public readable binding
properties; it never loads a Noesis view. Titles, tooltips, sections, exclusion and
ItemsSource-backed templates use the existing `ModSettingsSearch` attached
metadata. Item collections are snapshots, not live UI subscriptions.
Re-register the source to refresh changed language text or dynamic items. This
replaces the source and optional callback and invalidates its cached entries.

Configured-value filtering is independent of text metadata and parent activation.
`ModSettingsSearchCatalog.GetState(model, key)` reads current public checkbox/slider
bindings on the Unity thread. `ModSettingsSearchState` is a flags enum: `Unknown`,
`Active`, `Inactive`; a logical row with mixed values contains both flags and matches
both status filters. Unsupported bindings, non-numeric values and failed value
getters remain unknown without invalidating the catalog. Associated text editors
do not add a second value. Recursive reads of the same state key return unknown
at the recursive boundary. The text-only APIs and converter inputs keep their contracts.

Checkboxes use their checked state. Sliders default to inactive at exactly -1 or 0.
Declare context-specific neutral values directly on a slider, for example
`shared:ModSettingsSearch.InactiveValues="1"` for a multiplier or `"100"` for a
percentage. This literal metadata **replaces** the default rule: with `"1"`, zero
is active. Multiple values use semicolons (`"-1;100"`); inclusive numeric ranges
are supported (`"0..1"`). Numbers use invariant decimal notation. Invalid metadata
produces unknown state. Declare neutral semantics explicitly; neither labels nor
initial/default values are used to guess them.


TextBoxes can opt in to numeric state filtering with APIShared 0.16.1:
`shared:ModSettingsSearch.StateValue="{Binding Amount}"` binds the authoritative
numeric property, independently of `Text="{Binding AmountValueText}"`. Put
`shared:ModSettingsSearch.InactiveValues="0"` on the same TextBox when every
nonzero amount, including -1, is active. The catalog reads and observes the
numeric model path on the Unity thread; it never parses localized display text.
Converters, alternate binding sources, missing paths and nonnumeric values remain
unknown. TextBoxes without StateValue retain their prior unknown state. Checkbox/slider
behavior and defaults need no migration.

The catalog starts observing value paths when state is queried. `StateChanged`
publishes the registered root model on the Unity thread, including changes to
observed nested objects; foreign-thread notifications use APIShared's existing
Unity dispatcher and are drained in a coalesced callback. Defaults resets, preset
snapshots and configuration commits internally batch derived search updates per
model: the outer operation publishes one final `StateChanged` if an observed value
changed, including when an operation exits with an exception. Individual edits
still publish synchronously on the Unity thread. Original setting notifications,
storage and network publication are not suppressed. Explicit `GetState` reads
remain live even inside a batch; presentation-only reuse ends with its notification.
Leaf changes do not rebuild the observation graph. Replaced intermediate objects
rewire only their affected paths. Local status presentation does no value refresh
for All; source replacement and text filtering retain their normal behavior.
Source replacement detaches the old managed observers. Dynamic
item collections still require re-registration; item metadata remains a snapshot.
Custom hosts can combine `ModSettingsSearchStateMatcher.IsMatch(filter, state)`
with the existing text matcher. Preset pages expose session-only
`System_ModSettingsSearchStateFilter` and selector index/labels. A status-aware
local converter prefixes the old inputs with root model, status filter and revision,
provided by the inherited `SourceModel`, `StateFilter`, `StateRevision` properties.
Exact navigation and the clear command reset the local status filter to All.

`IsRegistered(model)` checks support without reading consumer properties.
`TryGetEntries(model, out entries)` returns false only for an unregistered model;
a registered empty or failed catalog returns true with zero entries. Snapshots
are read-only. A load/getter failure is logged once per registration and cached
until replacement. Hosts must never fall back to a native UI-tree search.
`TryNavigate(model, key)` rejects stale keys and isolates callback exceptions,
logging the first navigation failure per registration. It uses the current title.
`SourceChanged` runs synchronously after registration/replacement and isolates each
listener's failure. Replacing the same source from its own notification is rejected
before mutation to prevent recursive event loops. Recursive `TryNavigate` calls for
the same model return false, including when a callback replaces its source. All these operations are Unity-thread operations; registration
retains the ViewModel and callback for process lifetime.

The original `ModSettingsSearch.RegisterSource(...)` remains a compatibility
facade. Its legacy `Export(model, view)` method retains its existing realized-view
fallback for older direct callers; the global host uses only the new catalog API
and never calls that method. Exact keys remain ordinal. Free-text matching repairs
malformed UTF-16 and Unicode noncharacters rejected by CLR normalization for comparison, retaining
ordinary Unicode, valid surrogate pairs and diacritic folding.

### Personal files and previous installations

APIShared preset participants store personal files in the platform-resolved Documents
folder under `SHCDE/APIShared/<Mod-GUID>/`: `settings.msgpack` contains the working
settings and `Presets/preset_<id>.json` contains personal presets. The same user and
mod GUID share this data across game installations. Ordinary Extender settings
registrations without APIShared presets keep their existing storage behavior.

The previous plugin-local `LobbyModSettings/Presets/Override/<Mod-GUID>` directory
remains readable. A valid Documents preset takes precedence over an old personal
preset with the same stable identity; duplicate IDs within either directory remain
invalid. Saving, importing or explicitly overwriting a personal preset writes only
to Documents and preserves old JSON files. Explicit deletion removes personal copies
of the selected identity from both directories and reports partial failures.
Bundled and external provider presets remain read-only in their original locations.

When no Documents working file exists, APIShared loads the previous plugin-local
`.msgpack` and writes the validated/migrated state to Documents, retaining the original.
A present but corrupt or inaccessible new file does not fall back to stale installation
settings. Failed migrations retain their source for retry. APIShared restores initial
values before UI binding and remains the sole writer for its participants. External
configuration providers retain ownership of their own configuration files.

Missing or unwritable Documents storage is reported rather than silently redirecting
writes to the plugin directory. The shorter layout reduces installation-path dependence;
it does not provide general long-path support for the game or its preset providers.
Documents resolution and persistence need validation in the actual Proton prefix.

Restart preparations use a separate installation-owned file at
`SHCDE/APIShared/Inst/<installation-id>/RestartPreparation.json` below the same
resolved Documents folder. The ID is the first 16 lowercase hexadecimal characters
of SHA-256 over the absolute configuration directory, with trailing separators
removed, `/` changed to `\`, and invariant uppercase applied. A durable migration
receipt verifies the complete normalized identity, so the short ID cannot silently
select another installation's preparation. Changing plugin folders or APIShared
versions does not change this identity; moving the configuration directory does.

The old `BepInEx/config/APIShared/RestartPreparation.json` is imported once when no
new file or completed migration receipt exists. Migration atomically publishes and
verifies the original bytes before removing the unchanged old file. Invalid journal
content remains available at the new location for the existing validation and
explicit discard flow; it is not converted into defaults. Failed publication retains
the old file for retry. A failed old-file cleanup is reported and does not restore
legacy reads. The receipt survives consuming or discarding the journal, preventing
stale old preparations from reappearing. Journal format, content fingerprints and
mission ownership checks are unchanged; older APIShared versions do not read the
new location and no parallel legacy copy is maintained.

Unresolved journal storage remains visible in the restart-preparation UI with an
error description; the visibility getter does not turn the error into permission
to launch or modify data. Explicit discard can reset an oversized journal while
preserving an oversized Documents original in a separate file, or retaining an
oversized legacy source unchanged. This recovery does not bypass installation
identity, receipt validation, access errors or failed migration publication.

### External settings and application backends

Settings adapters can override `DynamicSettingsProvider` with an
`IDynamicPresetSettingsProvider` when settings are described at runtime. Descriptors
use stable keys, exact CLR types and non-null code defaults; dynamic scopes are
Host or Local. The provider validates and replaces complete working snapshots.
An optional `IModSettingsApplicationBackend` separates desired values from applied
values and identifies settings requiring restart. Use the public
[dynamic/provider contracts](../src/ModSettings/Presets/DynamicPresetSettings.cs)
and [application coordinator](../src/ModSettings/ModSettingsApplication.cs);
do not treat every edited working value as an already applied runtime setting.

For a separately registered foreign page, use
`LobbyModSettingsPresetRegistration.AttachExternalWorkingCopy` to attach presets
without a second network registration, or `RegisterExternalWorkingCopy` for a
participant without a page. `PrepareExternalWorkingCopy` creates an unpublished
candidate; activate it only when the surrounding integration is ready. These
entry points are Unity-thread operations. Optional consumer adapters and their
activation rules belong to the integrating mod; presets do not require ExtendedData.

### Load/save requester integration

APIShared owns the savegame checkbox, including a persistent local preference
(initially off). Save selection, forced legacy/saved-only states and cancellation
do not change that preference. Missing/unusable UI means current supported settings
for automatic loads, while saved mission permissions and fixed creator rules remain
authoritative. Explicit `SavegameModSettings.PrepareLoad` and
`PrepareLoadWithRestartCheck` Boolean calls keep their existing meaning.

Use `APIShared.Savegames.SaveLoadDialogEvents.Register` on the Unity thread with
your GUID, a stable owner-local registration ID and `SaveLoadDialogCallbacks`.
Registration lasts until process exit and applies only to future requests.
Consumers requiring this service check `SaveLoadDialogEvents.IsAvailable` after
LibraryLoaded before publishing their runtime. The dialog API requires APIShared 0.12.1 or later.
Ordering is ascending numeric order, then owner GUID and registration ID ordinal. Delegates
are copied; each contribution receives private per-request context state.

`Opened` initializes a contribution after the game opened its view. `Validate`
must only check readiness: false or an exception prevents every preparation and
the original action. `Prepare` runs after all gates passed. `AttemptFinished`
runs in reverse order even for a partially throwing preparation. Release temporary
write decisions there, not a successfully transferred mission. `Closed` observes
confirmation, cancellation, replacement or failure once, after attempt cleanup;
its exceptions are isolated. A requester opened synchronously by a callback is
deferred until the outer game operation has returned, including delayed editor
overwrite execution and Vanilla's filename bookkeeping.
Exceptions from the original game callback propagate after cleanup, preserving its failure path.
Opening errors block that contribution's confirmation. Confirmation means the game
callback returned, not that the mission initialized successfully. Use the mission
lifecycle for the latter. A rejected confirmation keeps its request/selection and
restores the requester view; it does not invoke the original action.

Do not install another requester hook. Vanilla hides the requester before invoking
its confirmation callback; APIShared captures checkbox availability before that hide.
An installed ExtendedData plugin must advertise the shared dialog integration with
`public const bool UsesApiSharedSaveLoadDialog = true` on its plugin type. APIShared
rejects a conflicting requester owner before installing its dialog hooks.
The build installer's consumer preflight also rejects an incompatible ExtendedData
plugin before copying the APIShared package. It checks the plugin GUID and its public
constant broker marker in assemblies below `BepInEx/plugins`, including renamed
files. It does not discover unopened Workshop archives or prove a working UI;
the runtime check remains necessary for those loading paths and manual installs.
The dialog uses the save archive, packet protocol, local personal presets and
host-load agreement of the savegame service. No ExtendedData installation is required for this UI.

## Renamed and converted settings

Saves, Trail author rules, Map and Trail mission settings, the personal settings file and
preset files store settings by property name, in the form of the mod version that wrote them. Declare a change so
that this stored data keeps working.

A renamed property names its former stored names, newest first:

```csharp
[SyncHostOnly, ModSettingRenamedFrom("GoldReward", "Reward")]
public int KillRewardGold { get; set; }
```

APIShared then moves a value found under a former name to the current property wherever
it reads stored settings of that class by name: saved host settings and author rules in a
save, the Player and fixed settings of a Map or Trail mission document, the personal
settings file and every bundled, external or personal preset. Stored
data that already contains the current name keeps that value. The former name disappears
from the data, and the next write uses the current name only. A former name that is still
a property of the class, or that two properties claim, is ignored and logged once as
`MOD_SETTING_RENAME_IGNORED`. The stored value must still deserialize as the property type.

A change of meaning or type needs code. Implement `IModSettingsSnapshotMigration` on the
settings class: before a save is loaded, APIShared passes the prepared snapshot (after the
renames) to `System_MigrateMissionSnapshot`, applies the returned snapshot and verifies the
applied host values against it. Without that step a setter that converts a retired property
looks like a setter that ignored its value, and the load is refused with "Host setter did
not apply the prepared value". The converter must not change settings.

A mod that stores settings of such a class in its own format calls
`ModSettingsMigration.MigrateStoredSnapshot(settings, stored)` on the raw stored values,
before merging them with defaults. Renames cannot be applied to a merged snapshot, because
it no longer tells a stored value from a default.

## Locking an individual setting during a mission

Requires APIShared 0.25.0 or later.

Consumers using `PresetLobbyModSettingsViewModel` can exempt genuine mission
entry/exit and deferred personal-host restoration from a local mission edit lock
with the protected `IsApplyingMissionSettingsSnapshot` property. Local working
snapshots, published presets and reset actions do not enter that scope, even
though the general `IsApplyingSettingsSnapshot` flag and the Extender's
`InitialLoad` origin also cover those local actions. Keep the normal host/mutation
gate in the setter; this synchronous scope does not establish network authority.
Authenticated incoming host updates use the existing network authorization path.

