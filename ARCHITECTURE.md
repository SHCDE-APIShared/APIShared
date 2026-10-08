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
| GameModes | Context capture and optional caller-defined permission profiles |
| ModSettings, Savegames | Settings UI integration, presets, convergence and persistence |
| Units, Pathfinding, Diagnostics | Queries and advanced integrations |
| UnitCommands | Required internal command, formation and MoatMove runtime |
| SerpsMods | Explicit compatibility profiles for existing Serps consumers |

Directories help navigation; public namespaces define the contracts. Capabilities
use `APIShared`; settings and general profiles use `APIShared.ModSettings` and
`APIShared.GameModes`. Serps profiles are opt-in and never constrain an unrelated mod.
Internal command/formation integration is retained for existing friend-assembly
consumers; it is not an additional public entry point.

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
    BriefingGold/               briefing gold contracts and service
  ModSettings/
    Presets/                    preset documents, sources, view model, persistence
    Lobby/                      per-player synchronization and convergence
    UI/                         search and tooltip presentation
    Internal/                   JSON parser and atomic file replacement
  Pathfinding/
    Assassin/                   assassin route and attack integrations
    GateRoutes/                 shared gate/bridge policy bridges
    Moat/                       elevated moat AI state
  UnitCommands/                 internal command integration
    Runtime/                    process state, initialization, scopes, diagnostics
    Native/                     shared command hooks and exact backend contracts
    Movement/                   route search, publication and movement context
    Attack/                     attack orders, approach candidates and fallbacks
    Cursor/                     selection, targets and cursor eligibility
    Moat/                       work targets, fill approaches and placement
    Formation/                  input, preview, network commands and slot hooks
    Integration/                adapters between command and pathfinding services
```

Other small subjects (`Units`, `Players`, `Lobby`, `GameModes`, `Savegames`,
`Diagnostics`, `SerpsMods`) remain shallow. Add subdivisions when distinct
responsibilities actually exist, rather than creating empty template directories.
Folder paths do not change C# namespaces or public API identity.

For a new service:

1. Put caller-facing options, results and registration interfaces in a named
   `FeatureContracts.cs` beside that subject's service. Reuse existing services
   before introducing another public contract.
2. Keep ownership, registrations and availability in the service/capability.
   Separate substantial behavior into named implementation files. For example,
   the HUD service has `Registrations`, `Categories`, `Recruitment`,
   `DetailsAndArmy`, `Images` and `Hooks` parts.
3. Give a hook or closely coupled hook group a descriptive file. Formation uses
   `EngineAndCameraHooks` and `SlotHooks`; moat work uses `WorkTargetHooks`.
   Put its native validation next to it or in a dedicated `NativeContracts` part.
   A shared interception point has one owner and dispatches to its features;
   features must not independently install competing hooks on that address.
4. Connect the service through `ApiSharedRuntime` and the public entry point only
   when it is generally useful. Document it in the API catalog, add an example
   where useful, and test observable behavior in the appropriate suite.

Partial classes here divide one existing service, not independent runtimes.
`UnitCommandPathRuntime.State`, `FormationRuntime.State` and
`UnitHudPresentationService` retain their field initializers in their original
order. Initialization and published-hook lifetime stay centralized. Put a new
operation in its feature part; do not grow the state or initialization file with
unrelated algorithms. A small coherent feature can stay in one file.

## Publication

`APISharedPlugin.Awake` publishes managed services. `CrusaderLibrary.LibraryLoaded`
initializes native services. `ApiSharedRuntime.ProcessInstance`, static registries
and persistent publishers retain services after startup cleanup. Global completion
and individual capability availability are separate; one native failure must not
disable independent managed services.

Readiness callbacks run outside the initialization lock with exception isolation.
There is no implicit thread dispatch. Registration ownership, ordering, replay and
callback contracts are described in the [API catalog](docs/API_CATALOG.md).

## Settings and dependencies

The preset view model integrates source selection, UI commands and persistence.
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
