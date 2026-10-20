# Third-party integration guide

## Game-independent content

For JSON documents keyed by mod GUID, reference `APIShared.Content.dll` (target:
.NET Standard 2.0). Runtime mods use `Private=false`, declare the APIShared plugin
dependency, and install the complete central package. Do not bundle an additional
Content DLL with a consumer. Offline tools may deploy this game-independent library
with their own executable; they do not need APIShared.dll or the game.

Call `APIShared.ModData.ModDataNamespaceReader.Read(json, modGuid, source)`.
The result exposes `Status`, `Success`, canonical `ModGuid`, caller-supplied `Source`,
`Data`, normalized `Json` and `Diagnostic`. Data and nested objects/arrays are read-only
independent snapshots. GUID matching ignores case; duplicate GUID casing, blank keys
and non-object namespaces invalidate the entire document, including unrequested entries.
Missing namespaces and invalid requests have separate statuses. BOM text is accepted;
the underlying parser limits JSON nesting to 64 levels. There is no generic byte limit:
source-specific limits and authoritative host selection remain the caller's responsibility.
The parser does not read files or apply gameplay settings.

ExtendedData keeps its previous result types in ExtendedData.Core.dll and adapts them
to this implementation. Its Lord sidecar/snapshot limits and host-data rules remain intact.

For bounded sidecars, `ModDataDocumentJson` centralizes document/namespace limits
and strict caller-owned stream decoding. Runtime `ModDataAccess` reads current map
data or explicitly local Lord sidecars independently of ExtendedData, and exposes
an optional confirmed selected-Lord provider. See [mod data access](MOD_DATA.md)
for provenance, lifecycle and provider contracts; a local read is not host confirmation.

For schema-3 mission settings, use `APIShared.ModSettings.MissionModSettingsDocument`,
`MissionModSettingsEntry` and `MissionModSettingsJson` from the same Content assembly.
The codec parses JSON text or a decoded dictionary (`ParseDocument`), validates and
serializes deterministic CRLF JSON. Text parsing and serialization enforce 8 MiB
of UTF-8; decoded-object callers own the enclosing document size limit. Each mod
can supply up to 16384 combined player names and fixed overrides, counted before
deduplication. Values are Boolean/string/integer/decimal/double scalars or flat
scalar sequences; null, float, objects and nested sequences are rejected.

These are mutable authoring documents. Missing mods/settings use mod-defined
defaults; `PlayerSettings` selects player values and `Overrides` fixes author values.
`NormalizeAndValidate` and `Serialize` mutate entries, sort names ordinally, deduplicate
player names, reject player/fixed conflicts, remove empty entries and replace `Mods`.
If a later entry fails, already visited entries can remain normalized. `RemoveUnknownSettings`
preserves dictionary and retained entry identities and the order of retained player names.
The codec does not register a provider, select files or apply settings. Calls are
synchronous; callers own their documents and must coordinate concurrent access.

ExtendedData's previous `ModSettingsDefinition`, `ModSettingsEntry` and `ModSettingsJson`
remain in ExtendedData.Core.dll as compatible adapters, including observable mutations
and entry aliases. Its filesystem `Read`/`WriteAtomic`, `.modtrail.json` selection,
mission packages and Lord/host authority remain in ExtendedData. Shared application
and cleanup live in APIShared; see the mission application section below.
New consumers can use Content directly without loading either runtime assembly:

```csharp
using APIShared.ModSettings;

var document = MissionModSettingsDocument.CreateModDefaults();
document.Mods["Your.Mod.Guid"] = new MissionModSettingsEntry {
    PlayerSettings = new[] { "PlayerChoice" }
};
string json = MissionModSettingsJson.Serialize(document);
```

The development [dependency bootstrap](DEPENDENCY_BOOTSTRAP.md) can correct false
missing-provider warnings for selected nested plugins. Consumers keep their normal
dependency declarations; no registration or bundled APIShared copy is required.
This development package is not yet approved for installation or release.

APIShared optionally loads after Fixes for its audited
[wheat-sale startup correction](FIXES_COMPATIBILITY.md). No consumer registration
is required. Check its explicit outcome log; APIShared being present does not
prove that an unknown Fixes binary was corrected.

Unused observation services do not scan or prepare events. Register player-defeat
observers before the first simulation tick if early transitions matter: the first
tick after the first subscriber establishes the baseline and never replays earlier
losses. Lobby observers receive a current capture on first registration and the
known snapshot on later registrations. Marked selection observers and successful
`TryCapture` calls establish process-lived tracking demand.

Keep mod-specific diagnostic analysis and logging in your mod. `UnitAccess.TryGetById`
returns `UnitLookupFailure` for this purpose and does not log rejected lookups.
Generic observation hooks remain shared; they do not create payloads without
interested consumers. Registering an observer is explicit demand for observation.

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
Previously such a failure could clear the flag before restoration had completed.
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
The old public Core compatibility helpers remain available for existing callers.

## Diagnostic opt-in and observer demand

Search `API_SHARED_DIAGNOSTICS` in the sources to find optional tracing and its
activation boundaries. APIShared's local `BepInEx/config/APIShared_Serp.cfg` has
`[Diagnostics] Enabled = false` by default. Set it to `true` and restart the game
to enable Debug messages; Debug output additionally requires a BepInEx listener
that accepts Debug. Changing the config during a session does not change this
startup snapshot. APIShared logs a warning on every start with the persisted switch
enabled. Neither the branch nor Debug/Release compilation enables tracing implicitly.

Info messages are not gated: APIShared keeps their number small at the call sites
(one-time startup and state markers such as Workshop transaction outcomes), so they
are available in every player log. Routine and per-use tracing is Debug. Scroll
tracing additionally needs the caller's `enableScrollDiagnostics` option. Services
receive a forwarding logger so their direct Debug calls cannot bypass the gate.
Successful native-site summaries are not constructed without opt-in; refused
registrations still produce warnings. Warnings, errors, capability failure results
and recovery evidence remain available.
Workshop `Transactions/*/diagnostics.txt` contains installation warnings and is
retained without opt-in, alongside the transaction protocol. The independent
dependency bootstrap emits only warnings/errors and creates no tracing files.

Consumer observation contracts retain their existing behavior: registering an
`IAivBuildStepObserver`, `IEnemyBridgePathObserver` or temporary gate-route observer
is explicit demand for those callbacks, not automatic APIShared tracing. Without
registration these paths do not gather observer payloads. The owner of a temporary
gate-route observer can also use `SetObservationEnabled`. Consumers must apply
their own explicit diagnostic switch before registering diagnostic observers and
before doing diagnostic analysis. APIShared's log switch does not suppress their
callbacks, failure notifications, gameplay events or public query results.

The switch also affects Debug messages written through consumer-provided loggers.
Public signatures, observer registration, callback ordering/lifetime and gameplay
behavior are unchanged. For troubleshooting, enable both APIShared's switch and any
relevant consumer/page switch.

The release driver prints an advisory summary from the staged DLL/configuration.
A compiled enabled default or shipped `Enabled=true` produces a prominent warning;
an unknown diagnostic state is reported as unknown. These notices never reject a
release. Diagnostic builds may deliberately be published. Local user configuration
can override the package default and is not inferred from a release summary.

## Multiplayer actions and messages

Declare APIShared 0.7.0 or newer and use `ApiShared.ForMod(yourPluginGuid).Network` to register a typed channel once from
the validated Unity-thread `LibraryLoaded` entry, after Extender built-in packet
registration. Registration must be unconditional and in identical order on every
peer. Extender packet IDs depend on registration order; GUIDs do not negotiate IDs
or guarantee compatibility. Every peer needs compatible mods, packet schemas and
explicit `[MessagePackFormatter]` implementations. Duplicate owner-local IDs or
two channels of the same transport kind for one packet type are rejected. One
Chore channel and one message channel may deliberately share a mixed-protocol type.
Channels and subscriptions stay rooted until process exit; use logical activation,
never component teardown.

`RegisterChore<T>(id, execute, options)` is for gameplay-changing button/key actions.
Options support a smaller maximum payload, logical activation, deterministic
validation and Unity-thread diagnostics. `channel.Send(packet)` preflights receiver
registration, serialization, the native manager and body plus two-byte packet-ID
size (at most 1200). Keep the packet, nested values and formatter output unchanged
through submission: the Extender serializes the same object again. The common
`ChoreTransport.TrySend` adapts already registered Extender packet hooks.
Never apply the effect immediately at the button: Chore receipt executes the same
handler synchronously on every peer, including the sender. Use global IDs and
desired states, not local selection or a blind toggle. The consumer owns current
session/epoch, ownership, costs, protocol compatibility and operation deduplication.
Gameplay callbacks must not touch Unity/Noesis or log directly; publish copied
presentation snapshots separately with `Network.TryPostPresentation`, without an
inline fallback. Existing dedicated receivers can use `ChoreTransport.IsChoreDelivery`
before runtime checks. A known Steam delivery is rejected. Missing
Steam identity on this dedicated type is transport classification, not verified
player authorization; payload PlayerId is not proof of origin.

Chore `IsEnabled` and `Validate` must return the same result on every peer for the
same packet. Every peer, including the sender, evaluates both synchronously when
the Chore is received, so read synchronized game or lobby state there, never a
per-machine setting, local selection or UI state. A peer whose `IsEnabled` returns
false drops the packet before `Validate` and execution while the other peers
execute it, so the simulation diverges; APIShared performs no retry or
resynchronization ([receive policy](../src/Networking/PacketReceivePolicy.cs)).
The first such drop on each channel is reported once through the channel's
`Diagnostic` callback; later drops on that channel and `Validate` rejections are
not reported. `Send` checks `IsEnabled` on the caller and returns `Disabled`
without submitting.

`NetworkSendResult.Submitted` means local preflight and a normal return from the
underlying send, not native execution or peer acknowledgement. The Extender public
API returns void and retains its internal Steam fallback. APIShared blocks its
known missing-manager/oversize triggers before calling it, but does not claim an
atomic native queue contract. There is no automatic retry, split, rollback or local
fallback; acknowledgements and large synchronized batches require a consumer
protocol. Pause does not justify replacing a Chore with a Steam packet.

`RegisterMessage<T>(id, receive, options)` is for control-plane data and presentation,
never simulation mutation. The raw callback deep-copies only the payload and
verified transport sender. An optional pure `Snapshot` callback replaces the default
MessagePack round trip; mutable descendants must also be copied. Enabled checks,
validation and receipt run after FIFO Unity dispatch. Revalidate session/generation,
roster and host authority using `message.SenderSteamId` then. Missing identity or
dispatcher causes a drop; no foreign-thread execution or inline fallback occurs.
Options also support a send-body limit and isolated Unity-thread diagnostics.
`SendToGame` supports the Extender's `instantMessage` option; `SendToPlayer` uses a
1-based game player ID. `SendToLobby` explicitly uses current active-lobby human
peers even during the lobby/map transition. `SendToSteamPeer` sends directly on
reliable Steam channel 2 without resolving a stale game player. Broadcast excludes
the sender. Sends require the initialized Unity thread and remain local results;
partial broadcast failure cannot undo messages already submitted to other peers.

See the [compiled networking example](../examples/ThirdPartyMod/NetworkingExample.cs)
for registration, an explicit formatter and a button that only submits a desired
state. Supply your own current-session and ownership validation and execution.

## Initialization and ownership

Use your plugin's stable GUID for `ApiShared.ForMod`. Keep the resulting client, logger, settings and callbacks in a static runtime or a long-lived publisher. Register managed lifecycle services from `Awake`, after the hard APIShared dependency has initialized. Acquire native services in `WhenReady`. Never treat a native failure as proof that all managed services failed. `WhenReady` is a terminal global-state notification, not a Unity-thread dispatcher: late callbacks run synchronously on the registering thread, early callbacks on the initialization publisher thread. Call Unity/Noesis operations only from a known Unity-thread entry point.

The example library compiles against ordinary public assemblies and has no `InternalsVisibleTo` access. Build it with the game path supplied through `GameDir`; its APIShared reference defaults to this repository's output for verification and can be overridden with `ApiSharedDir` for normal installed use. APIShared's build driver compiles it as a consumer check, but does not install it into the game.

## Declaring native hook sites

Prefer a shared APIShared service; use the registry for hooks APIShared doesn't cover
yet. Where a service exists (for example market price queries), subscribe to it instead
of hooking. If your mod detours, patches or validates bytes of CrusaderDE.dll that no
service covers, register those spans in `APIShared.NativeSites.NativeSiteRegistry` from
your `LibraryLoaded` handler before installing anything, and keep Vanilla behavior when
the result is a refusal. A refusal identifies the conflict; it does not resolve it. If
another mod really needs the same point, propose a shared service with ordering, veto
and replacement rules. An accepted overlap is not a compatibility guarantee. Modes,
overlap rules, when to use which, and limits: [Native sites](NATIVE_SITES.md); workflow:
[compiled example](../examples/ThirdPartyMod/NativeSiteExample.cs).

## Script Extender bugfixes

For explicitly called alternatives, see
[Script Extender replacements](../src/ScriptExtenderReplacements/DOCUMENTATION.md).
That separate public area provides corrected pathfinding-table access with the
SE method signatures; migrate those calls explicitly rather than expecting an
automatic correction of existing SE calls.

APIShared includes targeted corrections for Script Extender and bundled dependencies,
implemented in `src/ScriptExtenderFixes`. They work without SerpsMods or a consumer
registration. They do not replace the Extender and do not change existing public
APIShared capability contracts.

The popularity display correction reads the 41 direct SE popularity modifiers after
successful `GuiChecks` execution during an active mission. It automatically replaces
only contributions whose effective SE value differs from Vanilla, including values
set by third-party mods. It updates report/building text, totals and direction icons;
it never changes simulation data, current popularity, SE stacks or building identity.
No consumer registration, new setting or network packet is needed. Restoring a SE
value to Vanilla automatically removes that correction. In particular, the existing
zero-population Vanilla report discrepancy stays untouched when its SE value is Vanilla.
The separate coverage/overpopulation thresholds are outside this correction's scope.

The implementation in `ScriptExtenderFixes/Popularity` validates the audited native
and managed game fingerprints before registering. Unknown report implementations
are skipped independently. Mission observers and the GUI publisher survive startup
cleanup; ending a mission closes only the logical gate. Snapshots are read from the
current perspective, including paused checks and ration/tax previews. Public text
setters retain Vanilla labels; signed church/cathedral sums never enter the identity
byte. Overflow leaves the original display intact and produces a throttled error.
One `SE_POPULARITY_DISPLAY` confirmation is logged per corrected session.

The independent `tests/PopularityDisplay.Tests` suite covers all 41 modifiers, engine
boundary callbacks and allocation checks. In the warmed .NET test runtime, effective
value capture and calculation allocate no per-call objects; UI text output still
creates strings. This is not a Unity frame-time or multiplayer performance claim.
The original testmod correction was accepted in-game; the transferred implementation
still needs its own game/editor/save-load and real host/client acceptance.

The shutdown fix addresses an intermittent Alt+F4 crash in the audited UU-ImGUI API
1.6.7 managed DLL and its matching `cimguiaio.dll` bundled with SHCDE-SE 2.14.1.
Compatibility is determined by SHA-256 fingerprints and method/IL contracts, not
by the version label alone. The implementation also validates the installed MonoMod
compiler/hook binaries. Unity Mono needs the MethodBuilder backend for these native
call signatures: a permanent managed compiler hook routes only copies of the eight
audited methods, including internal original backups, through that backend. Other
method generation and process-wide MonoMod settings keep their existing behavior.
All fingerprints are recorded in the implementation.
Unknown or changed binaries are skipped with a `SE_IMGUI_SHUTDOWN_FIX` log reason;
other services remain available. Recheck this evidence after upstream updates.

BepInEx writes the setting to `BepInEx/config/APIShared_Serp.cfg`:

    [ScriptExtenderFixes]
    EnableImGuiShutdownFix = true

The default is enabled. Changes take effect after restarting the game. There is no
live unpatching or network setting: the fix changes only local shutdown handling.

At the first close, menu exit, or quitting notification, a terminal shutdown gate
stops new ImGui work. Subsequent Present/Resize callbacks call the existing native
trampolines directly. Already admitted callbacks may finish; the fix never frees
their contexts. Window messages continue to the saved original WndProc, and its
stored predecessor cannot be replaced with the ImGui hook itself. Context destruction,
native hook removal, and native DLL unloading are suppressed during shutdown;
the operating system reclaims resources at process exit. The existing managed
`OnDestroyed` notification and context clearing in `ProcessExit` are preserved.
Rendering, input and normal menu callbacks before shutdown retain their behavior.

Fixes must be tested with controlled competing callbacks and installed-game checks.
An official upstream correction must be inspected before updating support or
retiring a workaround. Do not install a competing patch for the same owned methods.

## Optional game-mode permissions

For a same-mission restart, wrap the complete preparation and loading action in
`MissionRestart.Run(ownerGuid, action)` on the Unity thread. This requires a ready
gameplay session. The dedicated Skirmish restart and the confirmed in-game menu
restart already use this boundary; opening or cancelling confirmation does not.
Nested calls share one attempt. Other launches must use their normal entry points.
Restart authorization retains the source's Customize provenance and conflict locks,
including saves restored without ExtendedData; network and native facts are captured
again. An incompatible native family cannot inherit the source's gameplay permission.

The source receives an ordinary `End(Replaced)` with an immutable
`ReplacementContext`. Its `RestartedFromSessionId` identifies the source session;
the destination has its own session ID and `IsRestart`, and is a new game even
when the source was loaded from a save. Settings owners can transfer their
existing context to this destination instead of leaving/re-entering a preset.
Preserve working values, source attribution, locks and personal state; do not
reload source defaults while `MissionRestart.IsInProgress`. Release transferred
ownership on destination failure or actual departure. Delayed MP-client loading
retains the destination attempt until managed completion; no second transport or
disk snapshot is introduced. Gameplay runtimes still receive normal end/start
notifications and must perform their usual cleanup. See the
[restart contract](../src/Missions/MissionRestart.cs).

Read `MissionLifecycleNotification.Context.Mode` for the event being handled. Use `GameModeHelper.Capture()` for a current snapshot where appropriate. Capture itself applies no permissions. Construct `GameplayModActivationProfile` with your own GUID, allowed contexts and `allowRealMultiplayer`, then call `GameplayModModePolicy.IsAllowed`. You can instead implement your own policy directly from the snapshot.

The built-in optional evaluator rejects unknown or conflicting origins. Its existing contexts do not offer a tutorial permission. These defaults do not prevent a third-party mod from making its own informed policy decision without the evaluator. APIShared contains no author-specific GUID or feature-permission tables; consumers own those decisions.


For a cached runtime decision, retain a `GameplayModeGate(profile)` in your static
runtime. Feed `gate.Update(notification)` from initialization, start and end callbacks
**before** your own feature work in that callback. Read `gate.IsEnabled(configuredValue)`
when applying the feature. End clears the mode; ready-session replay is supported.
The gate installs no hooks or observers and does not change lifecycle ordering.
`StateChanged` runs synchronously after an allowed/blocked transition and isolates
each listener's exception. Runtime permission and settings editability are separate.

### Optional inactive-setting backgrounds

Use the same profile in `System_ModeAvailability.ConfigureDefault(profile)` before
registering your settings view model. Override individual setting/group keys with
`Configure("some.key", featureProfile)`; use `Configure("local.hotkey", null)` for
local preferences that should stay neutral. `ConfigureNetworkVariant(key, false)`
or `true` distinguishes separate singleplayer/multiplayer values without changing
how the consumer selects them at runtime. No particular GUID or mod pack is required.

Bind `shared:ModSettingsMode.Availability="{Binding System_ModeAvailability}"` on
your page root. Put `shared:ModSettingsMode.Key="some.key"` on a logical `Grid` row.
The key can also bind to that row's `ModSettingsSearch.Key`; search itself is optional.
APIShared inserts a noninteractive `#20FFCC66` background beneath the existing row,
leaving its search style and controls intact. Bind one heading to
`System_ModeNoticeText` / `System_ModeNoticeVisibility`. The optional
`System_ConsumerModeNoticeText` / `System_ConsumerModeNoticeVisibility` pair uses an
existing direct-launch notice when one is visible; include the legend in your notice
provider if you use that combined heading. The direct-launch and Trail-source notice
bindings are also available separately.

The preset view model refreshes presentation when the settings hub changes or access
is refreshed. It uses existing menu evidence and authoritative mission snapshots.
Menu families can be ambiguous: a row is tinted only if **every** possible target
excludes it. No selected target and mixed decisions remain neutral. Explicit trail
settings retain possible customized contexts. This UI status never grants runtime
permission, changes values, disables controls or replaces host/client and trail locks.
Independent pages can instead own a `ModSettingsModeAvailability`, use `GetState(key)`
for observable row state, and supply `SetPreview(contexts, multiplayer)` or
`SetMission(snapshot)` on the Unity UI thread. Consumers own policy and localization;
the built-in compact legend has English and German fallbacks.

## Presets without SerpsModsHost

Derive your settings from `APIShared.ModSettings.PresetLobbyModSettingsViewModel` and register through `LobbyModSettingsPresetRegistration.Register`. The Script Extender displays the mod tab; APIShared owns discovery, personal preset files, persistence and lobby convergence. SerpsModsHost and ExtendedData are optional consumers, not prerequisites.

Classify saved properties with Script Extender `[SyncHostOnly]` or `[SyncPerPlayer]`, or APIShared `[PresetLocal]`. Each per-player property needs a public compatible `<Name>Data` array for slots 0..8. Keep setters behind `CanMutateSetting()` and call `OnPropertyChanged` after actual changes. Configure additional per-player rules through `ConfigurePerPlayerLobbySettings`; do not implement another roster poller.

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
LibraryLoaded before publishing their runtime. The dialog API requires APIShared 0.12.1 or newer.
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
Older ExtendedData packages with their own requester interception must be updated
together: APIShared diagnoses that combination before installing its dialog hooks.
The build installer's consumer preflight also rejects an older ExtendedData plugin
before copying the APIShared package. It checks the plugin GUID and its public
constant broker marker in assemblies below `BepInEx/plugins`, including renamed
files. It does not discover unopened Workshop archives or prove a working UI;
the runtime check remains necessary for those loading paths and manual installs.
Save archives, packet protocols, local personal presets and the existing host-load
agreement are unchanged. No ExtendedData installation is required for this UI.

## Namespaces and packaging

Use `APIShared.ModSettings` for settings and `APIShared.GameModes` for mode contracts. Settings XAML imports `clr-namespace:APIShared.ModSettings;assembly=APIShared`. Compile against the selected APIShared package and check that it contains every API used by your mod; this guide describes the source checkout. Author-specific permission tables belong in consumers, rather than APIShared; construct profiles for your own rules. Command/formation implementations belong to their consuming mods; use the public route-search events for the documented preference extension.

Release consumers with hard minimum APIShared/Script Extender versions and `<Private>false</Private>` for runtime references. Never include APIShared.dll, Script Extender DLLs or game DLLs in the consumer package. Put your XAML under the usual mod Override directory. Keep one centrally installed APIShared instance.

## HUD integration

Choose the independent side-HUD capability for actions that do not depend on troop
selection. Choose the Unit HUD's optional action-button interface for actions on the
current local troop selection. [HudExample](../examples/ThirdPartyMod/HudExample.cs)
shows both registrations using only public APIs.

Keep stable owner-local IDs and return fresh unattached controls from factories.
Use the supplied HUD resource scope and player colour rather than retaining transient
contexts or computing screen coordinates. Keep logical handles when you need to update
visibility, enabled state, text or content. APIShared owns layout and paging; your mod
owns command authorization and multiplayer synchronization.

Factories and commands run on the Unity thread and must not block. Troop descriptions
use Vanilla rollover text; side buttons use the mod-options popup by default.
See the [catalog](API_CATALOG.md#side-hud-buttons) for layout, refresh, failure,
tooltip and notification contracts.

## Sharing action and HUD hooks

Use [the managed action/HUD contracts](API_CATALOG.md#managed-actions-and-hud-interception)
and [InterceptionExample](../examples/ThirdPartyMod/InterceptionExample.cs) when your
feature needs a site owned by these publishers. Add Pre only for a deliberate policy
or replacement; augment completed Vanilla UI in Post. Store invocation-private
information in Pre `State` for your matching Post/Accepted. A later veto still runs
Post; original HUD exceptions also run Post for cleanup and then propagate. Never
infer simulation success from managed completion. Guard mutable consumer settings
with an appropriate atomic snapshot if the original can be called on multiple
threads. Do not register a competing detour at the same site or compile APIShared
implementation sources into your mod.

These brokers require APIShared 0.6.0. Register once with a stable owner GUID and
owner-local ID, then activate callbacks through your own logical settings.
Successful Pre vetoes remain sticky; notification order is ascending order followed
by ordinal owner GUID and ID. Failed Pre changes are rolled back. State is private
to each registration, and registrations added during a call start on the next call.

Use GameAction Accepted for preparations that must happen only after every Pre has
accepted the final inputs. GameAction Post runs on normal return or veto, but not
when the original throws; a returned zero does not acknowledge recruitment failure.
Keep native creation reconciliation in your mod. RecruitmentRequestPolicy supplies
ceiling/reservation arithmetic, including the distinction between an untouched Ctrl
sentinel and an explicitly assigned amount. RecruitmentMaterialUi combines stock
display bypass predicates without changing native costs or other button gates.

For a custom HUD button action, assign Presentation Pre Replacement rather than
executing effects inside Pre. It runs once after all Pre callbacks with the final
parameter; a later veto suppresses both it and Vanilla. Presentation Post also runs
when the original or replacement throws. Check WasReplaced, CompletionException and
OriginalCompleted before augmenting UI, and use State for cleanup. Exceptions are
rethrown after Post; a partially executed replacement is not retried as Vanilla.

### Broker implementation and measurements

GameAction, Presentation, market query and sound brokers share one
[registry](../src/Core/Events/InterceptionEvent.cs). Without active registrations a
site calls its original before creating args. An observed call creates its public
Pre args, one Post args per Post registration, a rollback checkpoint per Pre callback
and owner-State storage only when a Pre has a matching Post or Accepted. Post
factories are static and receive an explicit value-type context. Presentation sites
pass their receiver to an original delegate created once per site, so no caller
creates a closure or delegate per call. Public args are not pooled; observers may
retain them.

Bytes per observed call, measured by `BrokerAllocationTests` on the local 64-bit
.NET Framework 4.8.1 test host in Release: one registration, no-op callbacks and
original, 2000 warm-up calls, minimum of five samples of 20000 calls. Presentation
is the GuiChecks site shape; its 07089d1 value reproduces that site's original
closure in the fixture. "Public args" allocates only the objects listed above.

| Path, registration | 07089d1 | Closure-free callers | Public args |
| --- | ---: | ---: | ---: |
| GameAction, Post | 264 | 168 | 112 |
| GameAction, Pre | 176 | 144 | 88 |
| GameAction, Pre + Post | 328 | 232 | 176 |
| Market query, Post | 256 | 160 | 104 |
| Market query, Pre | 168 | 136 | 80 |
| Market query, Pre + Post | 312 | 216 | 160 |
| Presentation GuiChecks, Post | 376 | 184 | 128 |
| Presentation GuiChecks, Pre | 288 | 160 | 104 |
| Presentation GuiChecks, Pre + Post | 440 | 248 | 192 |

The remaining 56 bytes are the registry's per-call invocation object. An IL test
rejects closures, delegates, arrays and boxing in the observed caller paths. Each
additional Post registration adds its Post args; mixed active and dormant internal
registrations add a filtered snapshot array.

Execution time was compared A/B on the same host against 07089d1, with the same
test entry points in both builds and the GuiChecks site closure reproduced in the
07089d1 probe: one registration, no-op callbacks, 5000 warm-up calls, the minimum of 41
samples of 10000 calls, eight alternating runs at raised process priority. Values are
the best run in ns per call; every run was faster after the change.

| Path, registration | 07089d1 | Closure-free callers |
| --- | ---: | ---: |
| GameAction, Post | 208.9 | 194.1 |
| GameAction, Pre + Post | 250.3 | 230.8 |
| Market query, Post | 173.0 | 156.6 |
| Market query, Pre + Post | 199.5 | 186.4 |
| Presentation GuiChecks, Post | 223.0 | 197.0 |
| Presentation GuiChecks, Pre + Post | 272.6 | 248.8 |

Timing is indicative and not asserted; the host also ran unrelated work (21 to 51%
CPU). Unity's Mono runtime and garbage collector and real callbacks are outside
these measurements; no FPS change is implied.

## Sharing native market price queries

Register APIShared.Economy.MarketPriceEvents after APIShared's LibraryLoaded
initialization, on the startup publisher thread. Inspect TryRegister's failure
reason; calls before initialization can be retried, while unknown native images or
occupied entries fail closed. Keep callbacks rooted for the process lifetime.
See the [query contract](API_CATALOG.md#native-market-price-queries) and
[native ownership](NATIVE_COMPATIBILITY.md#ownership-and-validation).

Validate player/goods/amount arguments before indexing your own data;
HasNativeManager only indicates a nonzero manager pointer. To replace a total,
set ReplacementTotal and SkipOriginalFunction together. Assigning the total alone
does not veto the helper. Post observes the final query result, not a completed
trade. Returning zero does not cancel the surrounding transaction. Policies must
cover AI planning, execution and ally valuations consistently on all multiplayer
peers. CalculateTradeTotal preserves Vanilla's signed division before unchecked
multiplication. Do not install a competing detour at either shared query entry.

## Reacting to created and recruited units

Use `APIShared.Units.UnitSettledEvents` when your mod changes or records a unit once it
exists in its final type: health or speed multipliers, upkeep registration, stable
links. It is not in APIShared packages up to 0.9.1; check the package you require.
Register once during startup with your plugin GUID, a stable ID and an optional
order, on every multiplayer peer and before a mission starts. Keep the callback
registered for the process lifetime and return early while your feature is off.

Filter on `Cause`, `PlayerId` and `UnitType` in the callback. Recruits arrive as
`MercenaryOutpost` or `EuropeanBarracks` with the peasant in `PreviousUnitType`;
units made by the game's spawn function arrive as `Created`. A unit with two
triggers can be reported twice, so key state that must not double by `GlobalId`
together with the unit ID. Callbacks run inside the simulation tick: change only
simulation state, deterministically, and do not log per unit, touch Unity/Noesis or
decide by local player there. Post presentation work with `Network.TryPostPresentation`
or your own queue.

Only triggers raised after the first registration in the process are observed.
Recruits whose purchase preceded a save are not reported after loading it, and restored
units only if the game recreates them through its spawn function (not verified). Scan
existing units at mission start and after a save load (for example from the
mission lifecycle Start notification) and make that scan and the settled path
idempotent. `DroppedUnits` and `CallbackFailures` are diagnostic counters. The exact
settle and drop rules are documented at the
[declaration](../src/Units/Settled/UnitSettledEvents.cs); the
[example](../examples/ThirdPartyMod/UnitSettledExample.cs) scales recruited troops' health.

### Settlement implementation and measurements

One tracker owns a queue of triggers in raise order. SE unit events read the slot
once (owner, GlobalId, type) and queue a value under a short lock. SE `OnTick` reads
one volatile count and returns while nothing waits; otherwise each waiting slot is read
again and settled values are delivered after the lock is released, against an
immutable registration snapshot. SE map unload, map load and save load (Pre) discard
the queue because its unit IDs belong to the previous world. A Vanilla recruit keeps
its peasant slot, owner and GlobalId: the native recruitment state runs for about 33
unit updates, passes a transient non-alive state and then initializes the requested
type in the same slot (native audit of the supported CrusaderDE image).

An installed-game self-test of a consumer mod (game 2.8.2, SE 2.14.1, four runs) used
the same rule: barracks and mercenary recruits changed type 35 simulation ticks after
`OnUnitTransition`, and units made by the spawn function were alive with a GlobalId
after 1 or 2 ticks. That run exercised the consumer's implementation, not this
service; in-game checks of this service, worker and disband transitions, save loads
and multiplayer are still pending.

Core.Tests (.NET 10) and the installed-assembly suite (.NET Framework 4.8.1) measure
zero allocated bytes after warm-up for idle ticks with registrations, for ticks with
32 or 64 waiting units, and for queueing, settling and delivering 32 units to three or
four registrations. On the local .NET Framework Release test build, an idle tick took
3 to 6 ns including the measurement delegate, a waiting unit 30 to 36 ns per tick, and
a queued, settled and delivered unit 108 to 114 ns with four registrations. IL tests
check the compiled SE entry points and the tick guard for heap allocation. The
fixtures use a fake slot reader, not Mono, the native unit array or consumer
callbacks; they are not an FPS measurement.

## Extending APIShared

Use the [catalog source map](API_CATALOG.md#source-map) to find the current contract
and its implementation. The [architecture guide](../ARCHITECTURE.md#finding-and-extending-a-feature)
explains where shared behavior, hook ownership and consumer algorithms belong.
Follow [CONTRIBUTING](../CONTRIBUTING.md) for setup and tests. An ordinary clone of
this repository is sufficient; no maintainer workspace or AI assistant is required.

## Assassin obstacle completion

Assassin obstacle control uses `AssassinAttackControlAPI.RegisterGuard(ownerGuid, callback)`
for idle-only completion, or its overload with `AssassinObstacleCompletionMode.NativeRetarget`
to run Vanilla's existing objective-based target selection after a veto. The synchronous callback
receives a one-based unit game ID on the native simulation thread and must not issue commands.
One owner holds the permanent hook; consumers implement their own logical activation predicate.
Native selection can fail or select another vetoed obstacle; it does not guarantee a successful route.
Obstacle completion does not expose consumer-defined alternative traversal search
and requires no command-engine mod.

## Workshop package cleanup

APIShared cleans its own previous releases through the contract-validated Extender staging
path. Foreign maps remain Extender-managed. Inventory schema, historical hashes
and preservation rules are in [Workshop cleanup](WORKSHOP.md#workshop-package-cleanup).

## Plugin entry points with optional APIShared

Optional consumers requiring APIShared 0.7.1 or newer may call `SavegameModSettings.ExcludePluginFromAutomaticSettings(typeof(MyPlugin))` after dependency validation and before settings registration, instead of applying the exclusion attribute to their independent bootstrap type. This thread-safe registration is idempotent, applies to derived types and lasts until process exit. The exclusion applies only to the registered type and its derived types. Set your consumer's minimum APIShared version to 0.7.1 or newer when calling this method.

## Versioned save data

Store per-mission mod state in saves with `APIShared.Savegames.VersionedSaveData` instead of a
raw `ModSaveDataAPI` handler when the data must survive mod updates and downgrades. Register
once at startup with your own identifier, record version, minimum reader version, known entry
names, a capture callback and a load callback. Apply delivered entries when your mission state
is ready, and treat `NoRecord` as a new mission. Follow the
[record-version migration policy](SAVE_DATA.md#record-version-migration-policy) when the
format changes, and add a legacy reader when you keep an identifier that already holds data.
See [Save data](SAVE_DATA.md) and the [compiled example](../examples/ThirdPartyMod/SaveDataExample.cs).

## Loose Workshop packages

Use the shipped external publisher for explicitly declared Payload packages.
The [Workshop guide](WORKSHOP.md) covers publishing, prerequisites, manifests,
bootstrap maps, consent, embedded-copy replacement, recovery and Windows/Proton limits.

## Situational sound rules

Declare APIShared **0.8.0** or newer. Register through `SoundEvents` after managed
startup, retain the handle and use `SetEnabled` for live settings. Filter context
before changing a request; preload clips outside playback callbacks. If your rule
needs only some named situations or sources, pass a `SoundSourceSelection` to
`TryRegister` so the other soldier sounds stay native ([source selections](AUDIO.md#source-selections)).
See the [audio integration guide](AUDIO.md#situational-sound-rules).

### Consumer filters, repeat limits and other sound policies

Unit-type/source filters, evidence checks, repeat limits and local profiles use the
same Pre/Post events. See [policy examples and boundaries](AUDIO.md#consumer-filters-repeat-limits-and-other-sound-policies).

### Exact-unit sound cues

For a cue that depends on one unit (for example a converted unit's kill sound), use
`SoundSourceEvents` rather than a detour of the positional wrapper: its args carry the
acting unit's game ID and GlobalId, the killed victim for melee kill cues, and
`TryReplaceSound`. See [exact-unit cue replacement](AUDIO.md#exact-unit-cue-replacement).

## Building tooltip text

APIShared 0.9.0 provides `APIShared.Presentation.BuildingTooltipPresentation`.
On the Unity startup thread, register your own plugin GUID, a stable contribution
ID and a provider returning `IReadOnlyList<BuildingTooltipLine>`. Check the returned
boolean and diagnostic. A line contains immutable `BuildingTooltipText` segments
with independently selectable ARGB foreground colours; localize text in your mod.
The service owns wrapping, inherited game text direction and the two shared hosts.
Do not patch their layout or replace Vanilla hover controls.

The provider receives the Vanilla structure identifier (not mapper ID), Compact
or Detailed variant and `IsSymbolHover`. Identifier zero means that no building
tooltip is active: retire selection state if necessary; returned rows are not
displayed in that case. It runs synchronously after completed
BuildingRollover/GuiChecks operations on the confirmed Unity main thread. Return
null or an empty collection when inactive or irrelevant. State, counters, native
reads and gameplay authorization remain yours. Empty text consumes no space.
Providers are retained until process exit; there is no unregister or hook teardown.
Callbacks run outside registry locks; registrations made from a callback take
effect on the next update. Failures discard that provider's entire current result
and report once; other providers continue, and subsequent calls can recover.

Rows are ordered top-to-bottom by ascending optional `order` (default 0), then
ordinal owner GUID and contribution ID. Higher values sit closer to Vanilla's
cost line. Identical owner/ID pairs are rejected. Cache immutable results when
possible. Never call the HUD update recursively or retain a HUD instance.
Numeric fragments in RTL languages should use directional isolates when needed.
Vanilla's compact cost anchor is preserved as contributions grow upwards; the
detailed tooltip expands its existing background. Additional text has no custom
background, buttons or markup. This does not arbitrate arbitrary third-party XAML
replacements of the original tooltip.

See [BuildingTooltipExample](../examples/ThirdPartyMod/BuildingTooltipExample.cs)
for a foreign-plugin registration independent of the maintainer's mod pack.

### Implementation and measurement limits

The dormant publisher guard is allocation-free; unchanged
contexts and collected row snapshots are reused. Provider callbacks still run
when registered so dynamic activation is not incorrectly cached. Layout size
notifications invalidate cached width; geometry and VM work resume on the next
Unity post publication. The pure Core.Tests fixtures measure zero steady-state
collection allocations with empty or cached immutable providers after warm-up on
.NET 10. They do not include Mono/Noesis rendering, consumer callbacks or native
reads, and are not an FPS benchmark.

Initialization validates the six HUD_Main selection fields as before and compiles
one typed field read for each, so the GuiChecks and BuildingRollover Post reads them
without reflection or boxing: 96 bytes per update at 07089d1, 0 after, measured by
`BuildingTooltipHudFieldTests` on the .NET Framework 4.8.1 test host against the
installed game assembly. In the broker A/B timing run the six reads and selection rule
took 29.5 ns instead of 284.8 ns per update (best of eight runs; every run faster).
Providers are still collected before the no-building return, since they may retire
selection state there.

## Gate access contributions

Use `GateAccessRules.TryRegister` for additional gate access exceptions and
`Invalidate` after synchronized data changes. Registration performs no world work.
Follow the [gate routing guide](GATE_ROUTING.md); a routing provider is required.

## Rounded fear popularity

The 0.10.0 development API provides `APIShared.Economy.FearPopularity` for absolute,
fractional popularity coefficients per signed fear level. After native initialization,
call `TryRegister` with your own plugin GUID and stable local ID, retain the handle,
and inspect the returned diagnostic. SE compatibility is determined from the installed
module's actual coefficient-to-resource data flow, without an SE version or file-hash
allowlist. The separate native game ABI remains bound to its audited fingerprint.
No consumer native hook or SE stack manipulation is needed.

`handle.SetMultiplier(1.1m)` computes `round(level * 1.1)` in whole report points,
with midpoint ties away from zero, then converts to the engine's 25 units per report
point. Thus level 5 produces 6 points and level -5 produces -6. The calculation runs
at every validated SE fear multiplication, before the worst-source decision, event
penalties and final native clamp. Reports and preview panels use the same arithmetic.
Fear level, productivity and combat remain unchanged.

Registration starts inactive. Set null at your mission end or when disabled; do not
unhook or unregister. Among active registrations, the last in ascending order, ordinal
owner GUID and local ID wins. `SetMultiplier` is thread-safe; synchronized settings,
mode permissions and activation boundaries remain the consumer's responsibility.
When every handle is inactive, original SE multiplication, including other mods'
integer coefficients, remains intact. Overflow preserves the SE simulation contribution
and logs once; the report's existing error boundary preserves its original display.
Use sensible bounds in the consumer. The API accepts decimal coefficients; UI ranges
and step sizes belong to the consumer.

See the [compiled example](../examples/ThirdPartyMod/PopularityExample.cs).
The pure popularity tests cover rounding, ownership and matching display deltas;
installed-assembly tests validate and generate the actual edited SE callback and reject
partial/double edits. Equivalent one-site/multiple-site calculations, reordered operands,
local encodings, harmless NOPs and renamed compiler carriers are accepted. Incorrect input
fields, player pointers, signedness/storage widths and changed arithmetic are rejected.
Unsupported exception-region or escaped-reference flows fail with a diagnostic rather
than claiming compatibility. The analysis runs once at startup, not on simulation ticks.
Tests do not execute native game memory or prove in-game Mono,
editor, save/load or real multiplayer acceptance. The adapter is permanent and statically
rooted; startup component destruction never removes it.
