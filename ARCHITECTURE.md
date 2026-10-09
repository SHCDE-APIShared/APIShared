# Architecture

APIShared is one process-wide BepInEx service library. Assembly identity `APIShared`
and plugin GUID `APIShared_Serp` are stable. Its public entry point binds a caller's
GUID without installing hooks or reserving a capability.

## Responsibilities

| Area | Purpose |
|---|---|
| Core | Initialization, capability diagnostics, ownership and native infrastructure |
| Missions, Lobby, Players | Shared observations and immutable state notifications |
| Presentation, Buildings | HUD/briefing extensions, building repair and gatehouse services |
| GameModes | Context capture, registered launch evidence and optional caller-defined permission profiles |
| ModSettings, Savegames | Settings UI integration, presets, convergence and persistence |
| Units, Pathfinding, Diagnostics | Queries and advanced integrations |

Directories help navigation; public namespaces define the contracts. Capabilities
use `APIShared`; settings and general profiles use `APIShared.ModSettings` and
`APIShared.GameModes`. Caller-defined profiles never constrain an unrelated mod.
Command interception, formations, work-target policy and MoatMove orchestration
belong to BugfixesAndQoL and its addon. APIShared supplies the targeted public
route-search preference events used by that publisher and third-party observers.

## Finding and extending a feature

Start with the feature's subject, then its public contract. Keep implementation and
hooks beside that feature; a hook is an implementation mechanism, not a separate
product area. General native mechanisms live in `Core/Internal/Native`.

```text
src/
  Core/                         entry point, initialization, capability ownership
    Internal/Native/            pattern resolution, memory, native reservations
  Missions/                     mission contracts, state and event relay
  Buildings/
    Repair/                     repair contracts and service
    Gatehouse/                  timing, origin and drawbridge coordination
  Presentation/
    UnitHud/                    HUD contracts and implementation parts
    HudExtras/                  independent side-HUD buttons, layout and rendering
    BriefingGold/               briefing gold contracts and service
  ModSettings/
    Presets/                    contracts, host adapter, controller, catalog and storage
    Lobby/                      per-player synchronization and convergence
    UI/                         search and tooltip presentation
    Internal/                   JSON parser and atomic file replacement
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

Customized launch owners register typed snapshot providers in `CustomizedLaunchOrigins`.
The registry combines evidence without naming or reflecting over consumer assemblies.
Each consumer owns its launch state and save protocol; APIShared owns validation and
conflict handling when capturing the common mission context.

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

## Publication

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
disable independent managed services.

Readiness callbacks run outside the initialization lock with exception isolation.
There is no implicit thread dispatch. Registration ownership, ordering, replay and
callback contracts are described in the [API catalog](docs/API_CATALOG.md).

## Settings and dependencies

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
Published presets keep their contracts, JSON schema, file discovery and dialog row models
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

The solution contains the runtime, two local integration suites, a game-independent
core suite and public consumer examples. Test-only packages never become plugin
dependencies. The standalone build uses no neighboring repositories. Additional
mod integration tests belong in their consumer repositories.
