# API catalog

## Game-independent content

`APIShared.ModData.ModDataNamespaceReader.Read(json, modGuid, source)` in
`APIShared.Content.dll` reads one GUID namespace into a recursively immutable snapshot.
It needs no game installation and targets .NET Standard 2.0. See
[content integration](THIRD_PARTY_GUIDE.md#game-independent-content).

`APIShared.ModSettings.MissionModSettingsDocument`, `MissionModSettingsEntry` and
`MissionModSettingsJson` in Content provide mutable schema-3 authoring documents,
validation, pruning and deterministic JSON, without file access or runtime settings
application. See the same [content guide](THIRD_PARTY_GUIDE.md#game-independent-content)
for limits, mutation semantics and a minimal example.

`SavegameModSettings.GetParticipantDiagnostics()` reports accepted and rejected
Map/Trail/Savegame settings participants from the existing registrations, including
typed status, reason and read-only host property metadata. Its purpose overload
preserves the distinction between automatic saves and mission authoring. It shares
discovery with the existing automatic selection; see
[participant diagnostics](THIRD_PARTY_GUIDE.md#settings-participant-diagnostics).

Optional runtime tracing is marked `API_SHARED_DIAGNOSTICS` and defaults to off;
see [diagnostic opt-in and observer demand](THIRD_PARTY_GUIDE.md#diagnostic-opt-in-and-observer-demand).

The development-only [early dependency correction](DEPENDENCY_BOOTSTRAP.md) uses
an independent, pair-validated bootstrap companion. It adds no public API and has
not yet passed the transferred assembly's game/package-lifecycle acceptance.

The internal [Fixes wheat-sale compatibility correction](FIXES_COMPATIBILITY.md)
runs once at verified startup, after the optional Fixes plugin. It adds no public
API and refuses unknown binary/startup contracts independently of public services.

Opt-in [Script Extender replacements](../src/ScriptExtenderReplacements/DOCUMENTATION.md)
provide the corrected `APIShared.ScriptExtenderReplacements.GamePathingManagerAPI`
table methods. These require explicit caller migration and do not hook SE methods.

APIShared also provides internal, binary-validated [Script Extender bugfixes](THIRD_PARTY_GUIDE.md#script-extender-bugfixes).
They require no capability acquisition or consumer registration and are implemented
separately in `src/ScriptExtenderFixes`. Unsupported dependency implementations are
skipped without disabling the public services below. The popularity display correction
automatically follows changed SE contribution values in reports and building panels,
without modifying simulation or requiring consumer registration.

Capabilities are acquired through `ModApiClient` or `IApiShared`; shared event brokers also expose static registration methods. The command, presentation, recruitment and market entries below require APIShared 0.6.0 or later. The table describes the existing contracts, not a guarantee of support for every game build. Always inspect returned diagnostics. All unit/building/player game IDs are one-based where documented; array indices are not game IDs.

Rounded fear coefficients use [`Economy.FearPopularity`](THIRD_PARTY_GUIDE.md#rounded-fear-popularity). This opt-in development API preserves SE behavior while inactive and shares the same result with the report correction.

| Area / entry | Purpose and availability | Thread, ownership and lifetime |
|---|---|---|
| `TryGetMissionLifecycle` | Managed initialization checkpoints, completed mission start/end and current context; startup member check | Register on Unity thread; publisher-thread notifications; no guaranteed observer sort; late start replay only; process lifetime |
| `MissionRestart.Run(ownerGuid, action)` | Explicit same-mission restart encompassing preparation and loading; links replacement sessions for settings ownership | Unity thread, ready non-editor source; nested calls share one attempt; no persistence or settings transport |
| `TryGetLobbyState` | Managed immutable multiplayer-lobby snapshots; startup member check | Register on Unity thread; immediate known-state replay; deterministic owner/ID ordering; process lifetime |
| `TryGetPlayerDefeat` | Managed one-shot lord death and official loss transitions; startup member check | Tick/simulation publisher starts with the first subscriber; first observed tick is baseline-only; no initial-state replay; do not change UI directly; process lifetime |
| `TryGetUnitHudPresentation` | Categories, image overrides, interactions, recruitment tickets and control groups; startup member check | Unity/Noesis presentation; owner-local IDs; ambiguity preserves Vanilla; logical activation extension; process lifetime |
| `TryGetHudExtrasButtons` | Independent side-HUD buttons, paging and popup tooltips; startup member check | Unity-thread factories/commands; owner-local IDs; logical activation; process lifetime |
| `TryGetBriefingGoldPresentation` | Ordered adjustments after Vanilla briefing calculation; startup member check | Presentation publisher; stage/owner/ID ordering; invalid results preserve last safe value; process lifetime |
| `TryGetGatehouseTiming` | Typed timing/distance settings | Native validation required; exclusive owner; permanent hook and logical state; automation via `IGatehouseAutomationCapability` |
| `TryGetGatehouseDistanceOrigin` | Vanilla begin coordinate or complete-bounds center | Native validation required; exclusive owner; permanent runtime state |
| `TryGetBuildingRepair` | Repair quote and repair-tooltip presentation; startup member check | Acquired on demand after native initialization; respect each operation's contract; UI on Unity thread |
| `TryGetAivBuildStep` | Before/after observation around one unchanged Vanilla call | Native caller thread; deterministic begin order and reverse completion; owner-local IDs; process lifetime |
| `APIShared.GameModes` | Mode snapshots, caller-defined contexts and optional permissions | No automatic permission enforcement; use the relevant mission snapshot; explicit multiplayer policy |
| `APIShared.ModSettings` | Settings base class, registration, presets, sources and search | Unity-thread UI/registration; existing host/per-player sync; personal persistence remains isolated |
| `APIShared.Commands.GameActionEvents` | Managed command Pre/Accepted/Post; mutable payloads and sticky veto | Startup-thread registration; actual caller thread; order/owner/ID sorting; process lifetime |
| `APIShared.Presentation.PresentationEvents` | Named HUD Pre/Post; parameter changes, veto and deferred replacement actions | Startup-thread registration; actual presentation caller thread; owner-private State; process lifetime |
| `APIShared.Recruitment.RecruitmentRequestPolicy` | Pure recruitment ceilings and pending-reservation arithmetic | No hook or native affordability guarantee; consumers own reconciliation |
| `APIShared.Recruitment.RecruitmentMaterialUi` | Shared European weapon-stock HUD bypass predicates | Validated 26-site IL contract; OR combination; process lifetime |
| `APIShared.Recruitment.RecruitmentAvailability` | One shared pass that disables Vanilla recruit buttons by side-effect-free predicates, with one context per GUI check | Startup-thread registration; GUI-check Post on its Unity caller thread, only in Vanilla recruitment panel modes; already disabled buttons stay untouched; OR combination; process lifetime |
| `APIShared.Presentation.TroopActionHud` | Single owner of the troop-action HUD setup: registered visibility refreshers, then one shared layout of all `SerpTroopAction_` hosts | Unity thread; owner/ID order; immediate refresh of an existing panel; process lifetime |
| `APIShared.Economy.MarketPriceEvents` | Native buy/sell query Pre/Post and replacement totals | Register after LibraryLoaded initialization; audited image only; actual native caller thread; process lifetime |
| `APIShared.Units.UnitSettledEvents` | Created, recruited, assigned and disbanded units once their native creation or type change has finished; value notifications | Startup registration; SE simulation-tick thread; trigger order, then order/owner/ID; first registration subscribes to SE; no replay; process lifetime |
| `APIShared.NativeSites.NativeSiteRegistry` | Prefer a shared APIShared service; use the registry for hooks APIShared doesn't cover yet. Cooperative registry of CrusaderDE.dll native sites: register a span before hooking it (Exclusive, ChainableEntry, CallOnly, Observe), get an acceptance or a refusal naming the owner, query owner and mode by RVA or span. A refusal identifies a conflict without resolving it; an accepted overlap only means no byte overlap, not compatibility | Any thread; register at startup or install time; atomic groups; allocation-free snapshot queries; process lifetime, unregister only to roll back; sees only registered sites ([guide](NATIVE_SITES.md)) |
| `APIShared.Savegames.VersionedSaveData` | Versioned per-mod save records over Script Extender `ModSaveDataAPI`: migration steps, unknown entries kept, documented load results | Register during startup; capture on the Extender's save thread; one load result per archive load in owner/identifier order; process lifetime |
| `APIShared.Audio.SoundSourceEvents` | Native-stage Pre for attributed soldier and melee-kill sound calls: acting unit ID, GlobalId and type, the killed victim, and sound ID replacement before the native record exists | Any-thread registration; native game-tick thread inside the wrapper; order/owner/ID; no replay; allocation-free struct args; process lifetime |
| `APIShared.Audio.SoundSourceSelection` | Optional set of catalog sources (by key or situation) for `SoundEvents`/`SoundSourceEvents` registrations; unselected sources stay native | Immutable; any thread; fixed per registration; existing overloads equal `All` ([guide](AUDIO.md#source-selections)) |

Entries marked *startup member check* resolve every Script Extender and game member their own code binds when they are created ([member lists](../src/Core/Internal/ManagedMemberContract.cs)). If one is missing or has another type or signature, only that capability reports `ValidationFailed` with the member in `Reason` and logs one error; other capabilities are unaffected, and nothing is checked per call. A capability whose members resolve is never made unavailable by this check.

The catalog describes this source checkout. Check the selected package and its
public declarations before assigning a consumer minimum version; source features
may not yet be present in published downloads.

## Multiplayer packets

Requires APIShared 0.7.0 or newer; register through `ApiShared.ForMod(ownerGuid).Network`; no SerpsModsHost
dependency is required. `RegisterChore<T>` retains synchronous simulation execution.
`RegisterMessage<T>` snapshots payload and Steam identity before FIFO Unity dispatch.
Register unconditionally on Unity after the Extender's built-in packet registration,
in the same order on all peers. Explicit MessagePack formatters are required.

Channel options accept logical activation, validation, payload limits and diagnostic
callbacks; message options additionally accept a pure deep-copy callback. Messages
can target the game, one 1-based player ID, active-lobby peers or a Steam ID.
`ChoreTransport.Send/TrySend` adapts existing registered packet hooks and enforces
the 1200-byte Chore cap including the packet ID. `TryPostPresentation` posts copied
UI/log work without delaying simulation execution.

The consumer owns current-session authority, deterministic gameplay, stable IDs,
protocol compatibility, acknowledgements and operation deduplication. Local
`Submitted` results do not prove execution or peer delivery. The Extender's public
Chore API returns void and retains its own fallback; APIShared preflights known
manager/size failures but cannot promise an atomic native queue acknowledgement.
See the [networking guide](THIRD_PARTY_GUIDE.md#multiplayer-actions-and-messages) and
[compiled example](../examples/ThirdPartyMod/NetworkingExample.cs).

## Source map

Start at the linked contract to learn what a consumer may call. Read the service
when changing behavior; internal files are not consumer extension points.
Member-level documentation is also shipped as `APIShared.xml` beside the DLL.

| Feature | Public contracts / entry | Behavior / hook owner | Example |
|---|---|---|---|
| Client and readiness | [ModApiClient](../src/Core/ModApiClient.cs), [core contracts](../src/Core/Contracts.cs) | [ApiSharedRuntime](../src/Core/ApiSharedRuntime.cs) | [Plugin](../examples/ThirdPartyMod/ExamplePlugin.cs) |
| Mission notifications | [Mission contracts](../src/Missions/MissionLifecycleContracts.cs) | [Capability](../src/Missions/MissionLifecycleCapability.cs), [relay](../src/Missions/Internal/MissionEventRelay.cs) | [Mission observer](../examples/ThirdPartyMod/MissionExample.cs) |
| Lobby observations | [Lobby contracts](../src/Lobby/LobbyStateContracts.cs) | [Capability](../src/Lobby/LobbyStateCapability.cs) | — |
| Multiplayer packets | [Packet contracts](../src/Networking/NetworkPacketContracts.cs), [owner registration](../src/Networking/ModNetworkClient.cs), [existing Chore sender](../src/Networking/ChoreTransport.cs) | [Chore channel](../src/Networking/ChoreChannel.cs), [message channel](../src/Networking/MessageChannel.cs) | [Networking](../examples/ThirdPartyMod/NetworkingExample.cs) |
| Defeat notifications | [Player contracts](../src/Players/PlayerDefeatContracts.cs) | [Capability](../src/Players/PlayerDefeatCapability.cs) | — |
| Troop HUD | [HUD contracts](../src/Presentation/UnitHud/UnitHudContracts.cs), [action buttons](../src/Presentation/UnitHud/UnitHudActionButtonContracts.cs) | [Service and state](../src/Presentation/UnitHud/UnitHudPresentationService.cs), [hooks](../src/Presentation/UnitHud/UnitHudPresentationService.Hooks.cs), named feature parts beside them | [HUD](../examples/ThirdPartyMod/HudExample.cs) |
| Side HUD | [Button contracts](../src/Presentation/HudExtras/HudExtrasButtonContracts.cs) | [Service](../src/Presentation/HudExtras/HudExtrasButtonsService.cs), [layout](../src/Presentation/HudExtras/HudExtrasButtonLayout.cs) | [HUD](../examples/ThirdPartyMod/HudExample.cs) |
| Briefing gold | [Contracts](../src/Presentation/BriefingGold/BriefingGoldContracts.cs) | [Capability](../src/Presentation/BriefingGold/BriefingGoldPresentationCapability.cs) | — |
| Repair | [Contracts](../src/Buildings/Repair/BuildingRepairContracts.cs) | [Capability](../src/Buildings/Repair/BuildingRepairCapability.cs) | — |
| Gatehouse coordination | [Timing](../src/Buildings/Gatehouse/GatehouseTimingCapability.cs), [origin](../src/Buildings/Gatehouse/GatehouseDistanceOriginCapability.cs), [drawbridge helper](../src/Buildings/Gatehouse/GatehouseDrawbridgeCoupling.cs) | [Permanent state](../src/Buildings/Gatehouse/GatehousePermanentRuntimeState.cs), [automation](../src/Buildings/Gatehouse/GatehouseAutomationNativeState.cs) | — |
| Selection and perspective | [Local selection](../src/Units/LocalSelectionAPI.cs), [marked units](../src/Units/MarkedUnitSelectionAPI.cs), [perspective](../src/Players/PlayerPerspectiveAPI.cs), [unit access](../src/Units/UnitAccess.cs) | Implementation in those files | — |
| Modes and launch evidence | [Profiles](../src/GameModes/GameplayModModePolicy.cs), [mode capture](../src/GameModes/MissionModePolicy.cs), [origins](../src/GameModes/CustomizedLaunchOrigins.cs) | Implementation in those files | [Mission policy](../examples/ThirdPartyMod/MissionExample.cs), [launch origin](../examples/ThirdPartyMod/CustomizedLaunchExample.cs) |
| Settings and presets | [View model](../src/ModSettings/Presets/PresetLobbyModSettingsViewModel.cs), [registration](../src/ModSettings/Presets/LobbyModSettingsPresetRegistration.cs), [preset contracts](../src/ModSettings/Presets/ModSettingsPresetContracts.cs), [working sources](../src/ModSettings/Presets/ModSettingsWorkingSources.cs), [dynamic providers](../src/ModSettings/Presets/DynamicPresetSettings.cs) | [Controller](../src/ModSettings/Presets/LobbyPresetController.cs), [storage](../src/ModSettings/Presets/LobbyPresetStorage.cs), [per-player coordination](../src/ModSettings/Lobby/PerPlayerLobbySettings.cs), [application](../src/ModSettings/ModSettingsApplication.cs) | [Personal storage](THIRD_PARTY_GUIDE.md#personal-files-and-previous-installations), [Settings](../examples/ThirdPartyMod/ExampleSettings.cs), [XAML](../examples/ThirdPartyMod/Override/ScriptExtenderUI/APISharedExample.xaml) |
| Settings search / tooltips | [Search and attached properties](../src/ModSettings/UI/ModSettingsSearch.cs), [tooltip sizes](../src/ModSettings/UI/ToolTipPresentation.cs) | Implementation in those files; [focus scrolling](../src/ModSettings/UI/ModSettingsHorizontalFocusScrollGuard.cs) | [XAML](../examples/ThirdPartyMod/Override/ScriptExtenderUI/APISharedExample.xaml) |
| Savegame participation | [Contracts](../src/Savegames/SavegameModSettingsContracts.cs), [exclusion attribute](../src/ModSettings/ExcludeFromSavegameModSettingsAttribute.cs) | [SavegameModSettings](../src/Savegames/SavegameModSettings.cs) | — |
| Versioned save data | [Contracts](../src/Savegames/VersionedSaveDataContracts.cs), [registration](../src/Savegames/VersionedSaveDataRuntime.cs) | [Registry and load window](../src/Savegames/VersionedSaveDataRegistry.cs), [record codec](../src/Savegames/VersionedSaveRecordCodec.cs), [guide](SAVE_DATA.md) | [Save data](../examples/ThirdPartyMod/SaveDataExample.cs) |
| Additional route search | [Pre/Post contracts](../src/Pathfinding/Routes/RouteSearchContracts.cs) | [Registry and publication](../src/Pathfinding/Routes/RouteSearchEvents.cs) | [Route observer](../examples/ThirdPartyMod/RouteSearchExample.cs) |
| Advanced path integration | [Assassin API](../src/Pathfinding/Assassin/AssassinPathAPI.cs), [attack control](../src/Pathfinding/Assassin/AssassinAttackControlAPI.cs), [gate policies](../src/Pathfinding/GateRoutes/EnemyGatePathPolicyBridge.cs), [bridge diagnostics](../src/Pathfinding/GateRoutes/EnemyBridgeDiagnosticBridge.cs), [temporary routes](../src/Pathfinding/GateRoutes/TemporaryGateRouteAcceptanceBridge.cs), [elevated moat state](../src/Pathfinding/Moat/ElevatedMoatAiState.cs) | Named native contracts and implementations beside each entry | — |
| AIV build observation | [Contracts](../src/Diagnostics/AivBuildStepContracts.cs) | [Capability](../src/Diagnostics/AivBuildStepCapability.cs) | — |
| Managed command interception | [GameActionEvents](../src/Units/Commands/GameActionEvents.cs) | [Shared event infrastructure](../src/Core/Events/InterceptionEvent.cs), [managed hook owner](../src/Core/Events/ManagedInterceptionHook.cs) | [Interception](../examples/ThirdPartyMod/InterceptionExample.cs) |
| HUD interception | [PresentationEvents](../src/Presentation/Events/PresentationEvents.cs) | Named publishers in the same file and shared event infrastructure | [Interception](../examples/ThirdPartyMod/InterceptionExample.cs) |
| Recruitment policies | [Request arithmetic](../src/Units/Recruitment/RecruitmentRequestPolicy.cs), [material UI](../src/Units/Recruitment/RecruitmentMaterialUi.cs), [button availability](../src/Units/Recruitment/RecruitmentAvailability.cs) | [Material IL contract](../src/Units/Recruitment/RecruitmentMaterialUiIlContract.cs) | [Interception](../examples/ThirdPartyMod/InterceptionExample.cs) |
| Troop action buttons | [TroopActionHud](../src/Presentation/TroopActions/TroopActionHud.cs) | [Layout](../src/Presentation/TroopActions/TroopActionHudLayout.cs), [slot policy](../src/Presentation/TroopActions/TroopActionHudLayoutPolicy.cs) | — |
| Building tooltip text | [Public broker](../src/Presentation/BuildingTooltip/BuildingTooltipPresentation.cs) | Shared presentation in that file | [Tooltip](../examples/ThirdPartyMod/BuildingTooltipExample.cs) |
| Situational audio | [Sound contracts](../src/Audio/SoundContracts.cs), [event broker](../src/Audio/SoundEvents.cs), [source contracts](../src/Audio/SoundSourceContracts.cs), [source events](../src/Audio/SoundSourceEvents.cs) | [Audio contracts and implementation](AUDIO.md) | [Sound](../examples/ThirdPartyMod/SoundExample.cs) |
| Workshop packages | [Owner-bound client](../src/Workshop/WorkshopClient.cs) | [Publishing and deployment](WORKSHOP.md) | [Workshop](../examples/ThirdPartyMod/WorkshopExample.cs) |
| Gate access rules | [Passive registry](../src/Pathfinding/GateRoutes/GateAccessRules.cs) | [Provider contracts](GATE_ROUTING.md) | [Gate access](../examples/ThirdPartyMod/GateAccessExample.cs) |
| Native market queries | [MarketPriceEvents](../src/Economy/MarketPriceEvents.cs) | [Validated native owner](../src/Economy/MarketPriceNativeRuntime.cs) | [Integration guide](THIRD_PARTY_GUIDE.md#sharing-native-market-price-queries) |
| Native sites | [Contracts](../src/Core/NativeSites/NativeSiteContracts.cs), [registry](../src/Core/NativeSites/NativeSiteRegistry.cs) | [Owner registry](../src/Core/Internal/Native/NativeOwnershipRegistry.cs), [APIShared site catalog](../src/Core/NativeSites/ApiSharedNativeSites.cs), [guide](NATIVE_SITES.md) | [Native sites](../examples/ThirdPartyMod/NativeSiteExample.cs) |
| Unit settlement | [UnitSettledEvents](../src/Units/Settled/UnitSettledEvents.cs), [notification contracts](../src/Units/Settled/UnitSettledContracts.cs) | [Settle tracker](../src/Units/Settled/UnitSettledTracker.cs), [SE subscriptions](../src/Units/Settled/UnitSettledScriptExtenderSource.cs) | [Unit settled](../examples/ThirdPartyMod/UnitSettledExample.cs) |

Assassin obstacle control uses `AssassinAttackControlAPI.RegisterGuard(ownerGuid, callback)`
for idle-only completion, or its overload with `AssassinObstacleCompletionMode.NativeRetarget`
to run Vanilla's existing objective-based target selection after a veto. The synchronous callback
receives a one-based unit game ID on the native simulation thread and must not issue commands.
One owner holds the permanent hook; consumers implement their own logical activation predicate.
Native selection can fail or select another vetoed obstacle; it does not guarantee a successful route.
Obstacle completion does not expose consumer-defined alternative traversal search
and requires no command-engine mod.

## Game-mode permissions and settings presentation

The optional mode evaluator also has a consumer-owned [GameplayModeGate](../src/GameModes/GameplayModeGate.cs).
Feed lifecycle notifications before your own feature work; it has no automatic
subscriptions, hooks or settings side effects. The
[mode availability collection](../src/ModSettings/UI/ModSettingsModeAvailability.cs)
and [Noesis background properties](../src/ModSettings/UI/ModSettingsMode.cs) let
arbitrary mods share the same profiles with their settings UI. All presentation
configuration and refresh calls run on the UI thread. See
[the integration guide](THIRD_PARTY_GUIDE.md#optional-inactive-setting-backgrounds)
and the public example for one page-level legend and editable tinted rows.

## Side-HUD buttons

`ModApiClient.TryGetHudExtrasButtons` acquires `IHudExtrasButtonsCapability` independently of native Unit HUD hooks or troop selection. `TryRegisterButton` accepts `HudExtrasButtonDefinition`: owner-local ID, command, localized popup text, fresh Noesis Button factory, order (default 0), and optional ToolTip factory. Registration is process-lived; duplicate owner-local IDs are rejected.

APIShared places 36 x 34 buttons bottom to top inside Vanilla's `HUD_ObjectivesPanel`, reserving its Objectives/Freebuild slot. Visible active entries sort by ascending order, then ordinal owner GUID and ID. Five entries fit each page; an arrow above them cycles pages. Hidden and failed entries occupy no slots. Parent containers gate pages and activation because Vanilla's button style animates Button.Visibility.

Handles expose `SetVisible`, `SetEnabled`, `SetTooltip`, and `RequestContentRefresh`; the owner capability exposes `SetOwnerActive`. Commands are rechecked against current owner/button/HUD availability and receive the actual Button. Factories and commands execute on the Unity thread outside registry locks; exceptions isolate the entry. HUD replacement recreates visuals. This API does not impose local-troop selection or multiplayer gameplay authorization.

Default popups use the existing modoptions template, including Script Extender resolution scaling and a 60000 ms duration. Empty text suppresses the popup. An optional tooltip factory must return a fresh unattached ToolTip with explicit Style or Template; APIShared sets its Content to the current text and preserves that custom presentation. `RequestContentRefresh` retries factory failures. No screen coordinates or hook lifetimes are exposed.

## Own-troop action buttons

Acquire the owner-bound HUD service with `ModApiClient.TryGetUnitHudPresentation`,
then test for the optional `IUnitHudActionButtonsCapability` interface.
`TryRegisterActionButton` accepts an owner-local ID, command, nonempty localized
tooltip, content factory and optional sort order (default `0`). It returns a
process-lived logical handle for visibility, enabled state, Vanilla rollover text updates and
content refresh. No consumer supplies screen coordinates. Duplicate owner-local
IDs are rejected; the same ID under a different owner is independent.

Active visible entries are sorted by ascending order, then ordinal owner GUID
and button ID, and placed right to left beside Vanilla's Control Groups button.
Hidden entries consume no slots. A small arrow to its right cycles pages when
the measured space is insufficient. Owner activation applies to these entries
as well as existing HUD contributions.

Content factories run on the Unity thread outside the registration lock and
must return fresh unattached content fitting 35 by 35 HUD units. They run again
after explicit refresh, HUD replacement or player/colour changes; the context
contains the current HUD resource scope and the remapped Vanilla player colour.
Factory failures hide that entry until refresh or a context change and do not
stop other entries. Commands are reauthorized and exception-isolated at execution.
No implicit network command is sent: consumers retain their own multiplayer policy.

The strip is restricted to a nonempty local selection in the interactive Vanilla
troop container. Replacement foreign-unit displays cannot enable it merely by
setting `Show_HUD_Troops`. Context notifications report availability changes
(including owner/button visibility changes), not page changes; hover notifications
include hiding hovered entries. These optional callbacks also run on the Unity
thread, outside locks, with exception isolation. Registrations have no hook teardown.

## Direct helpers and advanced integration

`LocalSelectionAPI`, `MarkedUnitSelectionAPI` and `PlayerPerspectiveAPI` provide selection/perspective access independent of a custom command engine. `UnitAccess` validates unit lookup/liveness; its unsafe pointer views are immediate-use advanced contracts and must not outlive their valid game state. `GatehouseDrawbridgeCoupling` is a pure spatial helper: callers supply validated bounds and live identity predicates; it grants no ownership or access permission.

`AssassinPathAPI`, `AssassinAttackControlAPI`, `EnemyGatePathPolicyBridge`, `EnemyBridgeDiagnosticBridge` and `TemporaryGateRouteAcceptanceBridge` expose specialized path integration/diagnostics. They are not a general-purpose movement-command API. Use their documented immediate context and ownership contracts; do not retain transient native contexts. `SavegameModSettings` supplies typed save/trail settings integration. `LobbyPreparationOverride` is advanced shared lobby-preparation integration, not an alternative general lifecycle service.

Scanners, memory writers and concrete services are not supported public extension points. Native addresses are public only as coordination data in the [native-site registry](NATIVE_SITES.md): it records and reports who uses which span of CrusaderDE.dll and installs, scans or writes nothing itself. It supplements shared services for hooks APIShared does not cover yet; a point that several mods need belongs in a shared service. Command dispatch and formation implementations live in the consuming mods. Prefer Script Extender APIs directly for ordinary unit commands, pathing data, messages and events already supplied there.

`SavegameModSettings` prepares save settings before initialization and coordinates
authenticated multiplayer load readiness using the existing settings transports.
APIShared also owns the load dialog checkbox and remembers its last explicit local
choice across saves and process restarts (initially off). Without a usable checkbox,
automatic loads use current supported settings, subject to saved mission/creator rules.
[`SaveLoadDialogEvents`](../src/Savegames/SaveLoadDialogEvents.cs) coordinates ordered,
owner-bound dialog gates and preparation; see the [dialog integration](THIRD_PARTY_GUIDE.md#loadsave-requester-integration).
Solo multiplayer needs only local readiness; Coop detection remains in the central
mission mode service. See [settings integration](THIRD_PARTY_GUIDE.md#external-settings-and-application-backends)
and the adjacent save-restoration notes for personal-storage and timeout behavior.
The readiness packets are versioned (protocol 2); the host also compares every player's
stored copy of declared simulation-relevant save records, see
[multiplayer record comparison](SAVE_DATA.md#multiplayer-record-comparison).

`APIShared.ModSettings.ToolTipPresentation` exposes common tooltip sizes for external XAML without source links. Resolution-sensitive properties must be read on the Unity thread.

Apply `[APIShared.ModSettings.ExcludeFromSavegameModSettings]` to a plugin class
when it owns a separate persistence protocol or its settings are not mission data.
The inherited attribute excludes that plugin's lobby settings from automatic
savegame capture and restoration. It does not disable presets. Participation does
not depend on a particular plugin GUID or another mod's opt-out field.

Optional consumers requiring APIShared 0.7.1 or newer may call `SavegameModSettings.ExcludePluginFromAutomaticSettings(typeof(MyPlugin))` after dependency validation and before settings registration, instead of applying the exclusion attribute to their independent bootstrap type. This thread-safe registration is idempotent, applies to derived types and lasts until process exit. The exclusion applies only to the registered type and its derived types.

Settings view models show no mod-policy notice by default. Before registration,
call `System_ConfigureDirectLaunchNotice(() => localizedText)` when your mod needs
an explanation for direct campaign/Trail launches. APIShared determines the existing
menu/mission visibility; your mod supplies the message. The provider runs on the UI
reader's thread, is retained by the view model and may update its localized text.
Exceptions or empty results preserve its last successful message. Pass `null` to
disable the notice and clear its cached text.

The seven-argument `LobbyModSettingsPresetRegistration.Register` overload accepts
`enableScrollDiagnostics` explicitly. Existing overloads leave this off. Diagnostics
use the caller's logger and mod name; the horizontal-scroll guard itself applies to
all registered pages independently of diagnostics. It is implemented in the
ModSettings UI area alongside the settings search integration.

## Additional weighted route events

`APIShared.Pathfinding.RouteSearchEvents.TryRegister(ownerGuid, registrationId,
pre, post, out reason)` observes participating mods' additional weighted searches.
The current publisher is BugfixesAndQoL's command runtime, including the MoatMove
addon. This is a targeted extension point, not a replacement for Script Extender's
unit-order Pre/Post events. Cached route reuse and topological reachability queries
do not publish a new weighted-search event.

Pre exposes immutable publisher, player, endpoints and traversal permissions.
Positive `GroundEdgeCost` and `MoatEdgeCost` change route preference within
`MaximumEdgeCost`; they do not change native speed, permissions or publication
validation. Actual cadence and improvement limits remain authoritative.
`SkipOriginalFunction` skips this additional calculation, preserving the caller's
existing fallback. It does not cancel the game order. As with Extender's mutable
skip flag, a later valid Pre callback may change an earlier callback's decision.
Use identical deterministic policies on all multiplayer peers.

Registration is available without native readiness. Callbacks run synchronously on
the search caller's thread, outside the registration lock, in ordinal owner/ID order.
The registry retains them for the process lifetime; no replay or unsubscription is
provided. Registrations added during an operation join the next operation.
Throwing or invalid Pre contributions roll back that callback's entire contribution;
other callbacks continue. Pre arguments expire after notification. Post reports
the immutable effective costs and result, including ordinary failed calculations.
Skipped searches and publisher exceptions have no Post; Post cannot cancel an
already executed calculation. Callback errors are isolated and counted in
`CallbackFailures`. Do not issue commands or mutate game state inside callbacks;
nested preference calculations during notifications are skipped without recursion.
See the [public example](../examples/ThirdPartyMod/RouteSearchExample.cs).

## Failure and registration rules

`NativeApiState.Ready` means the global API is published. Capability diagnostics may still report `Pending`, `UnsupportedBuild`, `PatternMissing`, `Ambiguous`, `ValidationFailed`, `Conflict` or `Faulted`. `WhenReady` callbacks run once initialization ends, also after failures: each native initialization step is isolated, a failing step leaves its capability `Faulted` with the reason, and a failed native publication makes the state `Unavailable` (see `ApiShared.WhenReady`). Capability acquisitions and diagnostic-bearing registrations report reasons; boolean queries may instead return false as documented. `ConflictOwnerGuid` identifies an owner where applicable. Native hash fields may be empty for managed services or before native initialization.

Registration IDs are unique within an owner and service; stable IDs identify contributors, while ordering follows each service contract. Registrations do not imply replacement or unsubscription. Callback exceptions are isolated where the service explicitly documents that contract; native Vanilla exceptions retain the original propagation rules. No API-wide promise of Unity-thread dispatch exists. Use logical activation to suspend supported presentation features; never dispose a published process-wide hook.

## Customized launch evidence

Mods that own customized trail launches register a snapshot callback through
`CustomizedLaunchOrigins.Register(ownerGuid, capture)` during startup, before the
first mission load. Ordinary consumers need no registration. There is one provider
per exact owner GUID, retained for the process lifetime. Re-registering the same
delegate is harmless; replacing it throws. Providers run synchronously on the
mission capture thread outside the registry lock and should return coherent,
read-only evidence. Providers added during capture participate in the next capture.
No replay, implicit dispatch or disposal is involved. Callback failures, invalid
trail evidence and multiple active providers mark the origin as conflicting.
Registration order never chooses a winning active provider. Return `None` while
inactive; set `SupportsBuiltInOrigins` only if your mod tracks built-in trails too.
The contract uses existing game trail families and ID ranges; it supplies evidence,
not another mod's permission policy. See the optional
[customized launch example](../examples/ThirdPartyMod/CustomizedLaunchExample.cs).

## Managed actions and HUD interception

[GameActionEvents](../src/Units/Commands/GameActionEvents.cs) coordinates the exact
`EngineInterface.GameAction(GameActionCommand, int, int, int)` overload. The
KeyFunctions overload, direct native callers, network execution and actual unit
creation are outside this publisher. One hook preserves the installed MonoMod /
Script Extender chain. Register once on the Unity startup thread with owner GUID,
owner-local ID, optional Pre/Post and optional Accepted. Callback lifetimes are the
process lifetime; activate through your own logical settings, never hook teardown.

Pre can change the three payloads or set `SkipOriginalFunction`. Command is fixed;
payload meanings depend on it. Successful vetoes are sticky. A failing Pre rolls
back its payload/veto changes and discards its private State. Ascending order then
ordinal owner GUID/ID defines notification order. Registrations during notification
start on the next invocation. Accepted sees frozen final inputs after all Pre
callbacks, only for an unvetoed call, immediately before the original. It supports
preparations such as recruitment tickets and pre-Stop cancellation without applying
them to a later-vetoed command. Acceptance does not promise native success. Post
runs on normal original return and on veto (result zero), but not on an original
exception. Its final payload and owner-private State support reservation accounting.
Zero is not a negative recruitment acknowledgement; native creation occurs later.

[RecruitmentRequestPolicy](../src/Units/Recruitment/RecruitmentRequestPolicy.cs)
provides pure ceiling/reservation arithmetic. For MakeTroop, the first payload is
the amount and the second is the Vanilla unit type. An untouched Ctrl ceiling of
1000 can use a consumer's Vanilla preview. Explicitly assigning an amount marks it
concrete, including assigning 1000 itself. Reservations must use final Post values
and retain the consumer's observed-unit reconciliation.

[PresentationEvents](../src/Presentation/Events/PresentationEvents.cs) owns named
GUI-check, building-rollover, recruitment/troop-panel hover and siege-ammo button
sites: `GuiChecks`, `BuildingRollover`, `RecruitmentEnter`, `RecruitmentLeave`,
`TroopPanelEnter`, `TroopPanelLeave` and `RechargeSiegeAmmo`. GUI checks also run
while paused. Pre can replace a button parameter or veto
the complete managed call. Post reports normal completion, veto or original
exception; the original exception is rethrown after callbacks. Use
`OriginalCompleted` before augmenting Vanilla writes and State for cleanup even when
it is false. Keep references invocation-local; panels are replaced across maps.
These are presentation callbacks on their actual caller thread, not native events
or simulation clocks. Exceptions isolate registrations and are counted; repeated
errors are logged at exponentially spaced counts. Nested operations from a callback
run their originals without recursive notifications. Do not issue nested GameAction
commands. Native effects sent directly by an explicit replacement UI action remain
the consumer's deterministic multiplayer responsibility.

Presentation Pre may assign a replacement action. It runs once with the final
parameter only after all Pre callbacks; any veto suppresses it as well as Vanilla.
Post exposes WasReplaced and CompletionException, and OriginalCompleted remains
false for replacements. This lets another mod cancel a replacement button action
before native packets or other effects are issued.

[RecruitmentMaterialUi](../src/Units/Recruitment/RecruitmentMaterialUi.cs) coordinates
the complete validated European weapon-stock HUD IL patch. Predicates combine by OR;
true bypasses only fixed stock presentation, preserving other button gates. It does
not remove native material costs or establish affordability. Predicate exceptions
mean false. The installed original IL must match all 26 sites or registration fails.
No source links or private bridge are required by consumers. See the compiled
[interception example](../examples/ThirdPartyMod/InterceptionExample.cs).

[RecruitmentAvailability](../src/Units/Recruitment/RecruitmentAvailability.cs) owns one
GUI-check Post pass for all recruit-button restrictions (limits, extra costs). It runs
only while Vanilla's recruitment in-building modes are active, because only then does
Vanilla rewrite every recruit button's `IsEnabled` earlier in the same check. Each pass
reads the local player, hovered type and requested amount once into a
`RecruitmentAvailabilityContext`; `Sequence` identifies the check so predicates can reuse
derived values across buttons. Buttons that are already disabled are never re-enabled
and not queried. Active predicates combine by OR in owner/ID order and must be side-effect
free; work with side effects belongs in the consumer's own GUI-check registration, whose
order decides whether it runs before (negative order) or after this pass.

[TroopActionHud](../src/Presentation/TroopActions/TroopActionHud.cs) is the single owner of
`HUD_Troops.SetuptroopActionsUI`. After Vanilla's setup it runs every registered
visibility refresher in owner/ID order and then lays out all `SerpTroopAction_` hosts once
(two free slots, priority order, duplicates hidden). `Reflow` lays out a panel after a
consumer changed `WantsVisibility` outside that setup; `RefreshCurrentPanel` repeats
refreshers and layout, for example after a map start. A registration on an already
built panel (direct editor launch) refreshes it immediately. Older mod builds that still
install their own setup hook keep working, because every implementation applies the same
host convention.

## Native market price queries

[MarketPriceEvents](../src/Economy/MarketPriceEvents.cs) owns the two native market
price queries on the audited image. Pre can supply `ReplacementTotal` and veto the
helper; Post observes the final total and registration-private state. A veto without
a replacement returns zero. Other Pre callbacks may intentionally compose a prior
replacement; no participant can clear a veto. Callback failures roll back that
participant's replacement/veto and do not escape into native callers.
The native player/goods/amount arguments remain unchanged and unvalidated. Consumers
must validate them before indexing tables. These queries cover AI affordability,
trade execution and ally-transfer valuation; they are not general market transaction
or player-trade events. Replacement policies must be consistent across those callers
and multiplayer peers. `CalculateTradeTotal` preserves signed division before
unchecked multiplication. Registration requires LibraryLoaded initialization and
fails closed on unknown native images or already occupied entries. Both permanent
hooks use only the installed NativeX64 Indirect backend, with ten displaced bytes,
validated pointer slots, entry points and trampoline continuation. Foreign consumers
should subscribe here rather than detour CEB10/CEB90 independently.

## Unit settlement events

Not in packages up to 0.9.1; check the package you require. [UnitSettledEvents](../src/Units/Settled/UnitSettledEvents.cs)
reports each unit that Script Extender saw being created (`OnUnitCreate` Post) or
changing type (`OnUnitTransition`: mercenary post and European barracks recruitment,
worker assignment, disband) once the native work has finished: the slot is alive,
has a GlobalId, kept its owner and identity, and a transition has changed its type.
Recruitment reuses the peasant's slot and changes its type many ticks after the
purchase event, so that event alone is too early for per-unit writes.

`UnitSettledNotification` values arrive on the SE simulation-tick thread in trigger
order; each unit reaches registrations by ascending order, ordinal owner GUID and ID.
The first registration subscribes to SE. Nothing is replayed and existing units are
not rescanned after a save load. Units that die, change owner or identity, or wait
more than 3000 ticks are dropped and counted. Callbacks are exception-isolated and
may change the settled unit's simulation state deterministically. The declaration
holds the exact rules; see the [integration guide](THIRD_PARTY_GUIDE.md#reacting-to-created-and-recruited-units)
and the [example](../examples/ThirdPartyMod/UnitSettledExample.cs).

## Versioned save data

`APIShared.Savegames.VersionedSaveData.TryRegister` stores one record per mod in saves through
Script Extender `ModSaveDataAPI`. A record carries an explicit version; consumer-owned
migration steps upgrade older records, entries the current version does not know are written
back unchanged, and too-new, unrecognized or unmigratable data is kept byte for byte. The load
callback runs once per archive load with a documented status, including `NoRecord`. Records
declared `SimulationRelevant` are compared across players before a multiplayer save loads.
Usage, the record-version migration policy, the comparison boundary, limits and
implementation are in [Save data](SAVE_DATA.md).

## Workshop inventory

`APIShared.Workshop.WorkshopInventory` exposes the schema-1 file inventory.
The permanent staging callback cleans only previous APIShared releases; foreign
map packages remain Extender-owned. See [inventory and cleanup](WORKSHOP.md#workshop-package-cleanup).

## Workshop publishing and loose packages

`ApiShared.ForMod(guid).Workshop` provides package validation, subscription and
owned-item queries, upload preparation, installation plans and explicit application.
Steam entry points require initialized Steamworks on Unity. Planning does not
change installed files; application requires consent and uses a post-exit updater.
See [Workshop contracts and workflows](WORKSHOP.md).

## Situational audio

Requires APIShared **0.8.0** or newer. `APIShared.Audio.SoundEvents` provides owner-bound
Pre/Post registrations for the audited GameMap playback path. Rules can compose
volume factors, replace a Vanilla effect or preloaded clip, set panorama and
suppress a request. Context exposes source/type evidence where safely attributed;
Post reports submission or suppression, not audibility.
See [public audio contracts](AUDIO.md#public-audio-contracts).

### Preloaded custom clips

`SoundClips` loads owner resources and map archives or imports borrowed Extender
clip references. `SoundClipHandle` is distinct from an effect ID; preload before
using `TryReplaceClip`. See [clip contracts](AUDIO.md#preloaded-custom-clips).

### Native source events

Not in packages up to 0.9.1; check the package you require. `APIShared.Audio.SoundSourceEvents`
publishes attributed calls of the positional wrappers Audio already owns before the native
record is appended: the 91 soldier catalog sources and melee kill cues. `SoundSourceArgs`
carries the acting unit's game ID, GlobalId and type, the killed victim for kill cues, and
`TryReplaceSound`. Use it for exact-unit cue changes instead of detouring 0x2960.
See [native source events](AUDIO.md#native-source-events).

### Audio source selections

Not in packages up to 0.9.2; check the package you require. `SoundSourceSelection.TryCreate`
selects catalog sources by `SourceKey` or `SoundSituation`; additional `TryRegister` overloads of
`SoundEvents` and `SoundSourceEvents` take it. Native attribution then runs only for sources some
enabled registration selects; the existing overloads select every source.
See [source selections](AUDIO.md#source-selections).

### Consumer-defined audio policies

Filters, evidence thresholds, repeat limits, quiet modes, regional gain, alternate
cues and live profiles remain consumer-owned. See [policy recipes](AUDIO.md#consumer-filters-repeat-limits-and-other-sound-policies)
and the [compiled example](../examples/ThirdPartyMod/SoundExample.cs).

### Audio runtime demand

Inactive registrations do not create audio demand. Permanent hooks remain rooted; with no active
registration the native bridges test a demand byte and run the original without managed code;
managed dormant playback avoids context allocation and native attribution skips
UnitAccess. With demand, a native return-address filter admits only the sources selected by
enabled registrations. See [implementation and measurements](AUDIO.md#audio-demand-and-dispatch-cost).

## Building tooltip contributions

[`BuildingTooltipPresentation`](../src/Presentation/BuildingTooltip/BuildingTooltipPresentation.cs)
adds immutable plain-text lines above the existing costs in both Vanilla building
tooltip variants. Registration is owner-bound, ordered and process-lived; providers
run on Unity's main thread after the existing presentation publishers. Empty lines
hide without reserving layout space; provider failures are isolated. Existing
Vanilla costs, repair extras and backgrounds are preserved. See the
[integration guide](THIRD_PARTY_GUIDE.md#building-tooltip-text) and
[standalone example](../examples/ThirdPartyMod/BuildingTooltipExample.cs).

## Gate routing extensions

`GateAccessRules` supplies passive, ordered access contributions. The explicitly requested
`GateRoutingNativeHooks` backend coordinates the permanent native routing adapters.
See [gate routing contracts](GATE_ROUTING.md) for scope, ownership and limitations.
