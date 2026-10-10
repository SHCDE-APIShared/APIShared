# API catalog

Capabilities are acquired through `ModApiClient` or `IApiShared`; shared event brokers also expose static registration methods. The command, presentation, recruitment and market entries below require APIShared 0.6.0 or later. The table describes the existing contracts, not a guarantee of support for every game build. Always inspect returned diagnostics. All unit/building/player game IDs are one-based where documented; array indices are not game IDs.

| Area / entry | Purpose and availability | Thread, ownership and lifetime |
|---|---|---|
| `TryGetMissionLifecycle` | Managed initialization checkpoints, completed mission start/end and current context | Register on Unity thread; publisher-thread notifications; no guaranteed observer sort; late start replay only; process lifetime |
| `TryGetLobbyState` | Managed immutable multiplayer-lobby snapshots | Register on Unity thread; immediate known-state replay; deterministic owner/ID ordering; process lifetime |
| `TryGetPlayerDefeat` | Managed one-shot lord death and official loss transitions | Tick/simulation publisher; no initial-state replay; do not change UI directly; process lifetime |
| `TryGetUnitHudPresentation` | Categories, image overrides, interactions, recruitment tickets and control groups | Unity/Noesis presentation; owner-local IDs; ambiguity preserves Vanilla; logical activation extension; process lifetime |
| `TryGetHudExtrasButtons` | Independent side-HUD buttons, paging and popup tooltips | Unity-thread factories/commands; owner-local IDs; logical activation; process lifetime |
| `TryGetBriefingGoldPresentation` | Ordered adjustments after Vanilla briefing calculation | Presentation publisher; stage/owner/ID ordering; invalid results preserve last safe value; process lifetime |
| `TryGetGatehouseTiming` | Typed timing/distance settings | Native validation required; exclusive owner; permanent hook and logical state; automation via `IGatehouseAutomationCapability` |
| `TryGetGatehouseDistanceOrigin` | Vanilla begin coordinate or complete-bounds center | Native validation required; exclusive owner; permanent runtime state |
| `TryGetBuildingRepair` | Repair quote and repair-tooltip presentation | Acquired on demand after native initialization; respect each operation's contract; UI on Unity thread |
| `TryGetAivBuildStep` | Before/after observation around one unchanged Vanilla call | Native caller thread; deterministic begin order and reverse completion; owner-local IDs; process lifetime |
| `APIShared.GameModes` | Mode snapshots, caller-defined contexts and optional permissions | No automatic permission enforcement; use the relevant mission snapshot; explicit multiplayer policy |
| `APIShared.ModSettings` | Settings base class, registration, presets, sources and search | Unity-thread UI/registration; existing host/per-player sync; personal persistence remains isolated |
| `APIShared.Commands.GameActionEvents` | Managed command Pre/Accepted/Post; mutable payloads and sticky veto | Startup-thread registration; actual caller thread; order/owner/ID sorting; process lifetime |
| `APIShared.Presentation.PresentationEvents` | Named HUD Pre/Post; parameter changes, veto and deferred replacement actions | Startup-thread registration; actual presentation caller thread; owner-private State; process lifetime |
| `APIShared.Recruitment.RecruitmentRequestPolicy` | Pure recruitment ceilings and pending-reservation arithmetic | No hook or native affordability guarantee; consumers own reconciliation |
| `APIShared.Recruitment.RecruitmentMaterialUi` | Shared European weapon-stock HUD bypass predicates | Validated 26-site IL contract; OR combination; process lifetime |
| `APIShared.Economy.MarketPriceEvents` | Native buy/sell query Pre/Post and replacement totals | Register after LibraryLoaded initialization; audited image only; actual native caller thread; process lifetime |

## Source map

Start at the linked contract to learn what a consumer may call. Read the service
when changing behavior; internal files are not consumer extension points.
Member-level documentation is also shipped as `APIShared.xml` beside the DLL.

| Feature | Public contracts / entry | Behavior / hook owner | Example |
|---|---|---|---|
| Client and readiness | [ModApiClient](../src/Core/ModApiClient.cs), [core contracts](../src/Core/Contracts.cs) | [ApiSharedRuntime](../src/Core/ApiSharedRuntime.cs) | [Plugin](../examples/ThirdPartyMod/ExamplePlugin.cs) |
| Mission notifications | [Mission contracts](../src/Missions/MissionLifecycleContracts.cs) | [Capability](../src/Missions/MissionLifecycleCapability.cs), [relay](../src/Missions/Internal/MissionEventRelay.cs) | [Mission observer](../examples/ThirdPartyMod/MissionExample.cs) |
| Lobby observations | [Lobby contracts](../src/Lobby/LobbyStateContracts.cs) | [Capability](../src/Lobby/LobbyStateCapability.cs) | — |
| Defeat notifications | [Player contracts](../src/Players/PlayerDefeatContracts.cs) | [Capability](../src/Players/PlayerDefeatCapability.cs) | — |
| Troop HUD | [HUD contracts](../src/Presentation/UnitHud/UnitHudContracts.cs), [action buttons](../src/Presentation/UnitHud/UnitHudActionButtonContracts.cs) | [Service and state](../src/Presentation/UnitHud/UnitHudPresentationService.cs), [hooks](../src/Presentation/UnitHud/UnitHudPresentationService.Hooks.cs), named feature parts beside them | [HUD](../examples/ThirdPartyMod/HudExample.cs) |
| Side HUD | [Button contracts](../src/Presentation/HudExtras/HudExtrasButtonContracts.cs) | [Service](../src/Presentation/HudExtras/HudExtrasButtonsService.cs), [layout](../src/Presentation/HudExtras/HudExtrasButtonLayout.cs) | [HUD](../examples/ThirdPartyMod/HudExample.cs) |
| Briefing gold | [Contracts](../src/Presentation/BriefingGold/BriefingGoldContracts.cs) | [Capability](../src/Presentation/BriefingGold/BriefingGoldPresentationCapability.cs) | — |
| Repair | [Contracts](../src/Buildings/Repair/BuildingRepairContracts.cs) | [Capability](../src/Buildings/Repair/BuildingRepairCapability.cs) | — |
| Gatehouse coordination | [Timing](../src/Buildings/Gatehouse/GatehouseTimingCapability.cs), [origin](../src/Buildings/Gatehouse/GatehouseDistanceOriginCapability.cs), [drawbridge helper](../src/Buildings/Gatehouse/GatehouseDrawbridgeCoupling.cs) | [Permanent state](../src/Buildings/Gatehouse/GatehousePermanentRuntimeState.cs), [automation](../src/Buildings/Gatehouse/GatehouseAutomationNativeState.cs) | — |
| Selection and perspective | [Local selection](../src/Units/LocalSelectionAPI.cs), [marked units](../src/Units/MarkedUnitSelectionAPI.cs), [perspective](../src/Players/PlayerPerspectiveAPI.cs), [unit access](../src/Units/UnitAccess.cs) | Implementation in those files | — |
| Modes and launch evidence | [Profiles](../src/GameModes/GameplayModModePolicy.cs), [mode capture](../src/GameModes/MissionModePolicy.cs), [origins](../src/GameModes/CustomizedLaunchOrigins.cs) | Implementation in those files | [Mission policy](../examples/ThirdPartyMod/MissionExample.cs), [launch origin](../examples/ThirdPartyMod/CustomizedLaunchExample.cs) |
| Settings and presets | [View model](../src/ModSettings/Presets/PresetLobbyModSettingsViewModel.cs), [registration](../src/ModSettings/Presets/LobbyModSettingsPresetRegistration.cs), [preset contracts](../src/ModSettings/Presets/ModSettingsPresetContracts.cs), [working sources](../src/ModSettings/Presets/ModSettingsWorkingSources.cs), [dynamic providers](../src/ModSettings/Presets/DynamicPresetSettings.cs) | [Controller](../src/ModSettings/Presets/LobbyPresetController.cs), [storage](../src/ModSettings/Presets/LobbyPresetStorage.cs), [per-player coordination](../src/ModSettings/Lobby/PerPlayerLobbySettings.cs), [application](../src/ModSettings/ModSettingsApplication.cs) | [Settings](../examples/ThirdPartyMod/ExampleSettings.cs), [XAML](../examples/ThirdPartyMod/Override/ScriptExtenderUI/APISharedExample.xaml) |
| Settings search / tooltips | [Search and attached properties](../src/ModSettings/UI/ModSettingsSearch.cs), [tooltip sizes](../src/ModSettings/UI/ToolTipPresentation.cs) | Implementation in those files; [focus scrolling](../src/ModSettings/UI/ModSettingsHorizontalFocusScrollGuard.cs) | [XAML](../examples/ThirdPartyMod/Override/ScriptExtenderUI/APISharedExample.xaml) |
| Savegame participation | [Contracts](../src/Savegames/SavegameModSettingsContracts.cs), [exclusion attribute](../src/ModSettings/ExcludeFromSavegameModSettingsAttribute.cs) | [SavegameModSettings](../src/Savegames/SavegameModSettings.cs) | — |
| Additional route search | [Pre/Post contracts](../src/Pathfinding/Routes/RouteSearchContracts.cs) | [Registry and publication](../src/Pathfinding/Routes/RouteSearchEvents.cs) | [Route observer](../examples/ThirdPartyMod/RouteSearchExample.cs) |
| Advanced path integration | [Assassin API](../src/Pathfinding/Assassin/AssassinPathAPI.cs), [attack control](../src/Pathfinding/Assassin/AssassinAttackControlAPI.cs), [gate policies](../src/Pathfinding/GateRoutes/EnemyGatePathPolicyBridge.cs), [bridge diagnostics](../src/Pathfinding/GateRoutes/EnemyBridgeDiagnosticBridge.cs), [temporary routes](../src/Pathfinding/GateRoutes/TemporaryGateRouteAcceptanceBridge.cs), [elevated moat state](../src/Pathfinding/Moat/ElevatedMoatAiState.cs) | Named native contracts and implementations beside each entry | — |
| AIV build observation | [Contracts](../src/Diagnostics/AivBuildStepContracts.cs) | [Capability](../src/Diagnostics/AivBuildStepCapability.cs) | — |
| Managed command interception | [GameActionEvents](../src/Units/Commands/GameActionEvents.cs) | [Shared event infrastructure](../src/Core/Events/InterceptionEvent.cs), [managed hook owner](../src/Core/Events/ManagedInterceptionHook.cs) | [Interception](../examples/ThirdPartyMod/InterceptionExample.cs) |
| HUD interception | [PresentationEvents](../src/Presentation/Events/PresentationEvents.cs) | Named publishers in the same file and shared event infrastructure | [Interception](../examples/ThirdPartyMod/InterceptionExample.cs) |
| Recruitment policies | [Request arithmetic](../src/Units/Recruitment/RecruitmentRequestPolicy.cs), [material UI](../src/Units/Recruitment/RecruitmentMaterialUi.cs) | [Material IL contract](../src/Units/Recruitment/RecruitmentMaterialUiIlContract.cs) | [Interception](../examples/ThirdPartyMod/InterceptionExample.cs) |
| Native market queries | [MarketPriceEvents](../src/Economy/MarketPriceEvents.cs) | [Validated native owner](../src/Economy/MarketPriceNativeRuntime.cs) | [Integration guide](THIRD_PARTY_GUIDE.md#sharing-native-market-price-queries) |

## Side-HUD buttons

`ModApiClient.TryGetHudExtrasButtons` acquires `IHudExtrasButtonsCapability` independently of native Unit HUD hooks or troop selection. `TryRegisterButton` accepts `HudExtrasButtonDefinition`: owner-local ID, command, localized popup text, fresh Noesis Button factory, order (default 0), and optional ToolTip factory. Registration is process-lived; duplicate owner-local IDs are rejected. Existing `IApiShared` interfaces remain unchanged.

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

Native addresses, scanners, memory writers and concrete services are not supported public extension points. Command dispatch and formation implementations live in the consuming mods. Prefer Script Extender APIs directly for ordinary unit commands, pathing data, messages and events already supplied there.

`APIShared.ModSettings.ToolTipPresentation` exposes common tooltip sizes for external XAML without source links. Resolution-sensitive properties must be read on the Unity thread.

Apply `[APIShared.ModSettings.ExcludeFromSavegameModSettings]` to a plugin class
when it owns a separate persistence protocol or its settings are not mission data.
The inherited attribute excludes that plugin's lobby settings from automatic
savegame capture and restoration. It does not disable presets. Participation does
not depend on a particular plugin GUID or another mod's opt-out field.

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

`NativeApiState.Ready` means the global API is published. Capability diagnostics may still report `Pending`, `UnsupportedBuild`, `PatternMissing`, `Ambiguous`, `ValidationFailed`, `Conflict` or `Faulted`. Capability acquisitions and diagnostic-bearing registrations report reasons; boolean queries may instead return false as documented. `ConflictOwnerGuid` identifies an owner where applicable. Native hash fields may be empty for managed services or before native initialization.

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
