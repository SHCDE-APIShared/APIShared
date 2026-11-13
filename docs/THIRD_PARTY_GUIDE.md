# Third-party integration guide

**Contents**

- [Example package requirements](#example-package-requirements)
- [Initialization and ownership](#initialization-and-ownership)
- [Namespaces and packaging](#namespaces-and-packaging)
- [Extending APIShared](#extending-apishared)
- [Declaring native hook sites](#declaring-native-hook-sites)
- [APIShared interface language](#apishared-interface-language)
- [Native simulation ticks](#native-simulation-ticks)
- [Managed engine-run boundaries](#managed-engine-run-boundaries)
- [Demand-driven engine readback](#demand-driven-engine-readback)
- [Native field knowledge](#native-field-knowledge)
- [Diagnostic opt-in and observer demand](#diagnostic-opt-in-and-observer-demand)
- [Game-independent content](#game-independent-content)
- [Player defeat state](#player-defeat-state)
- [Player views and control](#player-views-and-control)
- [Coop Trail AI start import](#coop-trail-ai-start-import)
- [Mission settings and presets](#mission-settings-and-presets)
- [Optional game-mode permissions](#optional-game-mode-permissions)
- [Multiplayer actions and messages](#multiplayer-actions-and-messages)
- [Script Extender bugfixes](SCRIPT_EXTENDER_FIXES.md)
- [Plugin entry points with optional APIShared](#plugin-entry-points-with-optional-apishared)
- [Versioned save data](#versioned-save-data)
- [Workshop package cleanup](#workshop-package-cleanup)
- [Loose Workshop packages](#loose-workshop-packages)
- [HUD integration](#hud-integration)
- [Sharing action and HUD hooks](#sharing-action-and-hud-hooks)
- [Sharing native market price queries](#sharing-native-market-price-queries)
- [Staged values and health](#staged-values-and-health)
- [Ordering units taken from AI groups](#ordering-units-taken-from-ai-groups)
- [Reacting to created and recruited units](#reacting-to-created-and-recruited-units)
- [AI military admission](AI_MILITARY.md)
- [Assassin obstacle completion](#assassin-obstacle-completion)
- [Gate access contributions](#gate-access-contributions)
- [Enemy-unit building proximity and Peace Time](#enemy-unit-building-proximity-and-peace-time)
- [Plague standing-height policies](#plague-standing-height-policies)
- [Rounded fear popularity](#rounded-fear-popularity)
- [Situational sound rules](#situational-sound-rules)
- [General in-game tooltip extensions](#general-in-game-tooltip-extensions)
- [Building tooltip text](#building-tooltip-text)
- [Multiplayer map lookup and lobby system hints](#multiplayer-map-lookup-and-lobby-system-hints)
- [End-screen presentation](#end-screen-presentation)
- [Shared render and pause-menu presentation](#shared-render-and-pause-menu-presentation)
- [Mapper selection camera](#mapper-selection-camera)
- [Lord statistic columns](#lord-statistic-columns)
- [Packaged Lord presentation](#packaged-lord-presentation)
- [Lord message replacement](#lord-message-replacement)

## Example package requirements

The [third-party plugin example](../examples/ThirdPartyMod/ExamplePlugin.cs) declares
its required APIShared and Script Extender minimum versions in its BepInEx attributes
and [info.json](../examples/ThirdPartyMod/info.json). Keep these declarations consistent
in your own mod, choosing minimum versions for the APIs it actually uses. The complete
example uses more APIs than the networking example alone; check their individual requirements.
Install the complete APIShared package and reference its assemblies with
`Private=false`. The example references both `APIShared.dll` and
`APIShared.Content.dll`; neither is bundled in the consumer. Its GUID is
`Example.Author.APISharedDemo`, independent of ExtendedData and the modpack.

## Initialization and ownership

Use your plugin's stable GUID for `ApiShared.ForMod`. Keep the resulting client, logger, settings and callbacks in a static runtime or a long-lived publisher. Register managed lifecycle services from `Awake`, after the hard APIShared dependency has initialized. Acquire native services in `WhenReady`. Never treat a native failure as proof that all managed services failed. `WhenReady` is a terminal global-state notification, not a Unity-thread dispatcher: late callbacks run synchronously on the registering thread, early callbacks on the initialization publisher thread. Call Unity/Noesis operations only from a known Unity-thread entry point.

The example library compiles against ordinary public assemblies and has no `InternalsVisibleTo` access. Build it with the game path supplied through `GameDir`; its APIShared reference defaults to this repository's output for verification and can be overridden with `ApiSharedDir` for normal installed use. APIShared's build driver compiles it as a consumer check, but does not install it into the game.

## Current-mission PlayState readiness

`ModApiClient.TryGetMissionPlayStateReadiness` supplies the additive
`IMissionPlayStateReadinessCapability`. Managed lifecycle Start does not imply that
Vanilla has published a current-mission `GameData.lastGameState`: multiplayer may
still wait for peers, and presentation can be paused. Register a stable owner-local
ID through `TryRegisterObserver(id, onReady, out diagnostic)` on Unity. Keep the
capability and callback for the process lifetime. Already-ready registration may
replay synchronously before returning; otherwise delivery occurs on Unity when a
fresh current-mission PlayState is observed. There is no loading-duration deadline.

`MissionPlayStateReadyNotification` contains immutable `Context` and `IsReplay`.
`Current` is safe to query from any thread and returns the ready current context,
or null while waiting/outside a mission. End and replacement invalidate it. Before
performing deferred work, require `capability.Current?.SessionId` to equal the
receipt's session. Callbacks are exception-isolated and cannot revive an ended
session. Registrations made inside a callback receive their replay after it exits.

Readiness tracks managed Start plus a nonnull PlayState reference different from
the earliest pre-initialization reference. It reuses lifecycle, GuiChecks and the
existing Unity work publisher; GuiChecks completion alone is not readiness. This
does not authorize gameplay or establish HUD roots, a player identity, converged
roster, selected report viewpoint or fresh report values after switching views.
Keep those feature-specific checks. Existing lifecycle Start behavior is unchanged.
The compiled [example](../examples/ThirdPartyMod/MissionReadinessExample.cs) shows
registration and session validation.

## Deferred Unity work and refresh demand

Requires APIShared 0.26.0 or later.

`ApiShared.ForMod(yourGuid).Ui` provides presentation work independently of native
or network readiness. `TryPost(Action)` always defers through the existing validated
Unity synchronization context, even on Unity. FIFO means enqueue order; a nested
post may run in the same drain. It is not a next-frame or render-phase promise.
False means unavailable; accepted work is not durable across context failure or
shutdown. `Network.TryPostPresentation` retains its existing contract and queue.

Register a stable owner-local ID with `TryRegisterBeforeRender(id, callback, out
registration, out reason, order)`. Registrations are process-lived and initially
dormant. Handles are thread-safe: `Request()` coalesces refreshes; `RequestAfter(delay)`
keeps the earliest nonnegative monotonic deadline, and zero is immediate. An immediate
request satisfies an older deferred refresh. Invalid/overflowing delays throw before
changing demand. Delays run on the first rendered frame at/after the deadline,
including while paused, without catch-up calls. They are refresh requests, not action timers.

`SetContinuous(true)` requests every rendered frame; false preserves separately
pending work. `CancelPending()` clears uncaptured immediate/deferred work, leaving
continuous demand unchanged. Each callback runs at most once per rendered frame.
The service freezes a pass and consumes current requests before invoking callbacks;
requests or toggles during it apply to later frames. A canceled frozen callback may
still run once. Keep current session/HUD guards and never permanently capture a map's
view. Callbacks and diagnostic failures are isolated, outside registry locks.

Ordering is `order`, ordinal owner GUID, then ordinal registration ID. No ordering
against foreign Unity callbacks or Vanilla GUI writes is promised. Use the existing
`PresentationEvents.GuiChecks` for post-Vanilla HUD writes, concrete input events for
input, and simulation events for gameplay. Do not throttle commands, authorization,
input protection or decisive save/load checks. Use bounded delays only for presentation
whose latency permits them. See the compiled [example](../examples/ThirdPartyMod/UiWorkExample.cs).

One static publisher survives startup cleanup. Dormant, waiting and steady continuous
scheduler paths allocate nothing after warm-up; real demand changes may allocate
copy-on-write snapshots. Callback cost and application-level scans remain the consumer's
responsibility. Internal lobby fallback remains 15 rendered frames; save preparation
continues every frame only while pending and not already loading.

## In-game test automation

Get the owner-bound client with `ApiShared.ForMod(yourGuid).Automation`. After
APIShared initialization, explicitly start a run on Unity from your development
build. A run receives a GUID, a sequence of named `AutomationStep` objects and a
BepInEx logger. It emits one terminal `INGAME_TEST run=… result=…` line and can
optionally quit the game. The caller owns scenario selection, activation and
restoration of external settings and files.

Steps yield `AutomationWait.NextFrame`, `Delay` or `Until`, and may throw to
fail with context. Each step has a monotonic deadline, including during pause.
Keep callbacks short. An individual synchronous game call cannot be preempted
by a frame-based timeout. Use unbound steps to load a local save or start local
Skirmish, and mission-bound steps for the subsequent assertions. Wait separately
for fresh PlayState and native tick progress. Capture the current mission before
using a mission-scoped action. Quitting is explicit and applies after success,
failure or cancellation.

Use the Extender directly for unit and wall creation inside `InSimulation`.
Return managed values from that scope and keep native pointers inside it.
`TrySpawnBuilding` confirms an ordinary single-building placement through the
existing Extender event. Supply the audited sprite variant for the chosen mapper.
Noesis clicks send input through the active game view. Verify the outcome as a
separate assertion. The helpers target local tests and leave lobby and mission
settings policy to the regular game and APIShared entry points.

## Namespaces and packaging

Use `APIShared.ModSettings` for settings and `APIShared.GameModes` for mode contracts. Settings XAML imports `clr-namespace:APIShared.ModSettings;assembly=APIShared`. Compile against the selected APIShared package and check that it contains every API used by your mod; this guide describes the source checkout. Author-specific permission tables belong in consumers, rather than APIShared; construct profiles for your own rules. Command/formation implementations belong to their consuming mods; use the public route-search events for the documented preference extension.

Release consumers with hard minimum APIShared/Script Extender versions and `<Private>false</Private>` for runtime references. Never include APIShared.dll, Script Extender DLLs or game DLLs in the consumer package. Put your XAML under the usual mod Override directory. Keep one centrally installed APIShared instance.

## Extending APIShared

When extracting settings into another plugin, declare an owner move through
`ModSettingsMigration.RegisterModMove` before mission preparation. Reuse the existing
renames/converters for property-level changes. Explicit destination data remains
authoritative, and Player author rules follow the new owner. Custom Map/Trail readers
must migrate their detached author document before provider checks and reset-source
caching; see [the migration contract](MOD_SETTINGS.md#moving-mission-settings-between-mods).

Use the [catalog source map](API_CATALOG.md#source-map) to find the current contract
and its implementation. The [architecture guide](../ARCHITECTURE.md#finding-and-extending-a-feature)
explains where shared behavior, hook ownership and consumer algorithms belong.
Follow [CONTRIBUTING](../CONTRIBUTING.md) for setup and tests. An ordinary clone of
this repository is sufficient; no maintainer workspace or AI assistant is required.

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

## Safe managed IL patches

MonoMod rebuilds a method from its original IL whenever any IL hook or Harmony
patch of it changes, and runs every applied manipulator and transpiler again. A
manipulator that throws there drops every IL edit and Harmony patch of that
method, including other mods' patches, and keeps failing every later change
because it stays applied. Edits that expect the IL of a game version or of
other hooks are exactly the ones that can throw after an update.

- **IL hooks:** install with `SafeIlHook.TryInstall(ownerGuid, id, method,
  manipulator, log, out hook, out reason)`. It validates the edit on a copy
  (including code generation and JIT), applies it permanently, and on every later
  rebuild catches any exception and restores the body it received. Nothing is
  installed when it returns false. `IsApplied`, `Diagnostic` and the `Rebuilt`
  event report the latest rebuild. A presentation feature may follow them; keep
  gameplay or multiplayer-relevant readiness fixed after installation, so a
  later rollback never flips it mid-mission. The `id` is passed to MonoMod unchanged.
- **Own installers:** `SafeIlHook.Wrap(...)` returns the guard and its
  non-throwing `Manipulator` for an `ILHook` you create (with `ManualApply`);
  validate with your raw manipulator, which may throw.
- **Independent parts:** `SafeIlHook.TryEdit(il, edit, out error)` rolls back
  one part of a manipulator and keeps the rest.
- **Harmony transpilers:** return
  `SafeTranspiler.Run(ownerGuid, id, instructions, transform, log, outcome)`.
  The edit runs on copies and is fully evaluated; on an exception the original
  instructions are returned. Validate the original IL before patching, for
  example with `PatchProcessor.GetOriginalInstructions`, so an unsupported game
  version fails the installation instead of leaving the method unchanged.

Find all sites before the first edit. These helpers cannot repair IL that is
accepted but later rejected by code generation, nor another mod's throwing
manipulator.

## APIShared interface language

APIShared's own UI uses `Locales/<locale>.txt`, not consumer/modpack resources.
The internal reader is not a public localization service. Files are UTF-8 with
`Key=Value` entries, `#` comments, named `{Placeholders}`, `\n` for line breaks and
`\\` for a literal backslash. Values retain spaces and may contain `=`.
English and German are maintained; other supported game catalogs currently contain
the same keys with English substitutes. Duplicate/unknown keys, invalid UTF-8 and
changed placeholder sets reject that external catalog with a diagnostic.

In game, an explicit Extender language override wins, followed by Steam's game
language, the Extender fallback and English. Main-thread UI calls publish the
selected language for workers; catalogs are cached per language. Missing keys use
English. The English file is also embedded in the assembly, so missing or damaged
installed catalogs retain usable messages. The publisher uses the operating
system's UI culture; its reader and English resource are compiled into the tool.
Generated warning bootstraps compile their own reader, language selector and
catalog resources and work before APIShared is installed. Launcher helpers read
catalog data without executing it. Package/build evidence includes the catalogs.

For preset consumers, continue overriding
`ResolveSettingsUiText(string key, string fallback)` if needed. Existing `Common.*`
keys and their English fallback values are retained; a nonempty consumer result
that differs from the key takes precedence. Empty/key results or translation
exceptions fall back to APIShared's selected catalog. APIShared does not replace
Vanilla text or text supplied by consumers. Technical diagnostics, protocol values
and mod identities are not translated.

## Native simulation ticks

Requires APIShared 0.22.0 or later (`SimulationClock`: 0.23.0 or later).

For synchronous initialization work, use the existing mission lifecycle phases.
`Simulation.SimulationClock.TryGetCurrentTick(expectedMission, out int mapTick)`
is available after successful native loading, before managed readiness is required.
The internal gate is updated before `NativeLoaded` observers run and cleared before
another native load/unload or mission retirement. The expected mission must still
be active; a read rechecks the immutable load token to reject same-attempt reload
races. Before native completion there is no substitute clock. This helper does not
make a callback safe for arbitrary mutations, acquire the engine lock, or add an
initialization tick event. Existing initialization notifications do not replay.

Use `Simulation.SimulationClock.TryGetElapsedTicks(startTick, currentTick, out elapsed)`
only for values from the same mission. It supports signed wrap and distances up to
Int32.MaxValue; backwards/ambiguous distances return false. Keep availability in
separate state: zero, negative ticks and Int32.MinValue are valid native values.
Early synchronous transactions should use their operation scope until map time is
available, rather than borrowing the preceding mission's counter.

After APIShared's library initialization, register once with
`Simulation.SimulationTickEvents.TryRegister(ownerGuid, registrationId, callback, out reason, order: 0)`.
Failure returns a reason; keep the dependent feature inactive. Registrations last
until process exit. Use logical activation in the callback rather than registering
again at each map start. Callbacks run synchronously on the simulation publisher's
thread, outside registry locks, ordered by order, ordinal owner GUID and registration
ID. Exceptions are isolated per registration; registrations added during delivery
start at the next tick. There is no replay, catch-up or main-thread dispatch.

The argument is the signed Int32 representation of the native map counter, preserved
in saves. Duplicate/older ticks in the same mission and reentrant publications are
suppressed, including across Int32 wraparound. A new mission may reuse earlier tick
values. `TryGetCurrentTick(out int mapTick)` reads the same source without activating
notifications. Check its Boolean result: zero is valid and is not an unavailable
sentinel. Read in the simulation context or at completed mission initialization
while simulation is stopped/locked; the getter acquires no engine lock.

Only fully started missions publish, including the editor. Pending or failed loads,
menus and unavailable native time sources publish nothing. The service uses the
central mission lifecycle and the public Extender `GetElapsedMapTicks` getter.
It forwards the existing Extender time callback and reads the native counter once
for all APIShared recipients of that publication. Paused/skipped simulation steps
do not advance the native counter. Delivery still depends on the Extender reaching
its event: an exception in earlier Extender triggers, runners or subscribers may
prevent a notification. This adapter cannot recover that lost callback.

**Multiplayer:** the audited Extender `GameTimeManagerAPI.OnTick` argument comes
from `Director.getSimTickCount`, a local engine-run counter that includes multiplayer
frame skips and FPS compensation. Do not use that argument to schedule tick-dependent
multiplayer mutations. The callback context itself is suitable; this API provides
the native map tick without changing existing Extender events, R3 schedulers,
timestamps, high-precision clocks or timer serialization. Register matching callbacks
before the mission on all peers, use synchronized settings and deterministic state,
and keep game mutations synchronous. Do not block, touch Unity/Noesis, or modify
native scenario/timeline vectors from these callbacks.

For a periodic rule, capture the current map tick at completed mission start and
on logical reactivation; execute only later matching boundaries. This preserves
save phase and avoids repeating the boundary already present in the loaded save.
Feature permission, recipient selection and accounting remain the consumer's job.

## Attack target rules

Register one or more owner-local rule sets with `Units.AttackTargetRules.TryRegister`
during startup (APIShared 0.43.0+). Check `IsAvailable` after native publication.
Each endpoint can match a native controlling player (including neutral zero),
unit type, a world-specific global ID, or their conjunction. Null matches any.
Rules are directed unless bidirectional is selected. All owners contribute
exclusions, so an earlier match cannot be overridden by another owner.

Initial configuration uses `TrySetConfiguredRules` with identical agreed
settings on all multiplayer peers. It supports a condition on the actual
native Peace Time flag, including its exact expiration. For world-specific
rules use `TryReplaceRules` with the current mission and an increasing revision.
In real multiplayer, call it from an authenticated Chore receiver after
authorizing the native issuer and requested mutation. The receiver applies
the agreed state on all peers at the execution tick. UI, ordinary ticks and
Steam deliveries cannot directly publish world-specific multiplayer rules.
Republish agreed rules for the restored world after a load.

The hook covers the audited melee contact, candidate and ranged revalidation
paths reaching the native pair predicate. It does not cover all attack orders,
damage sources or the Hunter bypass. A nonmatch preserves the original result.
This API does not negotiate treaties or synchronize consumer settings itself.

## Troop-command observation

Register with `Units.TroopCommandInputEvents.TryRegister` after managed
initialization (APIShared 0.44.0+). The observer receives the final local map
tile and an immutable list of unit IDs in cursor order. Slots filtered out
by the Extender are omitted. The observer runs synchronously
under the existing engine monitor before Vanilla processes the tick.
The Extender's selection replacement gestures do not produce a notification.
The release can represent a move or another action, and Vanilla can still
reject it. Treat it as local input only. Keep gameplay policy and multiplayer
authority in the consumer.

## Managed save observation

Register with `Savegames.ManagedSaveEvents.TryRegister` during startup
(APIShared 0.42.0+). Begin receives immutable call arguments and can return
state for its own completion callback. Both callbacks run on the caller thread
using the same ordered registration snapshot. A nested call has its own state.
Callbacks must return promptly and avoid invoking saves or touching the UI.

Each invocation publishes completion once, even if Harmony re-enters the
finalizer. Completion reports the boolean seen at the finalizer, a skipped original,
or an exception. A skipped or throwing invocation has no result. The path
seen at completion can differ from the initial path when another patch
rewrites it. These observations neither authorize mutations nor prove that the
file reached disk. The observer also sees map exports through the managed
entry point. Native synchronized saves that bypass it have their own contract.

## Managed engine-run boundaries

After APIShared managed initialization, register a paired observer with
`Simulation.EngineRunEvents.TryRegister(ownerGuid, registrationId, begin, end, out reason, order: 0)`.
Both callbacks are required. Use a stable mod GUID and an owner-local registration
ID. If the shared boundary is unavailable, registration fails with a reason.
Registrations last until process exit. Keep transient scope state in the consumer.

Each call admitted to the existing managed engine-run wrapper takes one
registration snapshot. Begin and end both run in ascending order, then by
ordinal owner GUID and registration ID. They run synchronously on the engine
caller's thread. A registration added during a callback participates in the next
snapshot. A nested admitted engine call takes its own snapshot and completes its
pair before returning to the enclosing call.

End runs after a zero result, a failed begin callback, or an exception in the
original. Callback failures are isolated. The original result or exception
reaches the caller, and calls rejected by engine admission emit no callbacks.
There is no replay, veto, replacement or dispatch. Keep callbacks short and
avoid touching Unity/Noesis, blocking, or initiating another engine run. The
pair identifies a managed engine call. Use native ticks to observe advancement
and a feature-specific readback to establish gameplay effects.

## Demand-driven engine readback

After APIShared native initialization, call
`Statistics.EngineReadbacks.TryRegister(ownerGuid, stableId, copyPreparedRawData, out registration, out reason)`.
Registration is permanent and rejects duplicate owner/ID pairs; `reason` names the
unavailable publisher, missing argument or duplicate. The overload without `reason`
has the same behavior. On the main thread,
prepare private reusable buffers and immutable request identity, then call `Request()`.
The existing `EngineInterface.run` / `MemoryBuffers.returnBuffer` publisher invokes
requested readers in ordinal owner/ID order, after native execution and managed
state copying but before the completed buffer is published. It additionally verifies
current-thread ownership of `EngineInterface.threadLock`. Runs without a completed
buffer retain demand. This is not proof that game time advanced.

The callback may only copy bounded raw data into that request's prepared storage.
Do not allocate, wait, log, call providers or touch Unity/Noesis/view models there.
Publish completion atomically and consume it on the main thread. Keep a running
buffer owned until completion; `Cancel()` drops queued demand but cannot interrupt
an already executing callback. Recheck mission, HUD generation and information
rights before publishing. Clear sensitive retired data after producer ownership
ends. Never retain game-native pointers between callbacks.

For example, prepare a buffer and set an atomic queued state on the main thread;
let the registered callback claim that state once, copy native fields and release
publish a completed state in `finally`. A main-thread zero-wait `Monitor.TryEnter`
fallback may claim the same job while paused. Consume a completed state from an
existing GUI publisher; avoid per-request dispatcher closures under the engine lock.
Requests during a callback are retained for a later publication. Callback exceptions
are isolated; use `TryTakeError(out error)` outside the critical section. The broker
provides no refresh cadence, session validation, automatic retries or coherent UI
snapshot: those are consumer responsibilities. Dormant registrations do no copying.

## Native field knowledge

Use `APIShared.NativeKnowledge.NativeFieldCatalog.Default` for offline metadata and
`NativeFieldReader.TryGetCurrent(out var reader)` after native initialization for
the actual process image. Retry acquisition from an existing appropriate publisher
if called too early; no Unity component or new polling loop is needed. A reader for
an unknown image reports `UnsupportedImage` on reads. Catalog field IDs remain stable
through name improvements; meanings and storage verification are independent.
Callers own native pointer validity, the game-thread/lifetime contract and correct
view origins. See the [complete usage contract and example](../src/NativeKnowledge/INTEGRATION.md)
and [field/evidence reference](../src/NativeKnowledge/FELDKATALOG.md).

For reverse engineering, see the separate [function-name reference](../src/NativeKnowledge/FUNKTIONEN.md).
Its hash-bound authoring JSON is not consumed by the runtime and grants no native-call contract.

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
Workshop `_APIShared/T/*/diagnostics.txt` contains installation warnings and is
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

ExtendedData exposes its result types from ExtendedData.Core.dll through adapters
to this implementation. Its Lord sidecar/snapshot limits and host-data rules apply.

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

ExtendedData's `ModSettingsDefinition`, `ModSettingsEntry` and `ModSettingsJson`
are provided by ExtendedData.Core.dll as adapters, including observable mutations
and entry aliases. Its filesystem `Read`/`WriteAtomic`, `.modtrail.json` selection,
mission packages and Lord/host authority remain in ExtendedData. Shared application
and cleanup live in APIShared; see the mission application section below.
Offline consumers can use Content directly without loading either runtime assembly:

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
Release readiness requires separate game and package-lifecycle acceptance of the development package.

APIShared optionally loads after Fixes for its audited
[wheat-sale startup correction](FIXES_COMPATIBILITY.md). No consumer registration
is required. Check its explicit outcome log; APIShared being present does not
prove that an unknown Fixes binary was corrected.
Supported legacy stack-load and division-based wheat hooks are corrected inside
their existing allocations; no consumer adapter or settings migration is needed.

Unused observation services do not scan or prepare events. Register player-defeat
transition observers before the first simulation tick if early transitions matter: the
first tick after the first subscriber establishes the baseline, and transition callbacks
never replay earlier losses; [state observers](#player-defeat-state) receive the current
snapshots instead. Lobby observers receive a current capture on first registration and the
known snapshot on later registrations. Marked selection observers and successful
`TryCapture` calls establish process-lived tracking demand.

### Trail containers

`APIShared.Maps.TrailContainer` (Content) inspects an SCDE map or `.trail` container
held in a caller-owned byte array. `ReadLayout` validates the magic, the six
length-prefixed preamble blocks, the optional restart field and a known section
directory tag, and returns the restart offsets. `ReadRestartData` copies the saved
restart payload; `ExtractMap` returns the embedded map with an empty restart field,
copying every other byte, including section data and unknown trailing data, unchanged.

Results are independent copies; the input is never modified. Malformed, truncated or
oversized input (default limit 64 MiB, configurable per call) throws
`InvalidDataException`. The library does not read files, decode the restart payload,
load maps or decide whether the game accepts an extracted map; test that in the game.

```csharp
byte[] trail = File.ReadAllBytes(trailPath);
byte[] restart = TrailContainer.ReadRestartData(trail);
File.WriteAllBytes(mapPath, TrailContainer.ExtractMap(trail));
```

## Player defeat state

Requires APIShared 0.24.0 or later.

Acquire the player-defeat capability through your owner-bound client, then check
whether it implements `IPlayerDefeatStateCapability`, which is independent of the
transition callbacks of `IPlayerDefeatCapability`. Register a state observer when an already defeated player in a loaded save matters:

```csharp
if (client.TryGetPlayerDefeat(out var defeat, out var diagnostic) &&
    defeat is IPlayerDefeatStateCapability states)
{
    states.TryRegisterStateObserver("defeat-state", snapshot => {
        // Dispatch UI work yourself. Verify the current session before acting.
        if (states.TryGetObservedState(snapshot.SessionId, snapshot.PlayerId, out var current) &&
            current.IsEliminated)
        {
            // Apply your own mode/participant/feature policy; state grants no control rights.
        }
    }, out diagnostic);
}
```

State observers receive available current snapshots in player order on registration,
then changed snapshots and unreadable invalidations through the service's ordered
FIFO. A registration before the first observation receives its initial states on that
tick. Callbacks run outside service locks on the FIFO-draining thread, which can be
the simulation, registering or lifecycle thread; exceptions are isolated and reentrant
publications follow the current delivery. Owner/registration order is deterministic.
State and transition registrations share the owner-local registration-ID namespace.
The state FIFO is independent of the original transition delivery: lord-death and
official-loss callbacks remain synchronous on their simulation-tick publisher,
even while another thread is delivering state replay. State is committed before
those transition callbacks, so their managed lookup sees the confirmed observation.

`TryGetObservedState(sessionId, playerId, out snapshot)` is a thread-safe managed lookup,
not a native read or an implicit observation request. It fails before the first
observation and for an invalid or ended session. Its successful result can still have
`Readable == false`; that state grants no elimination rights. `OfficialLoss` includes
a loaded loss baseline; `LordDeathConfirmed` requires a previously observed living
lord followed by its death or disappearance. A lordless baseline alone is insufficient.
`IsEliminated` requires readable official loss or confirmed lord death. Unreadable
captures withdraw the visible confirmation until readable state returns.

Snapshots are immutable and can be retained as historical values. Recheck current
state and session before acting: queued deliveries can describe an ended mission.
Unchanged observations reuse their snapshot and do not notify; `SimulationTick`
records the last state change, rather than the latest observation or saved map tick.
Session-end invalidation retains that tick. The existing transition observers still
receive no baseline replay, even when a state observer receives a loaded loss.

Keep mod-specific diagnostic analysis and logging in your mod. `UnitAccess.TryGetById`
returns `UnitLookupFailure` for this purpose and does not log rejected lookups.
Generic observation hooks remain shared; they do not create payloads without
interested consumers. Registering an observer is explicit demand for observation.

## Checked entity access and index bounds

`BuildingAccess` and `IndexBounds` require APIShared 0.49.0 or later.

Use `BuildingAccess.TryGetById(buildingId, out GameBuilding* building, out var failure)`
for an immediate building view, or pass a previously captured `GameBuildingManagerAPI`
as the first argument. Building IDs are one-based. Nonpositive IDs return
`InvalidId` before even accessing the lazy manager. A missing manager returns
`ManagerUnavailable`; an ID beyond its current capacity returns `OutOfRange` before
the SDK lookup. A failed lookup or null pointer returns `Unavailable`. Every failure
clears the output pointer; success reports `None` and passes the ID unchanged.

These helpers do not log rejected IDs. The consumer owns diagnostics and decides
whether a missing ID is expected. They neither filter `NeedsInit`/dead records nor
validate identity or owner. Use the existing game-thread/simulation synchronization
contract and consume pointers immediately; do not retain them across simulation
work or mission changes. Revalidate global identity, owner and life state before
acting. `UnitAccess.TryGetById` retains its existing equivalent unit-ID contract.

For a zero-based span index use `IndexBounds.IsValidIndex(index, span.Length)`.
It returns true exactly for `index >= 0 && index < length`; empty or negative lengths
admit no index. Unlike a game ID, index 0 can be valid. The function allocates nothing,
accesses no game state and performs no ID-to-index conversion. Keep specialized
rules for reserved slots, projectile layouts, packed native storage and identity.
Do not narrow wider integer types merely to call this `int` helper. Ordinary
already-bounded iteration does not need an additional check per element.

## Player views and control

Original-spectator views, eliminated-participant views, observer command exceptions and
spectator input control (an original spectator plays one chosen player with Vanilla input)
are described in [player views and control](PLAYER_CONTROL.md).

## Coop Trail AI start import

Vanilla's Coop Trail start (`FRONT_Multiplayer.StartSkirmishGame`, Coop branch) calls
`AIVLoader.UploadDefaultAIV` once per AI player. Unlike the skirmish branch it never
imports selected castles or sets a custom Lord configuration (AIC). APIShared is the
single owner of that upload; mods describe what each AI should receive:

```csharp
ModApiClient api = ApiShared.ForMod(MyPlugin.Guid);
if (api.TryGetCoopAiStartImport(out ICoopAiStartImportCapability import, out var diagnostic))
    import.TryRegisterProvider("my.coop.ai", request =>
        IsMyMission(request.CoopTrail, request.CoopMission)
            ? new CoopAiStartContribution(BuildPlans())
            : null, out diagnostic);
```

- Providers run on the Unity thread inside the native multiplayer initialization of
  each Coop start attempt (`CoopTrail` 1-4, zero-based `CoopMission`), before the AI
  upload loop. Read only prepared in-memory state; confirm selection, session and
  host authority yourself. Every peer must derive the same plans from the same data.
- A `CoopAiSlotPlan` names the 1-based player ID, the Lord type Vanilla uploads for
  the slot, the castle source and an optional `EngineInterface.AILordConfigTransferData`.
  Explicit candidates are imported in order into bank `PlayerId - 1`; the AIC follows.
  No additional AIV reset is performed. Slots without a plan keep Vanilla's upload.
- Mark a plan incomplete when only a prefix of the candidate list is known, such as
  the single candidate Vanilla's lobby data transmits. Complete plans from several
  providers must be identical; incomplete plans must be prefixes of the complete one.
  Contradictions, provider exceptions, a changed Lord type or a repeated upload abort
  the start with `InvalidOperationException` instead of starting with Vanilla castles,
  so validate earlier at your own start button.
- `ConfirmedCandidateCount` (constructor overload) states how many leading candidates
  you confirmed against data every peer shares. A complete list you could compare only
  partly, such as a packaged list checked against Vanilla's single transmitted
  candidate, may confirm fewer; a fully confirmed complete plan whose leading
  candidates match then supplies the import. Existing plans confirm all candidates.
- Validate at the start button with the preview the same capability object provides:

```csharp
if (import is ICoopAiStartPreviewCapability preview &&
    preview.TryPreview(coopTrail, coopMission, out CoopAiStartPreview result, out _) && !result.Succeeded)
    BlockStart(result.Failure);
```

  The preview runs every provider with `request.IsPreview` set and combines the plans
  without importing anything; `PlanFor(playerId)` shows the plan that would be imported.
  Answer a preview with the plans you will supply for that start, for example a list a
  pending host synchronization will transfer.
- Registrations are permanent; return null to contribute nothing. Plans exist only for
  the current attempt. The capability requires the mission lifecycle.

## Mission settings and presets

Mission settings preparation, personal values, settings participant diagnostics, presets
without SerpsModsHost, settings search, load/save requesters and settings locked during a
mission are described in [mod settings integration](MOD_SETTINGS.md).

## Optional game-mode permissions

For a same-mission restart, wrap the complete preparation and loading action in
`MissionRestart.Run(ownerGuid, action)` on the Unity thread. This requires a ready
gameplay session. The dedicated Skirmish restart and the confirmed in-game menu
restart already use this boundary; opening or cancelling confirmation does not.
Nested calls share one attempt. Other launches must use their normal entry points.
Restart authorization retains the source's Customize provenance,
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

The built-in optional evaluator rejects unknown modes. APIShared's own captures never leave an origin conflicting: unusable provider evidence is ignored, the mode is decided from the game's own data and the decision is logged. Its existing contexts do not offer a tutorial permission. These defaults do not prevent a third-party mod from making its own informed policy decision without the evaluator. APIShared contains no author-specific GUID or feature-permission tables; consumers own those decisions.


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

## Multiplayer actions and messages

These networking APIs require APIShared 0.7.0 or later; neither SerpsModsHost nor
ExtendedData is required. Follow the [package requirements](#example-package-requirements)
for dependency declarations, the complete central installation and `Private=false`
references; never bundle your own APIShared DLL with the mod.

### Chore quick start

1. Define a packet with an explicit MessagePack formatter, stable global object IDs
   and a desired state, such as `BuildingGlobalId` and `DesiredEnabled`.
2. Obtain `ApiShared.ForMod(yourPluginGuid).Network` and register a Chore channel once
   from the Unity-thread `LibraryLoaded` entry after Extender built-in registration.
   Retain it in your process-owned runtime; register in the same order on every peer.
3. Supply deterministic validation and execution callbacks. Check current session,
   protocol and ownership; use synchronized state for `IsEnabled` and `Validate`.
4. At the button/key action, call `channel.Send(packet)` and inspect its result.
   Apply the effect only in the receiver, which also executes on the sender.
5. Post copied UI/log snapshots with `Network.TryPostPresentation`; keep gameplay
   execution synchronous in the Chore callback.

Use the [compiled networking example](../examples/ThirdPartyMod/NetworkingExample.cs)
as the starting point. Its helpers are not automatically wired into `ExamplePlugin`:
call `NetworkingExample.Register` from your own initialization path above, supply your
session/ownership checks and gameplay callback, and route your button to `OnButton`.

### Registration and Chore execution

Use `ApiShared.ForMod(yourPluginGuid).Network` to register a typed channel once from
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

### Submission limits and results

`NetworkSendResult.Submitted` means local preflight and a normal return from the
underlying send, not native execution or peer acknowledgement. The Extender public
API returns void and retains its internal Steam fallback. APIShared blocks its
known missing-manager/oversize triggers before calling it, but does not claim an
atomic native queue contract. There is no automatic retry, split, rollback or local
fallback; acknowledgements and large synchronized batches require a consumer
protocol. Pause does not justify replacing a Chore with a Steam packet.

### Authenticated Chore channel

Requires APIShared 0.23.0 or later.

`RegisterAuthenticatedChore<T>(id, execute, options)` registers a Chore channel whose
sender is verified from the executing native slot. Register it with the same startup/order and
explicit-formatter requirements as a regular Chore channel; one packet type cannot have both
a regular and an authenticated Chore handler.

`AuthenticatedChoreMessage<T>` provides the packet, `SenderPlayerId`, native
`CommandId` and final `ScheduledTick`. The source is checked against both the active
native pending slot and executing issuer, independently of payload player IDs and
the local viewpoint. A shared managed scope binds the native body and exact packet
ID before deserialization. Nested dispatches cannot inherit a different packet's
identity; the native tuple must remain unchanged after validation. Known Steam deliveries, missing context or unsupported native
layouts are rejected. `ScheduledTick` is not a session token or operation identity.
Consumer `ValidateReceive` runs after this capture, synchronously in simulation
context; it must still validate the current session, authority, rights and live
object identities. A verified participant is not automatically permitted to control
another player. `ValidatePayload` runs on send and receive. Validation must not
mutate the packet or simulation; dispatch only the actual action in `execute`.

`Send` serializes once and calls the validated raw SE Chore path, with no Steam
fallback. It requires Unity and a real local player in 1..8 whose ID matches the
native enqueue view under the engine monitor. A foreign report viewpoint rejects
submission instead of impersonating that owner. `IsAvailable` reports technical
reader/dispatcher/adapter readiness on the initialized Unity thread, not peer agreement
or control rights. Other threads receive false without game-state reads; off-thread
`Send` rejects before consumer activation, validation or serialization. Submitted still
means local submission only; no acknowledgement, retry or immediate gameplay
effect is implied. These rules apply only to authenticated channels.

The adapter is deliberately tied to audited native layout and loaded SE method
contracts; see [native update requirements](../UpdateToNewDLL.md#authenticated-chore-context-and-submission).

### Control-plane messages

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

## Script Extender bugfixes

APIShared corrects selected Script Extender defects without consumer registration; see
[Script Extender fixes](SCRIPT_EXTENDER_FIXES.md). Save handlers in particular receive
`SaveContext.IsSaveFile=true` with a null `FilePath` for synchronized native saves, including
autosaves; branch on the explicit context rather than assuming autosaves are maps.

## Plugin entry points with optional APIShared

Optional consumers requiring APIShared 0.7.1 or later may call `SavegameModSettings.ExcludePluginFromAutomaticSettings(typeof(MyPlugin))` after dependency validation and before settings registration, instead of applying the exclusion attribute to their independent bootstrap type. This thread-safe registration is idempotent, applies to derived types and lasts until process exit. The exclusion applies only to the registered type and its derived types. Set your consumer's minimum APIShared version to 0.7.1 or newer when calling this method.

## Versioned save data

Store per-mission mod state in saves with `APIShared.Savegames.VersionedSaveData` instead of a
raw `ModSaveDataAPI` handler when the data must survive mod updates and downgrades. Register
once at startup with your own identifier, record version, minimum reader version, known entry
names, a capture callback and a load callback. Apply delivered entries when your mission state
is ready, and treat `NoRecord` as a new mission. Follow the
[record-version migration policy](SAVE_DATA.md#record-version-migration-policy) when the
format changes, and add a legacy reader when you keep an identifier that already holds data.
See [Save data](SAVE_DATA.md) and the [compiled example](../examples/ThirdPartyMod/SaveDataExample.cs).

## Workshop package cleanup

APIShared cleans its own previous releases through the contract-validated Extender staging
path. Foreign maps remain Extender-managed. Inventory schema, historical hashes
and preservation rules are in [Workshop cleanup](WORKSHOP.md#workshop-package-cleanup).

## Loose Workshop packages

Use the shipped external publisher for explicitly declared Payload packages.
The [Workshop guide](WORKSHOP.md) covers publishing, prerequisites, manifests,
bootstrap maps, consent, embedded-copy replacement, recovery and Windows/Proton limits.

## HUD integration

Choose the independent side-HUD capability for actions that do not depend on troop
selection. For the small Vanilla troop-action field, use
`APIShared.Presentation.TroopActionHud.TryRegisterButton`; APIShared places registered
actions directly while they fit and provides HUD pages on overflow. For the
separate strip beside Control Groups, use the Unit HUD's optional action-button
interface. [HudExample](../examples/ThirdPartyMod/HudExample.cs) shows these registrations
using only public APIs.

Keep stable owner-local IDs and return fresh unattached controls from factories.
Use the supplied HUD resource scope and player colour rather than retaining transient
contexts or computing screen coordinates. Keep logical handles when you need to update
visibility, enabled state, text or content. APIShared owns layout and paging; your mod
owns command authorization and multiplayer synchronization.

Factories and commands run on the Unity thread and must not block. Troop descriptions
use Vanilla rollover text; side buttons use the mod-options popup by default.
See the [catalog](API_CATALOG.md#side-hud-buttons) for layout, refresh, failure,
tooltip and notification contracts.

### Side-HUD overlays

With APIShared 0.37.0 or later, call `APIShared.Presentation.SideHudVisibility.TryGetState` with the current
`MainViewModel` on the Unity thread. An overlay can opt into staying visible for
`HiddenByHudMode`. This state requires an active in-game view, the Vanilla HUD
mode switched off, and the independent side-HUD flag still enabled. Briefing,
the game menu and mission-over screens report `Hidden`. If the query returns
`false`, use your overlay's ordinary visibility rules without the exception.

Read once when attaching the overlay, then refresh on `PropertyChanged` for
`Show_InGame`, `Show_InGameUI`, `Show_HUD_Extras`, `Show_HUD_Briefing`,
`Show_HUD_IngameMenu` and `Show_HUD_MissionOver`. Reattach the subscription when
the view changes. Overlay activation, popup conflicts and session cleanup belong
to the consumer. Registered side-HUD buttons keep their existing visibility rules.

### Vanilla action-field registration

`TroopActionButtonDefinition` supplies the command, localized rollover text and a
factory for an unattached Vanilla-styled Button. APIShared controls the button's
command, enabled state and visibility. Keep the returned handle and call
`SetVisible(true)` only when the feature applies; new registrations start hidden.
Priority is nonnegative and followed by owner GUID and button ID for stable ordering.
Use `RequestButtonRefresh` when sprite/style content must be recreated; prefer the
handle's simple setters for visibility, availability and rollover text.

Use a context callback to close consumer-owned UI when the own-selection context is
lost. Page placement is separate from context availability. Ordinarily APIShared
publishes the provided rollover text, including over a disabled button. Supply a
hover callback only when your mod owns the complete rollover, such as an icon or
cost metadata; it must handle false when the button is hidden or the HUD ends.

Legacy XAML hosts retain their original two-slot behavior and are not reparented into
additional pages. Migrate those contributions explicitly to the typed factory
registration; remove their old host and layout binding so the same action is not
registered twice.
The split navigation button advances through pages with its upper right arrow and
goes back with its lower left arrow; both wrap. The first page retains Vanilla
actions, and an action click keeps the chosen page. Consumers do not implement
navigation or a layout of their own. The full placement, reset, lifecycle and
failure contracts live in
[the catalog](API_CATALOG.md#vanilla-troop-action-slots-and-pages).

### Unit HUD sprites in another player's colour

Requires APIShared 0.24.0 or later.

A spectator or control feature that shows another player's units can lease the unit HUD
sprite colours instead of hooking `MainViewModel.UpdateUITroopSprites` itself:

```csharp
if (!UnitHudSpriteColours.TryLease(MyPlugin.Guid, viewedPlayerId, out string reason))
    Logger.LogWarning(reason); // e.g. another mod holds the lease
// ... later, when your view ends:
UnitHudSpriteColours.Release(MyPlugin.Guid);
```

Call both on the Unity main thread, for example from your perspective-change handler after
dispatching to it. Call `TryLease` again with the new player when the view changes; repeating
the same player does nothing. The lease ends at mission end, so lease again in the next mission.
Read a player's UI colour without changing the HUD with `PlayerColours.TryGetUiColour`.

Limits: only images that Vanilla's sprite update recolours change. Keeps, outposts, flags and
the other building icons have no colour variants, and some resource keys are identical in all
colours. The Arabic troop variants follow the leased player only for human multiplayer members;
otherwise they keep Vanilla's value for the local player. Map sprites, labels, selection and
control rights are unaffected. The [lease contract](../src/Presentation/UnitHud/UnitHudSpriteColours.cs)
describes ordering, replay and failure behavior.

## Identifying a Lord skin

Use the one-based player ID in the active mission or editor on the Unity main thread.
Skin and UI colour are separate lookups:

```csharp
if (LordSkins.TryGetForPlayer(playerId, out LordSkin skin))
{
    PlayerColours.TryGetUiColour(playerId, out int colour);
    // Select your own portrait using skin and the UI colour. Colour 0 means unavailable.
}
```

The skin lookup verifies the registered unit's identity and returns `Unknown` when
the Lord is absent, dead or has an unrecognized material. Callers that already have
the material of a Lord can use `LordSkins.TryGetFromMaterial` directly.

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

Use `GameActionEvents.TryRegisterWithFinalValidation` when a policy must verify
that the final payload still matches the original request after other mods' Pre
callbacks. The validator receives read-only original and final arguments and the
registration's own Pre State. Return false to veto. A Pre veto skips validation,
and the first validator rejection or exception skips Accepted and the original.
Keep validators free of side effects. Use Accepted for preparations that require
the final inputs to have passed all vetoes.

Nested commands from callbacks normally retain Vanilla passthrough. A validator
can declare specific command kinds that must be blocked during a notification.
APIShared copies this list at registration and applies it for the duration of the
active invocation. A blocked nested command returns zero with no notification.
An optional thread-safe, side-effect-free activation predicate affects the next
invocation and must avoid calling GameAction. A predicate exception is reported
and leaves the validator active.

GameAction Post runs on normal return or veto. An original exception propagates
without Post. A returned zero does not acknowledge recruitment failure.
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

## Staged values and health

APIShared 0.28.0 adds shared `BeforeFixedValue` and opt-in `AfterFixedValue`
composition for numerical policies. The normal stage is the default; after-stage
factors also multiply a winning fixed value. Existing legacy APIs keep their
meaning, including the absolute coefficient in fear `SetMultiplier`. Read the
[numeric contract and economy APIs](VALUE_MODIFIERS.md) before mixing policies.

For maximum HP, register `HealthModifiers` after `LibraryLoaded` and enable
immutable contributions at synchronized mission boundaries. APIShared captures
the completed native basis and freezes each generation after one application.
See [the health contract](HEALTH.md) for save migration, category ownership,
limits, linked siege towers and the [compiled example](../examples/ThirdPartyMod/HealthExample.cs).

For market prices use [MarketPriceModifiers](MARKET_PRICE_MODIFIERS.md) to compose
per-lot base prices across manual trade, AI, autotrade, HUD and valuation. Existing
low-level query veto totals remain distinct and compatible. ExtraFeatures' AI
exception now omits only its own factors, allowing other registered mods to work.

## Sharing native market price queries

For reserve or quantity policies, see [AI market sell policies](AI_MARKET_SELL.md)
and the [consumer example](../examples/ThirdPartyMod/AiMarketSellExample.cs).
Register after LibraryLoaded, then enable the process-held registration on every
peer using the synchronized host setting. Safety-only registrations stay native.

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

## Ordering units taken from AI groups

`GameTribeManagerAPI.IssueMoveHereCommand` and Vanilla's other group moves silently skip a member
that still carries the AI's group-move exclusion mark. The AI sets it for its own next group
order; regrouping keeps it. If your mod moves units out of an AI group (or orders a unit without a
group) and then gives a group move, clear the mark of exactly those units right before the call with
`APIShared.Commands.GroupMoveExclusion` (not in APIShared packages up to 0.38.0; check the package
you require):

```csharp
using APIShared.Commands;

// Simulation thread, in a context every multiplayer peer executes alike.
GroupMoveExclusion.TryClear(unitId, out bool wasMarked);
GameTribeManagerAPI.Instance.IssueMoveHereCommand(ownTribe, x, y, false, 1, TribeMoveType.DefaultInSync);
```

- The mark is simulation state. Call only in a native simulation tick callback or a tick-synchronous
  Chore that runs on every peer, never from local input, UI or a network thread.
- Clear only units you order yourself. When you repeat an order of the AI on the AI's own group,
  keep the marks: they are the AI's current member selection.
- `TryClear` returns false without a write when `IsAvailable` is false (another CrusaderDE.dll or
  unit layout; see `UnavailabilityReason`), the ID does not resolve or the unit fails
  `UnitAccess.IsReallyAlive`. A true result only means the mark is 0; the move can still skip the
  unit for its own admission rules.

The native contract is in [UpdateToNewDLL](../UpdateToNewDLL.md#group-move-exclusion-mark).

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

## Assassin obstacle completion

Assassin obstacle control uses `AssassinAttackControlAPI.RegisterGuard(ownerGuid, callback)`
for idle-only completion, or its overload with `AssassinObstacleCompletionMode.NativeRetarget`
to run Vanilla's existing objective-based target selection after a veto. The synchronous callback
receives a one-based unit game ID on the native simulation thread and must not issue commands.
One owner holds the permanent hook; consumers implement their own logical activation predicate.
Native selection can fail or select another vetoed obstacle; it does not guarantee a successful route.
Obstacle completion does not expose consumer-defined alternative traversal search
and requires no command-engine mod.

## Gate access contributions

Use `GateAccessRules.TryRegister` for additional gate access exceptions and
`Invalidate` after synchronized data changes. Registration performs no world work.
Follow the [gate routing guide](GATE_ROUTING.md); a routing provider is required.

## Enemy-unit building proximity and Peace Time

Requires APIShared 0.25.0 or later.

Register owner-bound `EnemyProximityPolicies` callbacks after native initialization.
Use Override for general category radii and Limit for reductions; valid vetoes
always prevail. Resolve your own settings sentinels before contributing a radius.
For a peace-only rule, check the availability result as well as the active value
of `PeaceTimeState.TryGetActive`. No extra tick subscription is needed.
See [enemy proximity](ENEMY_PROXIMITY.md) for ordering, standalone collaboration,
thread/lifetime, native fallback and multiplayer requirements.

## Plague standing-height policies

Requires APIShared 0.14.0 or later (`TryGetPlagueHeightPolicy`: 0.24.0 or later).

`ApiShared.ForMod(yourGuid).TryGetPlagueHeight(...)` acquires the optional
`IPlagueHeightCapability` after native readiness. Acquisition alone changes nothing.
Call `TrySetPolicy(active, categories, maximumHeightDifference, out diagnostic)` on
the main thread before or after simulation. Consumers own mission authorization,
host settings and identical configuration on multiplayer peers. No callbacks or
additional event transport are involved; existing Script Extender projectile-damage
Pre/Post events retain their original meaning and ordering.

Categories are `Walls`, `Gates`, `Towers`, `Keeps` or combinations. Tolerance is an
inclusive integer difference of standing planes, 0..65534 native height units;
the cloud's visual +20 offset is excluded. The range is the 16-bit packed native
policy contract, not a gameplay recommendation. Unit-type immunity, damage tiers,
apothecary resistance, treatment, lifetime and cadence remain native.

Repeated acquisition with one GUID shares that owner's policy. Owners start inactive.
Active policies are unioned, with the largest requested tolerance per category;
ordinary ground uses the largest active tolerance. Disabling one owner cannot veto
another. No active categories restores the complete original paths. Invalid input
leaves the previous snapshot unchanged. Unknown native/backend contracts make only
this capability unavailable with a diagnostic. Hooks and registrations persist until
process exit; never dispose or patch executable code to toggle these rules.

`TryGetPlagueHeightPolicy(out policy, out diagnostic)` reads the effective policy of all owners,
the one the native damage path applies, without acquiring or changing an owner. It is
allocation-free and safe on any thread, for example on the simulation thread of a mod that
predicts where clouds would damage units. `PlagueHeightPolicy` reports the tolerance per
category (-1 when no owner enables it) and `GroundTolerance`; `IsActive` false means the
complete Vanilla path. When the service is unavailable the call returns false with an inactive
policy, which also means Vanilla.

Building identity takes precedence over wall flags. Low/high walls and stairs,
walkable gates, defensive towers and keep platforms can be controlled separately;
mobile siege towers are excluded. Unrecognized elevated surfaces keep Vanilla
exclusions. Destroyed platforms are reevaluated from the current tile/building data.
See [the independent example](../examples/ThirdPartyMod/PlagueHeightExample.cs).

Native implementation and validation: [update contract](../UpdateToNewDLL.md#plague-standing-heights).
The executed fixtures cover original/modified paths, all category combinations,
multiple owners, heights, full native damage semantics and ABI preservation. These
checks do not replace in-game rendering, save roundtrips or a real multiplayer run.

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
When no registration applies to the current level, original SE multiplication, including other mods'
integer coefficients, remains intact. Overflow preserves the SE simulation contribution
and logs once; the report's existing error boundary preserves its original display.
Use sensible bounds in the consumer. The API accepts decimal coefficients; UI ranges
and step sizes belong to the consumer.

`handle.SetLevelContributions(IReadOnlyDictionary<int, int>)` (APIShared 0.15.0 or later) accepts
absolute whole report points for exact signed input levels. For example,
`{ [-1] = -2, [-2] = -4, [-3] = -6, [-4] = -8, [-5] = -10 }` doubles the Vanilla
negative contributions while leaving positive levels to earlier registrations or SE.
Output signs do not select the side: the key always names the original fear level.
Zero-valued contributions are active overrides; key zero is rejected so the neutral
level stays neutral. Other Int32 keys are allowed even though Vanilla produces -5..5.
The caller's map is copied; any invalid key, duplicate entry or overflowing points*25
conversion throws before publication and preserves the preceding configuration.
Null or an empty map disables the handle. Each setter replaces that handle's entire
configuration: a table never retains a previous multiplier or old table keys.
Among registrations the last applicable one wins separately for each level, using
the same order/GUID/ID ordering as uniform coefficients. A later uniform coefficient
covers every level; a later sparse table changes only its listed levels. The
published snapshot is immutable and read without locks or per-calculation allocations.
Simulation and the report correction share it. Existing multiplier callers retain
their rounding, overflow and logical-disable behavior; no new native or managed hook
is installed for tables.

### Human and AI contributions

Requires APIShared 0.16.0 or later.

`Economy.PopularityOverrides.TryRegister(ownerGuid, registrationId,
out handle, out diagnostic, order)` creates an inactive ordinary-source policy.
Call `handle.SetContributions(IReadOnlyDictionary<PopularitySource,
PopularityOverrideValues>)` at synchronized mission boundaries. The enum names the
SE parameters for rations, food variety, crowding, taxes, religion, ale and fairs.
`new PopularityOverrideValues(humanPoints, aiPoints)` contains independent nullable
whole report points: null leaves that player type untouched; zero is an explicit
contribution. Human covers every human participant; AI includes allied and enemy
AI lords. Invalid, empty or unresolved player slots receive no targeted policy.

Fear handles add `SetTargetedLevelContributions(IReadOnlyDictionary<int,
PopularityOverrideValues>)`. This distinct name preserves unambiguous legacy null
calls. Every fear setter replaces the complete configuration of its handle. Old
uniform multipliers and fixed level tables retain binary/source compatibility,
rounding, ordering, overflow and availability semantics. Mixed registrations select
the last non-null applicable value separately for each source, player type and
fear input level. Missing values reveal earlier policies and then the effective SE
global value, including foreign stack overrides. No global stack swaps are used.

Setters defensively copy and validate all keys and checked points*25 conversions
before publication. Invalid updates throw without changing active configuration.
Null or empty tables deactivate; unknown source enum values and fear key zero are
rejected. Handles remain owner-bound until process exit. Register after native
initialization, check diagnostics and keep synchronized settings, host authority
and mission activation in the consumer. Setters are thread-safe and may run on
any thread; no user callbacks execute under registry locks.

One immutable role/source/fear snapshot is captured per successful calculation
from the same R15 player ID used to resolve its resources. Eligibility follows
the audited native keep/keep-door gate, then the public SE `IsAIPlayer` query.
The permanent adapter validates resource and source-read provenance before editing
the actual installed SE method. Unsupported player flows disable new policies
with diagnostics while preserving the independent legacy fear adapter. Component
arithmetic, strongest penalty, religion bookkeeping, events and final clamping
remain in the original method. Reports and previews select the viewed player's
same policy; no native pointers escape the capture. There are no new locks,
allocations or roster searches in calculation reads. Gameplay and Mono rendering
still require in-game acceptance after the installed-method and behavior fixtures.

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

## Situational sound rules

Requires APIShared 0.8.0 or later. Register through `SoundEvents` after managed
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

### Unit speech replacement

Use the separate `APIShared.Audio.UnitVoiceEvents` for troop/civilian voices,
recruitment and special actions. Register on any thread, retain the process-owned
handle and prepare private mod/map resources outside callbacks. Pre/Post run on
Unity. Source/player/bank/situation selections filter each registration; request
gain remains separate from game speech-volume controls. Inspect availability
and nullable source evidence before applying a policy. See the authoritative
[unit-voice guide](AUDIO.md#unit-voices) and
[consumer example](../examples/ThirdPartyMod/UnitVoiceExample.cs).

## General in-game tooltip extensions

Use `APIShared.Presentation.TooltipPresentation.TryRegisterText` for literal strings,
or `TryRegisterContent` for immutable text/image segments. Registration is owner-bound
and Unity-thread-only. The [tooltip guide](TOOLTIPS.md) defines targets, semantic
placements, ordering, inactive contexts, lifetime and migration. The
[standalone example](../examples/ThirdPartyMod/TooltipExample.cs) demonstrates both paths.

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

## Multiplayer map lookup and lobby system hints

These runtime APIs are in APIShared.dll and require game references. They are
independent of the Content assembly's map/Trail container reader. Check each
service's `IsAvailable` after LibraryLoaded before enabling a dependent feature.
Registrations can be queued earlier; their success alone does not prove availability.
Register once with your own mod GUID and a stable owner-local ID. Providers,
observers and rules remain registered until process exit; return null or false
while inactive. No hook teardown or unregister is exposed.

`APIShared.Maps.MultiplayerMapLookup.TryRegisterProvider(owner, id, callback, out diagnostic)`
receives an immutable `MultiplayerMapLookupRequest` containing `FileName` and `Crc`.
Return a borrowed `FileHeader` only for the current verified content context, with
matching name/CRC and a nonempty local path. Keep the supplied header valid while
that context can be queried; do not mutate a header while another caller uses it.
The service changes no global catalog and performs no file or package validation.
The provider retains active-lobby identity, mission selection, content integrity,
multiplayer authority and activation policy. CRC matching alone is insufficient
proof of package integrity. Comparison preserves Vanilla's name/CRC conventions.

Callbacks execute synchronously on the validated Unity thread, in ordinal owner/ID
order and outside registration locks. Other threads and recursive queries use the
original lookup. With no claim, Vanilla runs once, with unchanged exception behavior.
Distinct claimed header instances produce `Conflict` and a null header instead of
choosing by registration order; the same header instance can be shared. Invalid
proposals and callback failures are logged and isolated. Result observers registered
through `TryRegisterObserver` receive `Vanilla`, `Resolved` or `Conflict`, the query,
a borrowed header (possibly null) and a technical diagnostic. There is no replay.
Registrations added during a query participate in the next query.

Use `APIShared.Lobby.LobbyChatPresentation.TryRegisterSystemMessageRule(owner, id,
rule, out diagnostic)` for recognizable local lobby hints. The predicate receives
immutable name, text and original system classification; return true to OR the
system-message flag. That retains the hint when ordinary chat is muted. It does
not send a message, change its text or render it twice. Existing system messages
remain system messages; unrelated chat retains Vanilla behavior. Rules have the
same thread, ordering, error isolation and permanent-lifetime contract. Message
text and display name are not authenticated gameplay authority.

Vanilla truncates each displayed lobby message at 300 characters. Consumers must
split longer support reports before sending them and prevent receive/send loops.

`APIShared.Lobby.LobbyLaunchStatus` explains why a multiplayer lobby cannot start yet.
For blocking Ready/Start and prepared multiplayer-save admission, `LobbyLaunchGuards` supplies a context whose
`RequiresPeerAgreement` distinguishes the exact Vanilla local Play/Coop branches
from fresh network lobbies. Prepared saves run the same guards after settings and
fingerprint validation at host release, client acknowledgement and final start;
their `HostLoadGame` context has a null lobby receiver, and only a freshly validated
solo-host roster bypasses peer agreement. In-game MP flags are not lobby evidence. See the
[launch-guard contract](WORKER_PRODUCTION_BONUS.md#mission-and-multiplayer-responsibility).
Mods that enforce their own Ready/Start checks register a provider with
`TryRegisterProvider(owner, id, provider, out diagnostic)`. The provider receives a
`LobbyLaunchStatusQuery` (lobby ID, host role and the Vanilla view, valid only during
the call) and returns the current `LobbyLaunchStatusItem` entries: localized `Text`,
`Kind`, a log-only `Detail` and a `Scope` for lobby members. Use `Loading` for expected transitional states, such as
a joining player still loading the selected mission; use `Blocking` for conditions
that require action; use `Waiting` for an obvious open-ended wait, such as a missing
second player, which is never announced but suppresses the "ready" message while it
lasts. Call `RequestRefresh()` from any thread when a provider input
changes, and `ReportAttempt(owner, operation, item)` right after rejecting a
Ready/Start command (calls from other threads are marshalled to the Unity thread). The service never blocks a command itself.

The host is the only sender of automatic announcements for the whole lobby, so its
provider should report every player's state (for example, which players still load).
On a lobby member, `LobbyLaunchStatusScope.Local` entries (the default) are only shown
in the member's own lobby chat as Vanilla system hints, without network traffic and
also in muted chat. Mark an entry `LobbyLaunchStatusScope.Lobby` when it describes a
local condition the host cannot detect, such as a missing file or a required restart;
it is then sent to everyone. A line another player sent within the last five seconds
is not sent or shown again, so several members with the same problem in the same
language produce one line. In a Coop lobby, whose chat Vanilla keeps collapsed, a
received or locally shown announcement opens the chat like Vanilla's chat button;
ordinary chat messages keep Vanilla's behavior.

Providers run synchronously on the Unity thread in ordinal owner/ID order, only in
an active Steam multiplayer lobby outside a mission; failures are isolated and
logged. A loading phase is announced once as a neutral wait hint after it has lasted
1.5 seconds, so short synchronizations such as browsing missions stay silent;
each loading entry that persists for 20 seconds is announced once as blocking. Blocking entries are announced when they first appear;
an unchanged or shrinking set sends nothing, a reappearing entry is announced again.
After any host announcement, one "ready" message follows once no entry has remained
for 1.5 seconds; lobby members never announce "ready". Every rejected command is sent
to the lobby by the rejecting player immediately, so a click that did nothing is
always explained; it is not repeated by the next refresh (neither as block nor as
wait hint) and does not arm "ready" by itself. Messages are split into numbered parts of at most
280 characters at word boundaries, start with a fixed marker recognized by
`IsAnnouncement`, and are classified as lobby system hints on every receiver with
APIShared. Logs record loading as info, automatic blocks as warnings and rejected
commands as errors, each with the complete detail. Text is sent in the sender's
language and conveys no gameplay authority.

## End-screen presentation

`APIShared.Presentation.MissionOverEvents` coordinates three managed boundaries:
`HUD_MissionOver.ButtonClicked`, `GameData.Scenarios.setGameOverState`, and only
`OST_MP_GAME_OVER` entries of `OnScreenText.addOSTEntry`. This is presentation,
not an authoritative defeat or mission lifecycle signal. Use the existing defeat
and lifecycle services for those purposes.

Register on Unity startup with an owner GUID and unique ID per operation. Hooks
are installed on first demand and retained for the process lifetime. Without
observers the original path remains unchanged. Callbacks run synchronously on the
caller thread, outside registration locks, ordered by order/owner/ID. UI dispatch
is the consumer's responsibility. There is no replay. Calls nested inside
notifications bypass notifications.

Pre may change `Result` for state/text, set private `State`, veto, or select button
deferral. All other inputs remain unchanged. A failing Pre rolls back its own
changes; successful vetoes are sticky. Accepted runs after all Pre only without
veto; Post receives immutable original/final inputs, owner-private State, veto,
deferral, synchronous completion and error information. Callback failures are
isolated.

For a preview close, veto the button original in Pre and close the owned preview
in the matching Post after checking its generation. This also suppresses any
selected lobby deferral regardless of registration order. Do not call the actual
game-over state method to open a preview. Accepted positive state transitions let
a preview revoke ownership before Vanilla reuses the view.

`DeferOriginal(handler, isCurrent)` is available only for buttons. Supply a
side-effect-free guard covering both session and HUD identity/generation. The
last successful Pre selection wins. Its handler receives a continuation that may
be retained. `TryInvoke()` runs only on the original caller thread, consumes an
invalid context, and never retries an original, including one that throws. A
wrong-thread attempt returns false without consuming it. Guard failures reject
execution; handler failures attempt the guarded continuation once. Original
exceptions propagate. Post completes when dispatch returns; it does not promise
that a retained continuation has run. Consumers must guard their own delayed
side effects as well as the continuation. Replaced views must never be restored
from a stale preview snapshot.

The service does not select a mod, decide who won, change player identity, grant
spectator rights, return to a lobby or read consumer settings. Existing mods that
do not register remain on their existing paths; consumers using this addition
must require the APIShared package that supplies it.

## Shared render and pause-menu presentation

Requires APIShared 0.18.0 or later. These are opt-in services; consumers need no
knowledge of one another.

`VisibleMapRenderEvents.TryRegister(owner, id, before, finallyCallback, out registration,
out reason, order)` installs the shared visible-map entry only after APIShared's
native-library initialization. Registrations start disabled. `SetEnabled` changes
native demand data; executable hooks remain installed. With no enabled observer,
the native guard jumps straight to the original trampoline without a CLR transition.
Callbacks run synchronously on the native render caller thread, ordered by order,
owner and ID; no UI dispatch, replay or cancellation occurs. The visible bounds and zoom are immutable; the borrowed native map pointer
stays internal. All six original native inputs are forwarded unchanged. Each registration gets its own invocation `State`; set rollback state
before changing temporary data. Cleanup runs in reverse order, even if preparation
throws or the registration is disabled during the call. Callback exceptions are
isolated and diagnosed once per registration. The original receives unchanged
parameters exactly once; its exception is not swallowed and cleanup still runs.
Recursive render calls get distinct state. Concurrent calls require consumer-owned
synchronization for any shared mutable state. Unsupported images or occupied entry
contracts reject registration without taking ownership of another hook.

### ESC menu rows and additional actions

`IngameMenuActions` is the single owner of ESC menu positions. Register and query
on the initialized Unity thread, using your mod GUID and a stable owner-local ID.
Providers and commands execute on Unity; consumers retain gameplay/session/MP
permission checks. Registrations and hooks last until process exit. Return null
or an invisible `IngameMenuActionState` to hide an action and release its position.
A visible disabled action still occupies its position.

Register with explicit `IngameMenuActionOptions`; the output handle invalidates the action.

**Breaking change in 0.21.0:** the former options-less overload
`TryRegister(ownerGuid, id, state, execute, out reason, order)` and the empty XAML resource
key `APISharedMenuActionButtonStyle` were removed. Binaries compiled against them fail with
`MissingMethodException`/an unresolved resource. Migrate to the overload below; for the
former automatic QuitMission/Left placement pass
`new IngameMenuActionOptions(IngameMenuRow.QuitMission, IngameMenuSlot.Left)`, or use the
additional page only. Placement conflicts are not resolved implicitly.


```csharp
IngameMenuActionRegistration menuAction;
var slot = IngameMenuActions.GetRowState(IngameMenuRow.QuitMission).Left;
// Claims includes hidden registrations; Current is the last-observed visible winner.
// This query neither invokes providers nor reserves the position.
if (!IngameMenuActions.TryRegister(MyGuid, "inspect", GetMenuState, OpenInspector,
    new IngameMenuActionOptions(IngameMenuRow.QuitMission, IngameMenuSlot.Left),
    out menuAction, out string reason))
    Log(reason);

// Call from an actual settings/session/state-change event, never a timer or frame.
menuAction?.Invalidate();
```

`IngameMenuActions.TryRegisterContent(ownerGuid, id, state, createContent, options,
out registration, out reason, order)` uses the same permanent registration,
placement/conflict and invalidation contracts for an inline `Noesis.Control` instead
of a generated button. Register on Unity. The factory receives an
`IngameMenuContentContext`: its `Menu` resolves resources for that view, and
`TryExecute(Action)` synchronously revalidates the current menu, visible/enabled
state and effective main/additional-page placement before executing a control action.
Use it for every committed edit; retain your own mission/authority checks. It does
not dispatch or replay work. A stale view or failed callback returns false.

Return a fresh unattached control for each menu view. The factory runs once when
first visible in that view; a factory failure hides only that registration until a
new view is created, revealing preceding slot claims. The broker preserves both
the control and its attachment on ordinary invalidations when placement is unchanged,
so text drafts and keyboard focus survive. Actual layout/page moves can change
focus; the same control moves to the additional page rather than being duplicated.
A replacement menu receives a new control and invalidates old execution contexts.
The consumer owns children, binding and keyboard behavior. The broker owns root
width, margin, enabled state and the optional `state.Tooltip`, scaled from the root
control's `FontSize`. `state.Text` labels diagnostics/occupancy rather than replacing
content. Existing button APIs, Vanilla objects and commands are unchanged.
See `RegisterInlineEditor` in the [menu example](../examples/ThirdPartyMod/IngameMenuExample.cs).

Rows have stable `IngameMenuRow` names independent of mode-specific visibility.
Each has Left and Right; Vanilla is the Right fallback. A single visible button
uses the normal full width; two share a row. A mod can activate a position even
when its Vanilla row is hidden in the current mode. Right requires explicit
`replaceVanilla: true`. Competing registered mod claims, including hidden claims,
reject registration unless `replaceExisting: true` is specified. Registration
checks and accepts the claim together; a preceding occupancy query is advisory.

Buttons use Vanilla's unchanged `BTN_SH_GlowSDisable` style: a centered single
button is 300 layout units wide; a pair uses 181.25 each with a gap of 5 in a
367.5-wide row. Fonts, texture heights and state effects retain Vanilla geometry.
Long labels use an ellipsis without wrapping or reducing the font; truncation
does not automatically create a tooltip. The main menu keeps Vanilla's initialized height
unless an additional visible row needs space; scrolling is bounded in local UI units.
ESC tooltip text matches the button's rendered font size even when Noesis renders
the popup in a separately scaled view. Width, padding and borders follow the same
pixel ratio, with at most 420 menu units of width and a viewport bound. Only ESC
popups use this view-owned sizing; other tooltip resolution presets are unchanged.
Sizing runs on opening and layout events, without evaluating action providers.

Tooltips are opt-in through `new IngameMenuActionState(text, visible, enabled, tooltip)`.
The original three-argument constructor remains binary/source compatible and supplies
no tooltip. Null, empty or whitespace-only tooltip text means no popup; nonempty text
is shown exactly as supplied, without appending the button label or mod GUID. Supply
localized text and notify ordinary `Invalidate()` when it changes. Removing a tooltip
closes and detaches the owned popup. APIShared generates no label/owner tooltips. Vanilla tooltip objects, bindings and settings are
untouched, including on the extra page; APIShared navigation buttons have no tooltip.

The latest accepted explicit takeover wins among visible claims. Refreshing an
older claim does not raise its priority. Hiding the winner reveals the preceding
visible claim, ultimately Vanilla on Right. Takeover does not delete registrations.
`GetRowState(row)` returns immutable Left/Right snapshots, their Current occupant
and Claims in precedence order, including owner GUID/ID, Vanilla identity, cached
state and effective ownership. Before first evaluation HasState can be false;
closed-menu snapshots can be stale. Queries do not read native state or call mods.

`new IngameMenuActionOptions()` registers exclusively for the additional-actions
page. Every visible displaced mod or Vanilla action also appears there, once.
Entries sort by order, ordinal owner GUID, then action ID. Invisible actions take
no space; disabled actions remain listed. The extra-page button appears before
Resume only while the page has entries. Both pages support bounded scrolling and
two buttons per row, with a full-width final single button. Back returns to the
main ESC page. Escape closes the entire menu using Vanilla's existing close/pause
path; reopening starts on the main page. Vanilla buttons retain their actual
objects, bindings, commands and confirmation dialogs when moved to the extra page.

Providers are reevaluated on menu opening (including return from a confirmation),
explicit invalidation while open, and command validation. A handle invalidates its
action; `IngameMenuActions.Invalidate(ownerGuid)` invalidates all that owner's
registrations. Requests from other threads are posted and
coalesced on the validated Unity dispatcher. Closed-menu invalidations defer
provider work until opening. Do not issue invalidations on recurring ticks/frames.
Invalidating from inside a provider defers that demand until the next external
event, avoiding self-recursive refresh. Providers should be side-effect free.
A provider exception hides only that action and is reported once; subsequent
invalidation can retry it. Commands recheck visibility, enabled state, view identity
and effective placement; exceptions are logged once and commands are not retried.
A command opening another view owns whether to keep the pause menu, hide it for
a confirmation, or call `HUDIngameMenu.Close()` to restore the previous pause state.

`apiui:SharedToolTipSizing.Enabled="True"` (namespace `APIShared.ModSettings`)
opts an owned `ToolTip` into the common resolution presets: font sizes 23/30/45,
width, padding and border thickness. Values are applied on the Unity thread at
attachment and each opening. Its template must bind those properties. Use this
scaler alone in the tooltip style, without a competing SE scaler setter. The
behavior temporarily disables SE scaling for that tooltip and restores its prior
opt-in when detached. It does not choose a template, colors or duration; consumers
retain those. Unopted tooltips and Vanilla rollover text are unchanged.

## Lord statistic columns

Requires APIShared 0.19.0 or later. Register through `APIShared.Statistics.LordStatistics.TryRegister` on
Unity's main thread after APIShared initialization. Use your own mod GUID and a stable owner-local ID.
Duplicate identities are rejected with a reason. The persisted key is escaped GUID + `/` + escaped ID;
never persist catalog positions or translated labels. The
[example](../examples/ThirdPartyMod/StatisticsExample.cs) compiles against the public DLL only.

A definition supplies localized name, tooltip, a shareable `Noesis.ImageSource` provider and a batch
values provider. Optional themes are Economy, Military and PopulationMood; the default OtherMods
appears under “Other mods”. Use `decimal?` for numeric values; null/missing means unknown.
Optional formatting can add signs, precision or units; renderers sort the underlying decimal,
not the formatted text. Provider exceptions are isolated to the column, with bounded diagnostics
and a fallback for metadata/formatting. Later requests can recover. Results are copied and filtered.

The registry owns no table or data source. A separate renderer (initially LordOverviewTest) owns
sampling cadence, selected columns, UI and information rights. Without a renderer registrations
remain idle. A renderer subscribes `CatalogChanged`, then reads `GetCatalog` on the same Unity
thread. Catalog operations never call value providers. Catalog callbacks are synchronous, ordered
by revision, isolated and outside registry locks; reentrant changes queue later revisions. There is
no implicit replay. Callbacks and registration handles remain process-lived; deactivate through
`SetActive`, and use `RefreshMetadata` for changed localized metadata. Do not remove persistent
registrations during SHCDE's early plugin destruction.

Only visible, selected columns should be read, using the current positive APIShared mission session
and the authorized subset of player IDs. Treat custom columns as extended information by default.
Pass no restricted IDs; never expose restricted values through tooltips or sorting. An empty roster,
inactive provider, invalid session or recursive read does not call the provider. Callbacks execute
on Unity's main thread outside registry locks, not under the renderer's engine lock. Providers acquire
their appropriate native read lock and return a fresh snapshot, without mutating the game or HUD.
The renderer revalidates session/activation after each callback. Retain missing selections as
removable unknown entries; reconnect when the same key is available again.

### Source-relative popup sizing

For an owned custom popup with a template that binds FontSize, MaxWidth, Padding and BorderThickness:

```csharp
SharedToolTipSizing.SetReferenceElement(tip, sourceHudElement);
SharedToolTipSizing.SetReferenceFontSize(tip, 16f); // source HUD logical font size
SharedToolTipSizing.SetEnabled(tip, true);
ToolTipService.SetShowDuration(ownerElement, 60000);
```

The source-reference mode uses measured source/popup screen transforms, including separate Noesis
views. Sizes are absolute, updated on opening/loading and relevant size events, without frame polling.
A popup that is not attached waits for its view. Closed popups release source/viewport listeners.
Enabling takes exclusive sizing ownership from the Extender scaler; disabling restores its prior
setting. A null reference retains the existing 23/30/45 resolution presets for existing consumers.
The caller owns the dark/gold, white wrapped-text template and closes its popups when its HUD ends.

## Lord message replacement

Requires APIShared 0.47.0 or later. Register `LordMessageEvents` on Unity after
readiness, retain the registration and use `SetEnabled` for activation. Each of
audio, video and text can remain unchanged or select its own typed replacement.
Use `VanillaLordMessages.TryGet` for an existing Lord/category reference; private
mod resources and already-localized text do not require a global asset override.
The service changes actual presentation after native queueing and retains the
existing `PackagedLordPresentation` contract. See [Lord message replacement](LORD_MESSAGES.md)
for identity, ordering, callback rollback, resource preparation, coverage and
submission limits, and the [consumer example](../examples/ThirdPartyMod/LordMessageExample.cs).

## Packaged Lord presentation

Requires APIShared 0.20.0 or later.

Register with `PackagedLordPresentation.TryRegister(ownerGuid, registrationId, out registration, out reason)` on Unity after managed initialization. Publish immutable PackagedLordPresentationEntry values with registration.SetEntries. Entries bind one-based player IDs, current internal Lord identities, authored fallback names, complete lordmeta.json and an optional private asset-provider GUID. Clear with SetEntries(null) at session end, on abandonment and before replacing an unrelated selection. Hooks and registrations remain installed until process exit.

The consumer authenticates the selected package and all media paths before publication. Text/message lookups perform no filesystem access or callbacks; portraits use the Unity asset resolver and are cached on demand. Different claims never select an arbitrary owner. Localized fields fall back independently to English; missing titles in an explicit metadata document are empty. Complete original JSON remains available, including fields unknown to this API. Script Extender's global Lord catalog and Lua availability are unchanged. The adapter also fills Coop multiplayer restart names before encoding the same object that later supplies the HUD and save data; ordinary multiplayer keeps its original path.

Passing null metadata preserves the authored name while runtime full-name lookups obtain the nickname suffix from Vanilla's existing `text.txt` / localized generic-title fallback. The current internal name can identify a peer-local media donor and never replaces the authored name. Invalid nickname subtypes retain the authored name without a suffix. Direct SE metadata-title queries report no title for these entries; the original custom-text path remains available. Null and an explicit `{}` are different claims for full-name and title resolution. Non-title lookups retain their previous equivalence: otherwise identical entries with null and `{}` metadata still share their empty media claim, regardless of slot, owner or publication order. Such a title-policy conflict reports no unambiguous metadata title and retains the original custom-text fallback; it must not consult peer-local SE titles or emit registration-miss errors. The public `MetadataJson` property still returns `{}` for null input, and the pure `ResolveName` / `ResolveTitle` methods keep their metadata-only behavior; persist the original nullable metadata separately when that distinction must survive a roundtrip. This title fallback does not opt into local speech metadata, alter media claims or consume synchronized random numbers.

Omitting the provider GUID means metadata-only presentation. Set `allowGlobalMedia: true` only for an authenticated installed source using the Extender's legacy global asset mode; null alone must never opt into global file lookup. Missing optional media does not change synchronized message-clip selection. Asset providers must contain only verified presentation files and must not impersonate the source mod or register its scripts.

The consumer must revalidate the player slot against the current selection before starting. Coop restart names use the claimed entry's current internal identity, which may be a verified peer-local alias of the host's selected package. This does not authorize a different gameplay selection or configuration.

```csharp
if (PackagedLordPresentation.TryRegister(pluginGuid, "mission-lords", out var presentation, out var reason))
    presentation.SetEntries(new[] {
        new PackagedLordPresentationEntry(3, selectedInternalName, authoredFallbackName, completeLordmetaJson)
    });
// Retain the registration and call presentation.SetEntries(null) when leaving this selection.
```

## Mapper selection camera

`APIShared.Presentation.MapperSelectionCameraEvents` owns the managed
`EngineInterface.StartMapperItem(int)` boundary and the narrowly filtered native
family-building camera call. Register from Unity after native initialization, for
example in `ApiShared.WhenReady` on its startup callback. A late worker-thread
readiness callback is not a Unity dispatcher. `TryRegister(ownerGuid, id, pre,
post, out reason, order)` returns false when the audited native contract,
initialization or unique registration identity is unavailable.

Pre receives immutable `Mapper` and `SessionId`. Its only camera decision is
`SuppressCamera`; it cannot cancel selection, rewrite the mapper/player, or change
the result. Successful requests combine with OR. Pre also has registration-private
`State`. A throwing Pre loses its suppression request, while preserving its State
for its matching cleanup Post. Pre arguments freeze when that callback ends.

Callbacks run synchronously on Unity under the existing engine monitor, outside
registration locks. Pre order is ascending order/owner/ID; Post order is reversed.
An immutable registration snapshot serves the whole invocation; registrations made
inside a callback join the next invocation. No replay or deregistration is supplied.
Each registration's errors are isolated and logged once. Callbacks must not wait
for another thread that needs the engine monitor.

The original managed selection runs exactly once. Post always follows entered Pre
callbacks, including original exceptions, after this invocation's native demand
has been restored. Post supplies `Mapper`, `SessionId`, private `State`, original
`Result`, original `Exception`, and `SuppressionActive`. Result is -1 on an original
exception, which is rethrown unchanged after Post. `SuppressionActive` means the
scope was armed, not that a native camera request occurred or selection succeeded.
Nested originals have independent scopes. Commands issued from notification
callbacks bypass notification and suppression, then restore any enclosing demand.
Session/world invalidation prevents arming or restoring stale demand.

The native bridge omits only mapper selection's automatic centering. All other
camera callers retain the complete original path. Native view, control identity,
resource/footprint checks and placement ownership are unchanged. Consumers retain
their gameplay-mode and permission policy. For an optional replacement jump, keep
the selected owner/building identity in State, then revalidate scene, session,
world, owner, alive/type/global ID and tile in a successful Post before making an
immediate Unity camera change. Missing or stale targets should keep the camera
stationary; do not clear unrelated camera reports or enqueue stale cross-mission
camera work. This broker does not select a building or move the camera itself.


## Ally gifts and send-dialog extensions

Use [AllyGoodsPanelExtensions](ALLY_GOODS_PANEL.md) to add a section or noninteractive per-ware icon markers without replacing existing controls or owning another mod's layout. Marker factories create durable controls per ware and host; update their Visibility on Unity and request rearrangement through the registration. Read the current selection again when executing a UI command. The compiled `AllyGoodsExample` demonstrates owner-local section registration.

Use [AllyTransfers](ALLY_TRANSFERS.md) for immediate gifts only within the shared synchronous simulation context. Authenticate rule edits through the existing Chore channel and schedule matching deterministic decisions with native simulation ticks. Net transfer observations, source bounds and capacity are separate from UI command completion.

<a id="fixed-worker-production-bonus"></a>

## Worker production modifiers

For actual-output base quantities, fixed percentages and [per-level fear contributions](WORKER_PRODUCTION_BONUS.md#fear-levels), use the [worker production service](WORKER_PRODUCTION_BONUS.md). Register a process-held desired profile, inspect and present its typed status, and let shared mission admission activate the resolved rules. The channels use staged factors or fixed values. The legacy absolute-percentage facade remains supported. Consumer mods own settings and localization. APIShared owns mode-profile evaluation, exact saved credits, zero-output completion, transport and rendering.

## Weapon workshop options

Register additional recipes with `WeaponProduction.TryRegister` during Unity
startup. Use `WithIngredientAmount` to set the cost per charge in the workshop's
native ingredient. The [weapon workshop guide](WEAPON_PRODUCTION.md) covers the
registration example, ingredient limits, Shift selection, AI, and requirements
for multiplayer and saved games.

## Huntable prey

Register further unit types for Vanilla Hunters with `HuntablePrey.TryRegister`
during Unity startup on every peer, and withdraw or restore them logically with
`TrySetEnabled` before or after a mission, driven by synchronized settings.
`HuntablePrey.Current` lists the types Hunters may hunt. The
[huntable-prey guide](HUNTING.md) covers priorities, unsupported types, mission
freezing, multiplayer agreement and what stays Vanilla behavior.

## Herds

Read and spawn Vanilla's animal herds with `Herds`, and register `HerdRouteEvents` handlers or
`HerdFlightRules` during Unity startup on every peer to keep herds in an area or calm. Decide
from simulation data and synchronized settings only; set calm marks and spawn herds inside a
`SimulationTickEvents` callback. The [herd guide](HERDS.md) covers threads, the editor and the
limits.

## Gatehouse geometry and closing range

`APIShared.Buildings.BuildingGeometry.TryGetSquareFootprintCenter` accepts the
minimum tile coordinates and side length of a square footprint. It returns the
center in native world coordinates, with eight units per tile. Tile boundaries
lie at multiples of eight and tile centers are four units farther along each
axis. An odd side length puts the result at the center of the middle tile. An
even side length puts it on the boundary between the middle tiles. This
calculation does not require a live map.

Within a synchronous gatehouse query, use
`APIShared.Buildings.GatehouseClosingRules.IsDefinitelyOutsideRange` with the
live gate and unit records. The method uses the current native distance origin
and the larger of the human and AI thresholds. True allows a consumer to skip
additional checks. False requires the usual evaluation, including when the
native rule could not be validated. Keep the rule stable during the query and
use the timing and origin capabilities to make configuration changes outside it.
