# Third-party integration guide

## Adding a side-HUD button

Use `ModApiClient.TryGetHudExtrasButtons`, then `IHudExtrasButtonsCapability.TryRegisterButton` with a `HudExtrasButtonDefinition` (see `HudExample.RegisterSideButtons`). Return a fresh unattached Noesis Button, using `context.Hud.TryFindResource` for Vanilla styles/sprites. APIShared controls its 36 x 34 size, position, command and availability; the command parameter is the actual Button. Do not retain the HUD context or calculate button coordinates.

Keep the returned handle in your process-owned runtime. `SetVisible` removes an entry from layout, `SetEnabled` blocks its command, `SetTooltip` updates localized text, and `RequestContentRefresh` rebuilds button/tooltip or retries a failed factory. `SetOwnerActive` applies to all this owner's entries. Buttons are sorted by order (default 0), then ordinal GUID and ID, bottom to top; five entries appear per page with a cycling arrow. Vanilla HUD hiding and its reserved button slot are inherited automatically.

Nonempty text gets the modoptions popup style by default; empty text creates no popup. To deliberately supply a different presentation, pass `tooltipFactory`, returning a fresh ToolTip with explicit Style or Template. APIShared sets Content to the current localized text and duration to 60000 ms. Factories and commands run on the Unity thread and must not block; exceptions are isolated. Side-HUD actions do not require troop selection, and consumers retain their own gameplay/network authorization.

## Adding an own-troop HUD button

Use the existing owner-bound Unit HUD capability and cast it to
`IUnitHudActionButtonsCapability` (see `examples/ThirdPartyMod/HudExample.cs`).
Register a stable button ID, command, localized tooltip and a factory returning
fresh Noesis content no larger than 35 by 35 HUD units. Optional `order` controls
sorting; equal orders use mod GUID and button ID, independently of loading order.
Keep the returned registration in your process-owned runtime. Use `SetVisible`,
`SetEnabled`, `SetTooltip` and `RequestContentRefresh` to update it; do not dispose
the shared HUD or hooks. The optional owner-activation interface also applies.

APIShared handles placement, paging, own-selection authorization and HUD replacement.
Use the factory context's remapped `PlayerColour` rather than assuming that player
ID equals colour. Factories, context/hover callbacks and commands run on the Unity
thread with exception isolation. They must not block. The optional context callback
can close a consumer popup when the own HUD becomes unavailable. A foreign display,
empty selection or spectator has no action-button context. Consumers still own
network synchronization and gameplay authorization for any command they implement.

Action-button descriptions and page-arrow text appear only at the Vanilla troop rollover location. `SetTooltip` updates that description; it does not create a Noesis popup. Category and recruitment-variant descriptions also use Vanilla rollover text. Mod-owned popup tooltips elsewhere must explicitly use the mod-options style, never the default lion-frame template.

## Initialization and ownership

Use your plugin's stable GUID for `ApiShared.ForMod`. Keep the resulting client, logger, settings and callbacks in a static runtime or a long-lived publisher. Register managed lifecycle services from `Awake`, after the hard APIShared dependency has initialized. Acquire native services in `WhenReady`. Never treat a native failure as proof that all managed services failed.

The example library compiles against ordinary public assemblies and has no `InternalsVisibleTo` access. Build it with the game path supplied through `GameDir`; its APIShared reference defaults to this repository's output for verification and can be overridden with `ApiSharedDir` for normal installed use. APIShared's build driver compiles it as a consumer check, but does not install it into the game.

## Optional game-mode permissions

Read `MissionLifecycleNotification.Context.Mode` for the event being handled. Use `GameModeHelper.Capture()` for a current snapshot where appropriate. Capture itself applies no permissions. Construct `GameplayModActivationProfile` with your own GUID, allowed contexts and `allowRealMultiplayer`, then call `GameplayModModePolicy.IsAllowed`. You can instead implement your own policy directly from the snapshot.

The built-in optional evaluator rejects unknown or conflicting origins. Its existing contexts do not offer a tutorial permission. These defaults do not prevent a third-party mod from making its own informed policy decision without the evaluator. APIShared contains no author-specific GUID or feature-permission tables; consumers own those decisions.

## Presets without SerpsModsHost

Derive your settings from `APIShared.ModSettings.PresetLobbyModSettingsViewModel` and register through `LobbyModSettingsPresetRegistration.Register`. The Script Extender displays the mod tab; APIShared owns discovery, personal preset files, persistence and lobby convergence. SerpsModsHost and ExtendedData are optional consumers, not prerequisites.

Classify saved properties with Script Extender `[SyncHostOnly]` or `[SyncPerPlayer]`, or APIShared `[PresetLocal]`. Each per-player property needs a public compatible `<Name>Data` array for slots 0..8. Keep setters behind `CanMutateSetting()` and call `OnPropertyChanged` after actual changes. Configure additional per-player rules through `ConfigurePerPlayerLobbySettings`; do not implement another roster poller.

Bind host/client UI sections to `CanEditHostSettings` and `CanEditClientSettings`. Bind common preset/source actions to the public `System_*` properties and commands; do not replace them with a second persistence engine. Use nonempty tooltips with `ToolTipService.ShowDuration="60000"`. Settings-search metadata is optional when the control title/tooltip layout is unambiguous.

The example includes a minimal host/local settings tab. Use localized labels and override `ResolveSettingsUiText` for translated common actions in a production mod. APIShared supplies English fallbacks. Shared file formats and legacy `__Serp*` persistence keys are retained for existing saves; they do not restrict mod GUIDs or require the Serps pack.

## Migration and packaging

Older APIShared releases exposed settings and mode types under `Shared`. When updating such a consumer, use `APIShared.ModSettings` and `APIShared.GameModes`, update its XAML namespace imports, and rebuild against the selected APIShared release. Author-specific permission tables belong in consumers, rather than APIShared; construct profiles for your own rules. Command/formation implementations belong to their consuming mods; use the public route-search events for the documented preference extension.

Release consumers with hard minimum APIShared/Script Extender versions and `<Private>false</Private>` for runtime references. Never include APIShared.dll, Script Extender DLLs or game DLLs in the consumer package. Put your XAML under the usual mod Override directory. Keep one centrally installed APIShared instance.
