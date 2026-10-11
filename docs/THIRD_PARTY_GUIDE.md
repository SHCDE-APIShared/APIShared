# Third-party integration guide

## Multiplayer actions and messages

Since APIShared 0.7.0, use `ApiShared.ForMod(yourPluginGuid).Network` to register a typed channel once from
the validated Unity-thread `LibraryLoaded` entry, after Extender built-in packet
registration. Registration must be unconditional and in identical order on every
peer. Extender packet IDs depend on registration order; GUIDs do not negotiate IDs
or guarantee compatibility. Every peer needs compatible mods, packet schemas and
explicit `[MessagePackFormatter]` implementations. Duplicate owner-local IDs or
two channels of the same transport kind for one packet type are rejected. One
Chore channel and one message channel may deliberately share a mixed-protocol type.
Channels and subscriptions stay rooted until process exit; use logical activation,
never component teardown. No new mandatory member was added to `IApiShared`.

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

## Script Extender bugfixes

Since 0.6.1, APIShared includes targeted corrections for Script Extender and bundled dependencies,
implemented in `src/ScriptExtenderFixes`. They work without SerpsMods or a consumer
registration. They do not replace the Extender and do not change existing public
APIShared capability contracts.

The initial fix addresses an intermittent Alt+F4 crash in the audited UU-ImGUI API
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
provider if you use that combined heading. Legacy notice properties remain unchanged.

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

Bind host/client UI sections to `CanEditHostSettings` and `CanEditClientSettings`. Bind common preset/source actions to the public `System_*` properties and commands; do not replace them with a second persistence engine. Use nonempty tooltips with `ToolTipService.ShowDuration="60000"`. Settings-search metadata is optional when the control title/tooltip layout is unambiguous.

The example includes a minimal host/local settings tab. Use localized labels and override `ResolveSettingsUiText` for translated common actions in a production mod. APIShared supplies English fallbacks. Shared file formats and legacy `__Serp*` persistence keys are retained for existing saves; they do not restrict mod GUIDs or require the Serps pack.

## Migration and packaging

Older APIShared releases exposed settings and mode types under `Shared`. When updating such a consumer, use `APIShared.ModSettings` and `APIShared.GameModes`, update its XAML namespace imports, and rebuild against the selected APIShared release. Author-specific permission tables belong in consumers, rather than APIShared; construct profiles for your own rules. Command/formation implementations belong to their consuming mods; use the public route-search events for the documented preference extension.

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

## Extending APIShared

Use the [catalog source map](API_CATALOG.md#source-map) to find the current contract
and its implementation. The [architecture guide](../ARCHITECTURE.md#finding-and-extending-a-feature)
explains where shared behavior, hook ownership and consumer algorithms belong.
Follow [CONTRIBUTING](../CONTRIBUTING.md) for setup and tests. An ordinary clone of
this repository is sufficient; no maintainer workspace or AI assistant is required.

## Assassin obstacle completion

Assassin obstacle control uses `AssassinAttackControlAPI.RegisterGuard(ownerGuid, callback)`
for legacy idle-only completion, or its overload with `AssassinObstacleCompletionMode.NativeRetarget`
to run Vanilla's existing objective-based target selection after a veto. The synchronous callback
receives a one-based unit game ID on the native simulation thread and must not issue commands.
One owner holds the permanent hook; consumers implement their own logical activation predicate.
Native selection can fail or select another vetoed obstacle; it does not guarantee a successful route.
The experimental `IAssassinTraversalView`/traversal-provider API was removed with maintainer approval;
no consumer-defined alternative search or mainmod prerequisite is involved in obstacle completion.
