# API catalog

Capabilities are acquired through `ModApiClient` or `IApiShared`. The table describes the existing contracts, not a guarantee of support for every game build. Always inspect returned diagnostics. All unit/building/player game IDs are one-based where documented; array indices are not game IDs.

| Area / entry | Purpose and availability | Thread, ownership and lifetime |
|---|---|---|
| `TryGetMissionLifecycle` | Managed initialization checkpoints, completed mission start/end and current context | Register on Unity thread; publisher-thread notifications; owner/registration ordering; late start replay only; process lifetime |
| `TryGetLobbyState` | Managed immutable multiplayer-lobby snapshots | Register on Unity thread; immediate known-state replay; deterministic owner/ID ordering; process lifetime |
| `TryGetPlayerDefeat` | Managed one-shot lord death and official loss transitions | Tick/simulation publisher; no initial-state replay; do not change UI directly; process lifetime |
| `TryGetUnitHudPresentation` | Categories, image overrides, interactions, recruitment tickets and control groups | Unity/Noesis presentation; owner-local IDs; ambiguity preserves Vanilla; logical activation extension; process lifetime |
| `TryGetBriefingGoldPresentation` | Ordered adjustments after Vanilla briefing calculation | Presentation publisher; stage/owner/ID ordering; invalid results preserve last safe value; process lifetime |
| `TryGetGatehouseTiming` | Typed timing/distance settings | Native validation required; exclusive owner; permanent hook and logical state; automation via `IGatehouseAutomationCapability` |
| `TryGetGatehouseDistanceOrigin` | Vanilla begin coordinate or complete-bounds center | Native validation required; exclusive owner; permanent runtime state |
| `TryGetBuildingRepair` | Repair quote and repair-tooltip presentation | Acquired on demand after native initialization; respect each operation's contract; UI on Unity thread |
| `TryGetAivBuildStep` | Before/after observation around one unchanged Vanilla call | Native caller thread; deterministic begin order and reverse completion; owner-local IDs; process lifetime |
| `APIShared.GameModes` | Mode snapshots, caller-defined contexts and optional permissions | No automatic permission enforcement; use the relevant mission snapshot; explicit multiplayer policy |
| `APIShared.ModSettings` | Settings base class, registration, presets, sources and search | Unity-thread UI/registration; existing host/per-player sync; personal persistence remains isolated |

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

`NativeApiState.Ready` means the global API is published. Capability diagnostics may still report `Pending`, `UnsupportedBuild`, `PatternMissing`, `Ambiguous`, `ValidationFailed`, `Conflict` or `Faulted`. Each failed operation returns a reason; `ConflictOwnerGuid` identifies an owner where applicable. Native hash fields may be empty for managed services or before native initialization.

Registration IDs are unique within an owner and service; stable IDs allow deterministic ordering. Registrations do not imply replacement or unsubscription. Callback exceptions are isolated where the service explicitly documents that contract; native Vanilla exceptions retain the original propagation rules. No API-wide promise of Unity-thread dispatch exists. Use logical activation to suspend supported presentation features; never dispose a published process-wide hook.

## Public type index

The following index includes data contracts and advanced APIs. XML documentation in `APIShared.xml` provides member-level details.

### Core

`APISharedPlugin`, `ApiShared`, `IApiShared`, `ModApiClient`, `NativeApiState`, `NativeCapabilityDiagnostic`, `NativeCapabilityIds`, `NativeCapabilityState`.

### Missions

`IMissionLifecycleCapability`, `MissionContext`, `MissionEndReason`, `MissionInitializationPhase`, `MissionLifecycleKind`, `MissionLifecycleNotification`, `MissionMapType`, `MissionStartKind`.

### Lobby

`ILobbyStateCapability`, `LobbyPreparationOverride`, `LobbyStateSnapshot`.

### Players

`IPlayerDefeatCapability`, `PlayerDefeatNotification`, `PlayerLordDeathNotification`, `PlayerPerspectiveAPI`.

### Units

`LocalSelectionAPI`, `LocalSelectionSnapshot`, `MarkedUnitSelectionAPI`, `MarkedUnitSelectionSnapshot`, `UnitAccess`, `UnitLookupFailure`.

### Presentation

`BriefingGoldAdjustmentStage`, `BriefingGoldContext`, `IBriefingGoldPresentationCapability`, `IUnitHudActivationCapability`, `IUnitHudPresentationCapability`, `UnitHudCategoryDefinition`, `UnitHudCategorySnapshot`, `UnitHudControlGroupSnapshot`, `UnitHudImageOverrideContext`, `UnitHudImageOverrideDefinition`, `UnitHudImageSlot`, `UnitHudInteractionContext`, `UnitHudMouseButton`, `UnitHudRecruitmentTicket`, `UnitHudSlotSnapshot`, `UnitHudSurface`, `UnitHudTextKind`, `UnitHudTextProfile`, `UnitHudTint`, `UnitHudUnitSnapshot`.

### Buildings

`BuildingRepairQuote`, `GatehouseDistanceOrigin`, `GatehouseDrawbridgeCoupling`, `GatehouseFootprintCandidate`, `GatehouseTimingSettings`, `GatehouseTimingValues`, `IBuildingRepairCapability`, `IGatehouseAutomationCapability`, `IGatehouseDistanceOriginCapability`, `IGatehouseTimingCapability`, `RepairTooltipEntry`, `RepairTooltipViewModel`.

### GameModes

`CustomizedLaunchOrigin`, `CustomizedLaunchOriginKind`, `CustomizedLaunchOrigins`, `GameModeHelper`, `GameModeKind`, `GameModeLaunchVariant`, `GameModeSnapshot`, `GameTrailType`, `GameplayModActivationProfile`, `GameplayModAllowedContext`, `GameplayModModePolicy`.

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

### ModSettings

`DynamicPresetSetting`, `ExcludeFromSavegameModSettingsAttribute`, `IDynamicPresetSettingsProvider`, `IModSettingsApplicationBackend`, `IModSettingsMissionSourceEndpoint`, `IModSettingsPresetEndpoint`, `IModSettingsWorkingCopyEndpoint`, `IModSettingsWorkingSourceProvider`, `INetworkModSettingsApplicationBackend`, `LobbyModSettingsPresetRegistration`, `ModSettingsApplication`, `ModSettingsPresetJson`, `ModSettingsPresetListEntry`, `ModSettingsPresetSaveTarget`, `ModSettingsPresetSourceKind`, `ModSettingsSearch`, `ModSettingsSearchEntry`, `ModSettingsSearchMatcher`, `ModSettingsSearchVisibilityConverter`, `ModSettingsWorkingSource`, `ModSettingsWorkingSourceKind`, `ModSettingsWorkingSourceRegistry`, `PerPlayerLobbySettingsBuilder`, `PerPlayerLobbySnapshot`, `PresetLobbyModSettingsViewModel`, `PresetLocalAttribute`, `PresetSaveBulkMode`, `PresetSaveSelection`, `PresetSaveSettingViewModel`, `PresetSettingDescriptor`, `PresetSettingScope`, `PublishedModSettingsPreset`, `PublishedPresetSetting`, `PublishedPresetValueMode`, `RequiresRestartAttribute`, `ToolTipPresentation`.

### Pathfinding

`RouteSearchEvents`, `RouteSearchContext`, `RouteSearchTerrain`,
`RouteSearchPreEventArgs`, `RouteSearchPostEventArgs` form the targeted additional
route-calculation extension described above.

`AssassinAttackControlAPI`, `AssassinGateTransitionPolicy`, `AssassinPathAPI`, `AssassinTransitionKind`, `ElevatedMoatAiCapability`, `ElevatedMoatAiState`, `EnemyBridgeDiagnosticBridge`, `EnemyGatePathPolicyBridge`, `EnemyGateSearchKind`, `IAssassinTraversalView`, `IEnemyBridgePathObserver`, `IEnemyBridgeTopologyObserver`, `IEnemyGateAssassinObserver`, `IEnemyGateClimbRoutePolicySnapshot`, `IEnemyGatePathPolicy`, `IEnemyGateRegionPairObserver`, `IEnemyGateRoutePolicyProvider`, `IEnemyGateRoutePolicySnapshot`, `ITemporaryAssassinGateObserver`, `ITemporaryGateRouteAcceptanceObserver`, `TemporaryGateRouteAcceptanceBridge`.

### Diagnostics

`AivBuildStepCompletion`, `AivBuildStepContext`, `IAivBuildStepCapability`, `IAivBuildStepInvocation`, `IAivBuildStepObserver`.

### Savegames

`SavegameLoadChoiceState`, `SavegameModSettings`, `SavegameModSettingsRecord`, `SavegameModSettingsRecordFormatter`, `TrailCreatorRule`.
