# Third-party integration guide

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

## Workshop package cleanup

APIShared automatically installs a permanent staging callback for the audited Script Extender binary, exclusively for cleaning its own previous releases. Foreign `.map` items remain entirely Extender-managed. Unsupported binaries disable this self-cleanup with a warning; other services remain available. Neither Extender sources nor its updater are replaced. The existing inventory helpers and schema described below remain available for packaging and historical evidence; foreign maps are no longer cleaned through this callback.

Generate `apishared-workshop-files.json` inside `BepInEx/plugins/<GUID>` from the final package with `tools/WorkshopPackage.ps1` and `New-ApiWorkshopInventory -Directory <package>`. The existing JSON contract is schema 1, `Guid`, `Version`, `Files` and optional `LegacyFiles`; both file collections contain `{ "Path": "relative/path", "Sha256": "64 lowercase hexadecimal digits" }`. Identity and version must match the package's `info.json`. `Files` lists immutable delivered files, excluding the inventory itself. Never list user settings, captures, generated files or other mutable data. The helper excludes `config` and `LobbyModSettings`; authors must keep all other user data outside package staging.

`LegacyFiles` supplies path/hash alternatives from verified historical releases for users who never had an inventory. Keep applicable historical alternatives in future releases until those installations are no longer supported. APIShared has no package-specific migration paths. Only files absent from the new `Files` collection and matching a recorded old hash are scheduled. Current, unknown, modified, missing and concurrently staged files remain untouched. Paths must be relative forward-slash paths; traversal, Windows updater wildcard characters (including square brackets), device names, alternate streams, duplicate paths and reparse points are rejected. Other subscribed package payloads are inspected even if they have no inventory; competing GUIDs disable cleanup and competing file claims preserve that file.

Deletion is deferred through `_SE/.staging/delete_list.txt`; installed files are never deleted by APIShared. The existing Windows/Proton updater applies deletion after exit and before copying staged replacements. Locked-file/deployment failures are logged and reevaluated at the next startup. This extends update cleanup; the Extender's existing unsubscribe behavior is unchanged.

## Plugin entry points with optional APIShared

Since APIShared 0.7.1, Optional APIShared consumers may call `SavegameModSettings.ExcludePluginFromAutomaticSettings(typeof(MyPlugin))` after dependency validation and before settings registration, instead of applying the exclusion attribute to their independent bootstrap type. This additive, thread-safe registration is idempotent, applies to derived types and lasts until process exit. Existing attribute behavior and other plugins remain unchanged. Set your consumer's minimum APIShared version to 0.7.1 or newer when calling this method.

## Loose Workshop packages

APIShared has no Workshop page in Mod Options. A statically rooted Steam-initialization subscriber discovers first installations, newer receipt versions and confirmed unsubscribes at startup. One bilingual confirmation lists all changes: Yes stages one batch and restarts; No leaves installed files unchanged and suppresses another prompt during that start. Extender work and bootstrap installation take priority. The updater backs up every affected file and commits all receipts after file changes, with recovery for the complete batch. Invalid dependencies or ownership conflicts defer changes safely.

Authors use `BepInEx/tools/APISharedWorkshopPublisher/UploadMod.bat`, shipped by APIShared outside the plugin scan. No running game or separate Steam password is needed; Steam must be running, signed in and online. The wizard accepts a prepared manifest or generates one from Payload, collects prerequisites/dependencies, offers owned SHCDE items and publishing metadata, validates a private snapshot and requires typing `UPLOAD` before submission. `/validate` skips Steam initialization and publishing, `/test` uses private visibility and account-scoped separate IDs, `/nopause` only suppresses the BAT closing pause; `/cancel` cancels a prompt. Saved settings and immediately persisted item IDs live in LocalApplicationData/APIShared/WorkshopPublisher, scoped by app/account/mode/package. Never add this state to uploaded content.

Linux users can run `bash UploadMod-linux.sh`. It uses the existing SHCDE Proton prefix, not a separate uploader. With custom Steam/Proton locations set `STEAM_COMPAT_CLIENT_INSTALL_PATH`, `STEAM_COMPAT_DATA_PATH` (directory containing pfx), and `APISHARED_PROTON` (the selected Proton script). Python 3 is required by Proton; host paths such as `/home/user/My Mod` are accepted by the wizard through Wine Z:. This launcher has automated syntax/argument tests; successful real Proton publishing is still required before claiming Linux support.

Build, ZIP and map contain the same complete tool with a separate `publisher-files.json` inventory. Only the fixed tool filenames are accepted; plugin inventory contracts and runtime binary restrictions remain unchanged. Publisher sources, build and shared Steam state machine belong to APIShared. Existing maintainer drivers continue using the same helper.
This capability is in development. The runtime version is intentionally unchanged during platform testing; assign the next release version before distributing new API consumers and use that release as their minimum dependency. See the compiled [Workshop example](../examples/ThirdPartyMod/WorkshopExample.cs) for explicit preview/confirmation callbacks and task-based failure handling.

The workspace maintainers use `CreateSteamModPack.bat` to prepare GitHub-backed packages, then `UploadWorkshop.bat` to publish the prepared content. Both drivers offer SerpsMods, APIShared, both, or cancel. Automation selects `/modpack`, `/apishared`, or `/both`; combine with `/test`, `/validate`, and `/nopause`. Conflicting target arguments fail. `/validate` performs checks without release changes or uploads. `/nopause` only suppresses the closing pause; explicit publishing confirmations remain.

Build `tools/WorkshopUploader/build.bat` before preparing packages. Set `SHCDE_GAME_DIR` for the first build; subsequent APIShared and publisher builds can reuse local installation paths from `.local/build-proof.json`. Explicit environment settings take precedence, and paths are checked before compilation. Its Windows x64 executable lives separately in `.local/WorkshopUploader`, with a checked build proof. It shares APIShared's internal package validation, content fingerprint and Steam publishing state machine, using its own callback loop. No uploader dependency is added to runtime mods. Failures identify their step and retain allocated Workshop IDs for retry.

Shared maintainer configuration is `Shared/Tools/Steam/WorkshopConfiguration.ps1`. Production content folders are `SerpsMods` and `APISharedWorkshop`. Preparation proofs and IDs are stored outside content in `.release-output/SteamWorkshop`, keyed by `SerpsMods` and `APIShared`. Test content, state, IDs and logs stay below its separate `test` directory. Test uploads are private and reject known production IDs; GitHub test releases remain drafts. Both targets process APIShared first and stop on its failure.

Initially prepare and upload APIShared alone, then prepare SerpsMods using the saved APIShared Workshop ID. No placeholder IDs are accepted. SerpsMods preparation only consumes reviewed APIShared releases and does not publish APIShared implicitly. Reused content ZIPs are checked before replacing any existing output. Upload preparation verifies every declared file and the preparation proof; snapshots omit release/proof files. Confirmed loose uploads do not become historical map inventories or change the next package's content signature.

Manual production preparation and uploads do not require a platform-approval file. The drivers still validate packages, hashes, release identity and Steam ownership, and retain explicit release/upload confirmations. Private Windows/Proton, Steam, migration, external-manager and Extender/Fixes tests are recommended; automated fixture results alone do not certify live platform integration.

Use one prepared upload folder:

```text
apishared-workshop.json
preview.png
OptionalBootstrap.map
Payload/BepInEx/plugins/YourMod/info.json
Payload/BepInEx/plugins/YourMod/YourMod.dll
```

Schema 1 contains `SchemaVersion: 1`, stable `PackageId`, numeric `Version`, `MinimumAPISharedVersion`, optional `MinimumScriptExtenderVersion`/`MaximumScriptExtenderVersion`, nonempty `Roots` (full `BepInEx/plugins/<folder>` paths), and complete `Files` records `{Path, Size, Sha256}` relative to the game root. `Dependencies` records use `Guid`, optional inclusive `MinimumVersion`/`MaximumVersion`, and optional decimal-string `WorkshopId`. `Bootstrap` uses `MapFile`, `Guid`, `Version`, `PluginFolder`; it is optional. `LegacyReleases` contains `{Version, Files}` records verified against historical deliveries. No Workshop identity is taken from a folder name.

Generate the complete inventory using [Prepare-WorkshopPackage.ps1](../tools/Prepare-WorkshopPackage.ps1). Keep mutable settings and runtime output outside Payload. Links, traversal, duplicate paths/GUIDs, protected infrastructure and overlapping ownership are rejected. All delivered info.json identities, including nested mods, are checked. A bootstrap map must contain only its own reserved plugin folder and root info.json. Its root/plugin metadata must agree with the declared GUID/version and BepInEx manifest. The loose host and its GUIDs remain unchanged during migration.

For authors without an existing manifest, `WorkshopPackageDraft` supplies identity, version, prerequisites and optional dependencies/bootstrap/history. `CreateManifestAsync` generates inventory text without changing source files. Passing the draft as `WorkshopUploadOptions.Draft` generates the manifest directly in the prepared immutable snapshot. The external wizard supports a new plain package using ID/version/minimum fields and dependency prompts; prepared manifests retain their dependency/bootstrap declarations. No registered mod or source-code compilation is required for plain publishing.

[New-WorkshopBootstrap.ps1](../tools/New-WorkshopBootstrap.ps1) generates independent bootstrap sources and a build.bat. Supply a unique GUID/folder, retained map filename, version newer than the legacy Extender receipt, APIShared minimum and its separate Workshop ID. Compile through that build.bat with SHCDE_GAME_DIR set, then stage only `info.json` at the root and the bootstrap DLL/matching `info.json` under its plugin folder. Run the Extender's existing map packager, then dot-source `tools/WorkshopPackage.ps1` and call `Set-ApiBootstrapMapPayload -MapPath <map> -Stage <stage>`: the packager's map template otherwise retains preview/config entries. This helper retains the native map prefix and creates/verifies exactly the staged ZIP inventory. There is no APIShared assembly reference. Missing/old APIShared shows a bilingual notice with its Workshop link; no files are installed, repaired or deleted by the bootstrap.

The public Workshop capability remains available for consumer mods with their own explicitly confirmed workflows. Upload snapshots include only declared content and optional bootstrap; changed content requires a greater package version. Steam dependencies are soft links and do not replace local prerequisite checks.

```csharp
// Steam entry points on Unity, after Steamworks initialization. The built-in publisher is an external console wizard.
var workshop = ApiShared.ForMod("YourModGuid").Workshop;
var catalog = await workshop.GetCatalogAsync();
var plan = await workshop.PrepareInstallAsync(itemId, WorkshopAction.Update);
// Present plan.Files, plan.RemovedFiles and plan.Diagnostics, and wait for explicit user confirmation.
var operation = await workshop.ApplyAsync(plan);
// AwaitingRestart means handoff. The receipt, written last by the updater, proves successful installation.
```

Receipt equality suppresses automatic repair even when local DLLs differ or disappear. Updates replace delivered files with backups after consent; obsolete files are removed only when their current hash matches a receipt or verified history. Settings, unknown files and modified obsolete files survive removal and migration. Invalid/missing receipts allow explicitly chosen installation with backup, never an assertion of successful adoption. Steam failures/downloads/missing folders do not mean unsubscribe.

Transactions and upload IDs live under `_APIShared/Workshop`, independently of `_SE`. The external updater checks operation-list hashes, staged bytes, targets and links; backs up before writing; journals each step; and commits the receipt last. On interruption, keep the entire transaction directory. With the game closed, run its installed `Workshop/workshop-updater.ps1` with the original `-GameRoot`, `-Transaction`, `-OperationsHash` and `-Recover`, or the host bash script with the same three positional arguments and `--recover`. Recovery restores the original files/receipt and exits with code 2. Failed recovery retains all evidence. Never manually delete backups while a transaction is unresolved. Proton requires Wine Z: paths and host bash/flock/coreutils; paths that cannot be translated are rejected before game exit.

Extender staging/download restarts take precedence. Under `SCDEModManagerLaunchId`, installation remains read-only, including restart and reactivation; uploads are available. The external manager's current importer is separate. APIShared's new format does not add an importer to it. Publish APIShared's independently managed map first; test migration at the existing modpack ID using the same map filename and a separate bootstrap GUID before replacing the live release. Do not publish the new format until real Windows/Proton and Steam tests pass.

Platform-test records are optional and are not read by the maintainer release/upload drivers. Missing, outdated or malformed records do not block manual publication. SerpsMods publishes a separate Workshop-content ZIP alongside its ordinary plugin ZIP; reusing a release restores the complete verified content ZIP, including manifest and Payload, rather than just its bootstrap map.
