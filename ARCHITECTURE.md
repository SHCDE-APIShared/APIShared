# Architecture

APIShared is one process-wide BepInEx service library. Assembly identity `APIShared`
and plugin GUID `APIShared_Serp` are stable. Its public entry point binds a caller's
GUID without installing hooks or reserving a capability.

## Responsibilities

The separate `bootstrap` project implements the development-only
[early dependency correction](docs/DEPENDENCY_BOOTSTRAP.md). It does not reference
the main APIShared assembly; it binds the selected runtime by a validated package
pair and owns its hooks for process lifetime. It is outside the runtime source glob. Its `src/EarlyEntry` contains the minimal
listener entry, `src/DependencyCorrection` keeps provider policy and its hook owner
together, `src/Package` owns pair validation, and `src/Diagnostics` owns tracing.
Package/build tools remain under `tools`; isolated tests live under
`tests/DependencyBootstrap.Tests`. This assembly boundary is required by its earlier
load phase; it is not a second public APIShared service runtime.

| Area | Purpose |
|---|---|
| Core | Initialization, capability diagnostics, ownership, native infrastructure and the public native-site registry |
| Compatibility | Internal, binary-validated interoperability corrections for optional mods; no public capability |
| ScriptExtenderFixes | Binary-validated corrections for Script Extender and bundled dependencies |
| ScriptExtenderReplacements | Opt-in public alternatives to specific SE methods; no interception of existing callers |
| Missions, Lobby, Players | Shared observations and immutable state notifications |
| Presentation, Buildings | HUD/briefing extensions, building repair and gatehouse services |
| GameModes | Context capture, registered launch evidence and optional caller-defined permission profiles |
| ModSettings, Savegames | Settings UI integration, presets, convergence and persistence |
| Networking | Owner-bound Chore channels, control-plane messages and presentation dispatch |
| Units, Pathfinding, Diagnostics | Command interception, recruitment policies, unit settlement, queries and advanced integrations |
| Audio | Native-record sound presentation, audited source contexts and shared rule events |
| Economy | Shared native market price query events and validated hook ownership |
| Workshop | Steam publishing, receipt-based plans and separate post-exit transactions |

Directories help navigation; public namespaces define the contracts. Capabilities
use `APIShared`; settings and general profiles use `APIShared.ModSettings` and
`APIShared.GameModes`. Caller-defined profiles never constrain an unrelated mod.
Static interception brokers use `APIShared.Commands`, `APIShared.Presentation`,
`APIShared.Recruitment` and `APIShared.Economy`; their shared Pre base contract uses
`APIShared.Events`. These brokers complement the capability entry points.
The unit-settled observation broker uses `APIShared.Units`.
The formations command engine, work-target policy and MoatMove orchestration
belong to BugfixesAndQoL and its addon. APIShared supplies the targeted public
route-search preference events used by that publisher and third-party observers.

## Finding and extending a feature

Start with the feature's subject, then its public contract. Keep implementation and
hooks beside that feature; a hook is an implementation mechanism, not a separate
product area. General native mechanisms live in `Core/Internal/Native`.

```text
src/
  Core/                         entry point, initialization, capability ownership
    Events/                     shared interception registry and managed hook infrastructure
    Internal/Native/            pattern resolution, memory, native reservations
  ScriptExtenderFixes/           audited Extender/dependency corrections and permanent managed hooks
  ScriptExtenderReplacements/    explicit public replacements and their migration documentation
  Audio/                        shared playback events, context capture and binary contracts
  Economy/                      native market query contracts and validated owner
  Missions/                     mission contracts, state and event relay
  Networking/                   owner-bound Chore and control-plane packet channels
  Workshop/                     package contracts, file policy, Steam callbacks and UI
  Buildings/
    Repair/                     repair contracts and service
    Gatehouse/                  timing, origin and drawbridge coordination
  Presentation/
    Events/                     named HUD Pre/Post publishers
    BuildingTooltip/            supplementary building-tooltip text and shared rendering
    UnitHud/                    HUD contracts and implementation parts
    HudExtras/                  independent side-HUD buttons, layout and rendering
    BriefingGold/               briefing gold contracts and service
  ModSettings/
    Presets/                    contracts, host adapter, controller, catalog and storage
    Lobby/                      per-player synchronization and convergence
    UI/                         search and tooltip presentation
    Internal/                   JSON parser and atomic file replacement
  Units/
    Commands/                   managed GameAction Pre/Accepted/Post contracts
    Recruitment/                request arithmetic and shared material-display IL policy
    Settled/                    unit-settled contracts, settle tracker and SE subscription owner
  Pathfinding/
    Assassin/                   assassin route and attack integrations
    GateRoutes/                 shared gate/bridge policy bridges
    Moat/                       elevated moat AI state
    Routes/                     public Pre/Post route-search preference contracts
```

Other small subjects (`Units`, `Players`, `Lobby`, `GameModes`, `Savegames`,
`Diagnostics`) remain shallow. Add subdivisions when distinct
responsibilities actually exist, rather than creating empty template directories.
Folder paths do not change C# namespaces or public API identity.

## Deciding what belongs here

APIShared coordinates services that multiple mods need to share in one process:
hook ownership, observations, registrations, presentation contributions and settings
protocols. A feature does not belong here merely because it needs a native hook.
Mod-specific activation rules, localized explanations and author preferences belong
to the consumer. Consumers configure shared services through explicit contracts;
general services should not infer these decisions from known plugin GUIDs.

Native market query ownership belongs to Economy/MarketPriceEvents; consumer AI
settings and player classification remain in the consumer. The two native helpers
serve both AI trade and ally valuations. Their fixed-image ABI and backend contract
are validated before publication; their hooks remain installed for the process.

Customized launch owners register typed snapshot providers in `CustomizedLaunchOrigins`.
The registry combines evidence without naming or reflecting over consumer assemblies.
Each consumer owns its launch state and save protocol; APIShared owns validation and
conflict handling when capturing the common mission context.

Consumers may retain a `GameplayModeGate` and feed it their lifecycle notifications.
The optional settings-mode presentation shares their profiles, uses stable local row
keys and adds a background without changing editing authority or persistence.
No author-specific mode tables or automatic lifecycle ordering are introduced.

Separate components by responsibility and state, not just file length. Partial
files can make one coherent hook owner easier to read, but do not create independent
components. Keep implementation tests with the implementation so a contributor can
change shared behavior without obtaining a separate consumer workspace. Consumer
tests should exercise how that mod uses the shared service.

For a new service:

1. Put caller-facing options, results and registration interfaces in a named
   `FeatureContracts.cs` beside that subject's service. Reuse existing services
   before introducing another public contract.
2. Keep ownership, registrations and availability in the service/capability.
   Separate substantial behavior into named implementation files. For example,
   the HUD service has `Registrations`, `Categories`, `Recruitment`,
   `DetailsAndArmy`, `Images` and `Hooks` parts.
3. Give a hook or closely coupled hook group a descriptive file. Keep independent
   interception and validation responsibilities visible in the feature directory.
   Put its native validation next to it or in a dedicated `NativeContracts` part.
   A shared interception point has one owner and dispatches to its features;
   features must not independently install competing hooks on that address.
4. Connect the service through `ApiSharedRuntime` and the public entry point only
   when it is generally useful. Document it in the API catalog, add an example
   where useful, and test observable behavior in the appropriate suite.

Partial classes here divide one existing service, not independent runtimes.
`UnitHudPresentationService` owns the shared state across its named parts. Initialization and published-hook lifetime stay centralized. Put a new
operation in its feature part; do not grow the state or initialization file with
unrelated algorithms. A small coherent feature can stay in one file.

Command dispatch, native selectors, formation planning and marker rendering belong
to their consumer mod. APIShared does not contain the engine or its handler contract.
Its shared pathfinding bridges and public route-search events coordinate only the
documented cross-mod integration points.

The AI construction diagnostic hooks, snapshots and disposable-save probes are owned
by their diagnostic mod. Observation points inside BugfixesAndQoL connect to that
mod through a small internal sink; they do not create a public APIShared service.
The generally useful AIV build-step observer remains here.

Unused services must exit before collecting observations or constructing event
payloads. AIV dispatch reads an immutable registration snapshot and calls Vanilla
directly when it is empty. Lobby capture starts with the first observer; defeat
observation starts with the first subscriber's simulation-tick baseline. Marked
selection tracking starts with an observer or a successful cache request.
Unit settlement subscribes to Script Extender events at its first registration.
Dormant internal HUD subscriptions do not create demand in shared event brokers.
Keep capturing lambdas in separate active-path methods: an early source-level
return alone does not prevent compiler-generated closure allocations.

Consumers own diagnostic analysis, histories and lookup-failure logging. UnitAccess
returns its failure reason without tracking call sites or logging automatically.
The compatible opt-in scroll diagnostic performs no formatting while disabled.
Readiness, validation and isolated callback error reporting remain service duties.

## Publication

Before managed publication, Awake attempts the optional
[Fixes wheat-sale correction](docs/FIXES_COMPATIBILITY.md) synchronously. This
one-time startup operation has no LibraryLoaded/tick retry or teardown path.

Additional weighted route calculations publish through `Pathfinding/Routes`.
`RouteSearchContracts` contains caller-facing arguments and results;
`RouteSearchEvents` owns process-lifetime registrations, stable ordering and callback
isolation. The command planner publishes Pre before its existing calculation and
Post after live-edge validation. Observers can change preference costs or skip the
additional search; they cannot broaden traversal permissions or replace native
publication. This shared extension stays separate from the mod's command algorithm.
Use Script Extender events for existing unit-order interception rather than
installing another hook. See the API catalog for skip, exception and thread rules.

`APISharedPlugin.Awake` publishes managed services. `CrusaderLibrary.LibraryLoaded`
initializes native services. `ApiSharedRuntime.ProcessInstance`, static registries
and persistent publishers retain services after startup cleanup. Global completion
and individual capability availability are separate; one native failure must not
disable independent managed services. Each `LibraryLoaded` step runs inside the error
boundary `ApiSharedRuntime.RunNativeInitialization`, so a throwing step cannot skip
later steps or leave the global state `Pending`.

Readiness callbacks run outside the initialization lock with exception isolation.
There is no implicit thread dispatch. Registration ownership, ordering, replay and
callback contracts are described in the [API catalog](docs/API_CATALOG.md).

## Settings and dependencies

Networking owns reusable packet submission and receive-context rules. Consumers
register through `ModApiClient.Network`; process-owned registrations retain channels
and Extender subscriptions. Chores execute synchronously in simulation context;
control-plane packets are copied before FIFO Unity dispatch. Packet schemas,
authority, session epochs, acknowledgements and gameplay handlers remain consumer
contracts. `ChoreTransport` adapts existing packet registrations without changing
wire IDs or registering an additional envelope type. It preflights the public
Extender send; its void return and internal Steam fallback are not upgraded into
an atomic delivery guarantee. No competing native transport hook is installed.

The preset view model owns UI bindings, commands and role/context presentation.
Its small `PresetHost` adapter implements the internal `ILobbyPresetHost` boundary:
live settings target, application backend, authority/context and completion callbacks.
`LobbyPresetController` owns the preset protocol independently of the ViewModel.
Its named parts handle the catalog, snapshots, transient missions and persistence;
they share one controller state and are not separate runtimes. Property setters still
belong to the participant, and the existing applying guard prevents reentrant partial saves.
`LobbyPresetStorage` owns binary file I/O, atomic replacement and corrupt-data
backups. `PresetAtomicFilePublisher` owns bounded publication retries and destination
replacement independently of lobby convergence; its tests run without the game.
The controller composes the existing envelope and determines which values
may be saved; storage receives the completed payload. Tests can exercise this protocol
through a plain settings host without constructing a Noesis page or subclassing the ViewModel.
Published preset contracts, JSON schema, file discovery and dialog row models live
in separate files. `ModSettingsWorkingSources` owns the optional source-provider
registry. Schema changes belong in `ModSettingsPresetJson`; provider discovery and
filesystem boundaries belong in `ModSettingsPresetCatalog`. Dialog models adapt
those contracts for XAML without owning file I/O or source selection.
Per-player state owns lobby convergence. Settings, presets and saved-game data keep
their existing formats. Internal JSON parsing and atomic publication are owned by
APIShared; no source links or automatic synchronization connect them to a mod workspace.

The compiled runtime uses the real installed game and Extender assemblies.
[Native compatibility](docs/NATIVE_COMPATIBILITY.md) describes native ownership and
update checks. Native catalogs and tests express supported contracts, not a universal
promise that an arbitrary game build is supported.

## Development

Explicit public SE replacements live in `src/ScriptExtenderReplacements`, with
[contracts and migration guidance](src/ScriptExtenderReplacements/DOCUMENTATION.md).
Their pathfinding-table state is validated during native initialization and
published before readiness. They do not belong to the automatic hook-based fixes
below and do not change existing SE callers.

APIShared also supplies narrowly scoped bugfixes for Script Extender and its bundled
dependencies. Their implementation lives in `src/ScriptExtenderFixes`, separate from
public capabilities and gameplay services. Fixes must validate the affected binaries
and method contracts, preserve foreign-mod callbacks, and fail closed independently
of other APIShared services. No consumer registration or author-specific mod is required.

The UU-ImGUI shutdown fix protects the audited 1.6.7 binaries bundled with
SHCDE-SE 2.14.1. The configuration and compatibility limits are documented in the
[integration guide](docs/THIRD_PARTY_GUIDE.md#script-extender-bugfixes). Permanent
hooks and native resources survive until process exit. When upstream fixes a defect,
re-audit the installed code before supporting that new binary or retiring the fix;
a version number or changelog entry alone is not proof of equivalent behavior.

The solution contains the runtime, two local integration suites, a game-independent
core suite and public consumer examples. Test-only packages never become plugin
dependencies. The standalone build uses no neighboring repositories. Additional
mod integration tests belong in their consumer repositories.

## Feature-specific guides

Audio owns shared playback interception, source attribution, native source events
and clip lifetimes. Consumers own their sound policies; native-context availability
is independent of managed playback. See [Audio](docs/AUDIO.md) for contracts, implementation and measurements.

Workshop owns publishing, receipt-based plans and post-exit transactions. Its
persistent staging integration cleans previous APIShared releases; foreign maps
remain Extender-owned. See [Workshop](docs/WORKSHOP.md) for workflows and implementation.

Versioned save data belongs to Savegames. APIShared owns the record format, the migration
order and the load window over Script Extender `ModSaveDataAPI`; consumers own their entries,
migration steps and when they apply loaded state. See [Save data](docs/SAVE_DATA.md).

Native-site registration belongs to Core. Shared services remain the way to avoid
hook conflicts; the registry supplements them as a safeguard and diagnostic for hooks
APIShared does not cover yet. APIShared owns the registry, its overlap rules and the
registration of its own native sites at installation; consumers own their spans,
modes and their reaction to a refusal. A refusal identifies a conflict without
resolving it, and a recurring conflict becomes a shared service. The registry is
cooperative and adds nothing to hooked paths. See [Native sites](docs/NATIVE_SITES.md).

Building tooltip contributions belong to Presentation/BuildingTooltip. APIShared
owns shared presentation and registration; consumers own localized text,
activation and gameplay decisions. See the [catalog](docs/API_CATALOG.md#building-tooltip-contributions)
for the public entry point and integration documentation.
