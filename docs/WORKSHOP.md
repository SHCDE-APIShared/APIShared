# Workshop packages and publishing

This guide is for package authors, consumers of the Workshop capability and
contributors maintaining its deployment integration. APIShared self-cleanup,
Extender-managed maps and explicitly declared loose packages are separate paths.

- [Public capability](#workshop-publishing-and-loose-packages): ownership, threading and plans.
- [File inventories](#workshop-package-cleanup): APIShared self-cleanup and schema.
- [Publishing](#loose-workshop-packages): the external uploader and platform limits.
- [Package format](#package-format-and-bootstrap): inventory and bootstrap preparation.
- [Installation and recovery](#installation-and-recovery): receipts, embedded-copy replacement and transactions.
- [Internal implementation](#internal-implementation): staging hooks and persistent publishers.

## Workshop inventory

`APIShared.Workshop.WorkshopInventory.FileName` identifies the schema-1 file inventory. The Extender staging callback cleans only obsolete APIShared package files; foreign `.map` packages are Extender-owned. See [Workshop package cleanup](#workshop-package-cleanup).

## Workshop publishing and loose packages

APIShared's built-in Workshop workflow has no Mod Options page. Startup prepares subscribed package changes for one explicit confirmation and a single post-exit batch update. Authors use the shipped UploadMod.bat or UploadMod-linux.sh under BepInEx/tools/APISharedWorkshopPublisher; both use the same APIShared publisher. Linux execution through the existing Proton prefix awaits practical publishing verification.

`ApiShared.ForMod(guid).Workshop` is independent of native capability readiness. Its [public client](../src/Workshop/WorkshopClient.cs) validates packages, captures a subscription catalog, queries owned SHCDE items, prepares upload snapshots and owner-bound installation plans, and explicitly publishes or applies a plan. Calls that access Steam require initialized Steamworks on Unity; file preparation runs on workers. Failures throw with the relevant step, and submitted operations expose status, diagnostic, operation ID and Unity-thread Changed notifications. Closing a view does not cancel a submitted request.

The [schema and workflow](#loose-workshop-packages) use a top-level `apishared-workshop.json` plus `Payload/BepInEx/plugins/...`. `info.json` contracts are authoritative for individual mods. Pure maps without this manifest stay with Extender. Optional bootstrap maps have an independently validated, exclusively reserved folder. `PrepareInstallAsync` never changes installed files; `ApplyAsync` is an explicit consent boundary and exits the game only after launching a separate updater. `AwaitingRestart` describes handoff, not a successful receipt commit. External managed launches reject Apply while retaining read-only discovery and upload.

Release versions are numeric, three or four parts. Only newer receipted releases offer Update; explicit Reinstall/Downgrade are separate actions. Missing, modified or disabled DLLs do not trigger automatic repair. Backups, hash-guarded obsolete-file removal and final receipt commit preserve settings and unknown files. Windows and Proton live tests and Steam account tests are recommended before distributing loose packages; passing file-policy tests alone does not certify platform integration. Manual release and upload drivers do not require a platform-approval file.

An embedded APIShared copy requires explicit package replacement; subscribing to the central map item alone leaves that copy installed. The Extender installs the central manager first. The confirmed payload transaction removes only files inside the embedded payload root whose hashes match verified deliveries. Modified or unproven files remain. Bootstrap readiness checks both the selected plugin path and the actual loaded assembly path, and preparation requires the central version to exceed every verified embedded release.

## Workshop package cleanup

APIShared automatically installs a permanent staging callback for the audited Script Extender binary, exclusively for cleaning obsolete files from its own installed packages. Foreign `.map` items remain entirely Extender-managed. Unsupported binaries disable this self-cleanup with a warning; other services remain available. Neither Extender sources nor its updater are replaced. The inventory helpers and schema below describe delivered files and verified release hashes. This callback does not clean foreign maps.

Generate `apishared-workshop-files.json` inside `BepInEx/plugins/<GUID>` from the final package with `tools/WorkshopPackage.ps1` and `New-ApiWorkshopInventory -Directory <package>`. The JSON contract is schema 1, `Guid`, `Version`, `Files` and optional `LegacyFiles`; both file collections contain `{ "Path": "relative/path", "Sha256": "64 lowercase hexadecimal digits" }`. Identity and version must match the package's `info.json`. `Files` lists immutable delivered files, excluding the inventory itself. Never list user settings, captures, generated files or other mutable data. The helper excludes `config` and `LobbyModSettings`; authors must keep all other user data outside package staging.

`LegacyFiles` supplies path/hash alternatives from verified historical releases for users who never had an inventory. Keep applicable historical alternatives in future releases until those installations are no longer supported. Cleanup is driven by inventories and hashes, not package-specific paths. Only files absent from the new `Files` collection and matching a recorded old hash are scheduled. Current, unknown, modified, missing and concurrently staged files remain untouched. Paths must be relative forward-slash paths; traversal, Windows updater wildcard characters (including square brackets), device names, alternate streams, duplicate paths and reparse points are rejected. Other subscribed package payloads are inspected even if they have no inventory; competing GUIDs disable cleanup and competing file claims preserve that file.

Deletion is deferred through `_SE/.staging/delete_list.txt`; installed files are never deleted by APIShared. The existing Windows/Proton updater applies deletion after exit and before copying staged replacements. Locked-file/deployment failures are logged and reevaluated at the next startup. The Extender also owns removal of unsubscribed map items.

## Loose Workshop packages

APIShared has no Workshop page in Mod Options. A statically rooted Steam-initialization subscriber discovers first installations, newer receipt versions and confirmed unsubscribes at startup. One bilingual confirmation lists all changes: Yes stages one batch and restarts; No leaves installed files unchanged and suppresses another prompt during that start. Extender work and bootstrap installation take priority. The updater backs up every affected file and commits all receipts after file changes, with recovery for the complete batch. Invalid dependencies or ownership conflicts defer changes safely.

Startup diagnostics use timestamped Info, Warning and Error entries. Warnings explain policy blockers (including every conflicting plugin/asset GUID with its existing path and allowed payload roots, package versions and Workshop ID). Errors identify technical failures with their step and exception details, including unreadable manifests or receipts and updater handoff failures. Each startup attempt ends with a result: current, blocked, declined, awaiting restart or failed; blocked items and technical failures cannot be reported as current. Awaiting restart confirms handoff only, not a committed installation. Repeated Steam initialization does not repeat the decision or its warnings. Pure Script Extender maps remain outside this package catalog.

Authors use `BepInEx/tools/APISharedWorkshopPublisher/UploadMod.bat`, shipped by APIShared outside the plugin scan. No running game or separate Steam password is needed; Steam must be running, signed in and online. The wizard accepts a prepared manifest or generates one from Payload, collects prerequisites/dependencies, offers owned SHCDE items and publishing metadata, validates a private snapshot and requires typing `UPLOAD` before submission. `/validate` skips Steam initialization and publishing, `/test` uses private visibility and account-scoped separate IDs, `/nopause` only suppresses the BAT closing pause; `/cancel` cancels a prompt. Saved settings and immediately persisted item IDs live in LocalApplicationData/APIShared/WorkshopPublisher, scoped by app/account/mode/package. Never add this state to uploaded content.

Linux users can run `bash UploadMod-linux.sh`. It uses the existing SHCDE Proton prefix, not a separate uploader. With custom Steam/Proton locations set `STEAM_COMPAT_CLIENT_INSTALL_PATH`, `STEAM_COMPAT_DATA_PATH` (directory containing pfx), and `APISHARED_PROTON` (the selected Proton script). Python 3 is required by Proton; host paths such as `/home/user/My Mod` are accepted by the wizard through Wine Z:. This launcher has automated syntax/argument tests; successful real Proton publishing is still required before claiming Linux support.

Build, ZIP and map contain the same complete tool with a separate `publisher-files.json` inventory. Only the fixed tool filenames are accepted; plugin inventories and runtime binary restrictions are separate contracts. Publisher sources, build and shared Steam state machine belong to APIShared. Maintainer drivers use the same helper.
This capability is under platform testing in the source checkout. Before distributing API consumers, choose a published release containing the capability and declare that release as their minimum dependency. See the compiled [Workshop example](../examples/ThirdPartyMod/WorkshopExample.cs) for explicit preview/confirmation callbacks and task-based failure handling.

### Maintainer workspace workflow

The workspace maintainers use `CreateSteamModPack.bat` to prepare GitHub-backed packages, then `UploadWorkshop.bat` to publish the prepared content. Both drivers offer SerpsMods, APIShared, both, or cancel. Automation selects `/modpack`, `/apishared`, or `/both`; combine with `/test`, `/validate`, and `/nopause`. Conflicting target arguments fail. `/validate` performs checks without release changes or uploads. `/nopause` only suppresses the closing pause; explicit publishing confirmations remain.

Build `tools/WorkshopUploader/build.bat` before preparing packages. Set `SHCDE_GAME_DIR` for the first build; subsequent APIShared and publisher builds can reuse local installation paths from `.local/build-proof.json`. Explicit environment settings take precedence, and paths are checked before compilation. Its Windows x64 executable lives separately in `.local/WorkshopUploader`, with a checked build proof. It shares APIShared's internal package validation, content fingerprint and Steam publishing state machine, using its own callback loop. No uploader dependency is added to runtime mods. Failures identify their step and retain allocated Workshop IDs for retry.

Shared maintainer configuration is `Shared/Tools/Steam/WorkshopConfiguration.ps1`. Production content folders are `SerpsMods` and `APISharedWorkshop`. Preparation proofs and IDs are stored outside content in `.release-output/SteamWorkshop`, keyed by `SerpsMods` and `APIShared`. Test content, state, IDs and logs stay below its separate `test` directory. Test uploads are private and reject known production IDs; GitHub test releases remain drafts. Both targets process APIShared first and stop on its failure.

Initially prepare and upload APIShared alone, then prepare SerpsMods using the saved APIShared Workshop ID. No placeholder IDs are accepted. SerpsMods preparation only consumes reviewed APIShared releases and does not publish APIShared implicitly. Reused content ZIPs are checked before replacing any existing output. Upload preparation verifies every declared file and the preparation proof; snapshots omit release/proof files. Confirmed loose uploads do not become map inventories or change the next package's content signature.

Manual production preparation and uploads do not require a platform-approval file. The drivers still validate packages, hashes, release identity and Steam ownership, and retain explicit release/upload confirmations. Private Windows/Proton, Steam, embedded-copy replacement, external-manager and Extender/Fixes tests are recommended; automated fixture results alone do not certify live platform integration.

### Package format and bootstrap

Use one prepared upload folder:

```text
apishared-workshop.json
preview.png
OptionalBootstrap.map
Payload/BepInEx/plugins/YourMod/info.json
Payload/BepInEx/plugins/YourMod/YourMod.dll
```

Schema 1 contains `SchemaVersion: 1`, stable `PackageId`, numeric `Version`, `MinimumAPISharedVersion`, optional `MinimumScriptExtenderVersion`/`MaximumScriptExtenderVersion`, nonempty `Roots` (full `BepInEx/plugins/<folder>` paths), and complete `Files` records `{Path, Size, Sha256}` relative to the game root. `Dependencies` records use `Guid`, optional inclusive `MinimumVersion`/`MaximumVersion`, and optional decimal-string `WorkshopId`. `Bootstrap` uses `MapFile`, `Guid`, `Version`, `PluginFolder`; it is optional. `LegacyReleases` contains `{Version, Files}` records verified against historical deliveries. No Workshop identity is taken from a folder name.

Generate the complete inventory using [Prepare-WorkshopPackage.ps1](../tools/Prepare-WorkshopPackage.ps1). Keep mutable settings and runtime output outside Payload. Links, traversal, duplicate paths/GUIDs, protected infrastructure and overlapping ownership are rejected. All delivered info.json identities, including nested mods, are checked. A bootstrap map must contain only its own reserved plugin folder and root info.json. Its root/plugin metadata must agree with the declared GUID/version and BepInEx manifest. Package replacement preserves the loose host identity and GUIDs.

For authors without an existing manifest, `WorkshopPackageDraft` supplies identity, version, prerequisites and optional dependencies/bootstrap/history. `CreateManifestAsync` generates inventory text without changing source files. Passing the draft as `WorkshopUploadOptions.Draft` generates the manifest directly in the prepared immutable snapshot. The external wizard supports a new plain package using ID/version/minimum fields and dependency prompts; prepared manifests retain their dependency/bootstrap declarations. No registered mod or source-code compilation is required for plain publishing.

[New-WorkshopBootstrap.ps1](../tools/New-WorkshopBootstrap.ps1) generates independent bootstrap sources and a build.bat. Supply a unique GUID/folder, retained map filename, version newer than the legacy Extender receipt, APIShared minimum and its separate Workshop ID. Compile through that build.bat with SHCDE_GAME_DIR set, then stage only `info.json` at the root and the bootstrap DLL/matching `info.json` under its plugin folder. Run the Extender's existing map packager, then dot-source `tools/WorkshopPackage.ps1` and call `Set-ApiBootstrapMapPayload -MapPath <map> -Stage <stage>`: the packager's map template otherwise retains preview/config entries. This helper retains the native map prefix and creates/verifies exactly the staged ZIP inventory. There is no APIShared assembly reference. Missing/old APIShared shows a bilingual notice with its Workshop link; no files are installed, repaired or deleted by the bootstrap.

### Installation and recovery

Automatic startup discovery waits inside the Script Extender Steam-initialization event until the player decides. File validation and staging run in the background; only APIShared continuations return to the blocked game thread. The Extender handles its maps first. An installed bootstrap is not a payload receipt: existing plugin files without a receipt are offered for confirmed adoption with backup. Declining preserves files and offers the pending change again on the next start. Asset GUIDs with author-defined, nonnumeric versions satisfy dependencies without version bounds; bounded dependencies require a comparable numeric version. Loaded plugin versions take precedence over asset metadata.

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

Receipt equality suppresses automatic repair even when local DLLs differ or disappear. Updates replace delivered files with backups after consent; obsolete files are removed only when their current hash matches a receipt or verified history. Settings, unknown files and modified obsolete files survive removal and package replacement. Invalid/missing receipts allow explicitly chosen installation with backup, never an assertion of successful adoption. Steam failures/downloads/missing folders do not mean unsubscribe.

Transactions and upload IDs live under `_APIShared/Workshop`, independently of `_SE`. The external updater checks operation-list hashes, staged bytes, targets and links; backs up before writing; journals each step; and commits the receipt last. Keep the entire transaction directory after an interruption, including backups and journals.

Before offering ordinary updates, startup inspects unfinished installations and presents a recovery dialog. When rollback is available, **Yes** restores the previous state, restarts, and offers the current update again; **No** restores the previous state and closes the game without starting another installation; **Cancel** postpones recovery. The next manual start after rollback offers available updates normally. No persistent update suppression is stored. When rollback is unavailable, the dialog explains why and offers complete repair or postponement. Complete repair requires an additional confirmation of the affected current packages. It backs up existing delivered files, replaces the current package inventory and preserves settings, unknown files and obsolete files. All affected packages must be identifiable, subscribed, available and cover the interrupted operations. Unreadable operation lists or unavailable packages require manual assistance; they are never bypassed by deleting journals.

Recovery checks every required backup before restoring any target. Independent interrupted transactions can be restored together; overlapping transactions require complete repair. A fresh recovery request has independent readiness and cancellation files, so a cancelled old installation cannot cancel newly approved recovery. Already restored targets can be skipped after another interruption. The request also retains changed interrupted bytes in `RecoveryBackup`. File changes happen only after game exit, with the updater lock held and no pending Extender staging. Failures retain evidence and are reported with the transaction path; startup logs previous outcomes and offers recovery again when required.

Manual recovery remains supported with the game closed: run the installed `Workshop/workshop-updater.ps1` with the original `-GameRoot`, `-Transaction`, `-OperationsHash` and `-Recover`, or the host bash script with the same three positional arguments and `--recover`. Successful manual recovery restores original files/receipt and exits with code 2. Never manually delete backups while a transaction is unresolved. Proton requires Wine Z: paths and host bash/flock/coreutils; paths that cannot be translated are rejected before game exit.

### Automatic history retention

Startup waits for an external maintenance pass before recovery inspection and update planning. The updater also runs maintenance after successful installation or recovery, before automatic restart. This mode only touches `_APIShared/Workshop/Transactions` and its single pending cleanup worklist; it does not require game exit, change installed files, or modify `Receipts`, `Publishing` or `Uploads`. Externally managed installations remain read-only. No maintenance dialogs are shown. Warnings never roll back a committed installation.

The two newest validated terminal installation/update transactions per Workshop item retain their backups. A mixed batch is retained whenever any of its items needs it. Verified completed `New` copies can be removed independently. New completions have an immutable UTC sidecar; legacy histories use journal modification time, with transaction ID as the deterministic tie-breaker. Open transactions, incomplete metadata, changed content, unknown files and links are retained with diagnostics. Recovery requests and their interrupted-byte snapshots are retained while an original still needs them. Therefore unresolved or ambiguous history can exceed the ordinary two-per-item bound.

Replacement proofs and recovery references form a dependency graph: an old resolved failure is removed before the successful replacement proving it resolved. A locked or partially removed predecessor keeps that proof. A checksummed `cleanup.pending.tsv` records the exact approved file hashes before deletion; interrupted cleanup resumes rather than reclassifying half-deleted metadata. Approved journals are retired before operations metadata, so partial deletion cannot leave a newly unrecognizable applying transaction. Unknown or changed content stops the affected deletion. Completed worklists are deleted; no permanent record or archive is created per removed transaction. Summary output reports removed transactions, freed bytes and deferred cases. Windows uses the updater's exclusive file handle and Proton its real host `flock`; managed staging is serialized with startup maintenance.

Extender staging/download restarts take precedence. Under `SCDEModManagerLaunchId`, installation remains read-only, including restart and reactivation; uploads are available. The external manager's current importer is separate. Loose APIShared packages require an importer explicitly supported by that manager. Publish APIShared's independently managed map first; test package replacement at the existing modpack ID using the same map filename and a separate bootstrap GUID before replacing the live release. Do not publish loose packages until real Windows/Proton and Steam tests pass.

Platform-test records are optional and are not read by the maintainer release/upload drivers. Missing, outdated or malformed records do not block manual publication. SerpsMods publishes a separate Workshop-content ZIP alongside its ordinary plugin ZIP; reusing a release restores the complete verified content ZIP, including manifest and Payload, rather than just its bootstrap map.

## Internal implementation

### Workshop staging integration

`Workshop/WorkshopCleanupRuntime` is rooted by a static `ILHook` installed in APIShared `Awake`. It intercepts every return of `MapModManager.TryStageModUpdate`; incoming branch labels are retargeted through the callback. The audited assembly SHA-256 and exact method signature gate installation. Dry transformation/compilation occurs before publication. There is no lifecycle unpatch, dispatcher or coroutine. The Extender's Steam initialization publisher supplies the long-lived invocation path; the first staging callback logs its execution after startup cleanup.

The staging callback only cleans obsolete APIShared package files. Foreign map packages remain exclusively Extender-owned. `WorkshopInventory` and its schema are public helpers; shipping an inventory does not opt a foreign map into APIShared deployment.

The owner-bound `ModApiClient.Workshop` capability handles a different schema, `apishared-workshop.json`, and exclusively declared loose `Payload` files. `WorkshopPackageCodec`, `WorkshopPaths`, `WorkshopInstallation`, `WorkshopTransaction` and `WorkshopPublishing` contain testable file policy without game dependencies. `WorkshopRuntime` captures Steam state on Unity and delegates file preparation to workers. `WorkshopSteamUpload` roots its own call-results and never uses Vanilla's stateful upload singleton. The view is presentation state rather than persisted lobby settings. Callbacks are retained by static fields and Extender publishers after plugin-component destruction.

Receipts under `_APIShared/Workshop/Receipts` determine update eligibility. Discovery does not hash or repair installed files. Planning has no installation side effects; explicit application rechecks source, receipt, targets, Steam subscriptions, prerequisites, external ownership and Extender staging. Separate scripts apply the immutable operation protocol only after game exit, back up all existing targets before changing them, journal writes, and commit the receipt last. Interrupted application requires explicit recovery; backups and journals remain available. No recursive plugin-directory removal occurs. The Proton backend uses the host bash through Wine's Z: mapping, requires flock/coreutils and conservatively waits for all matching game executables.

Mixed packages reserve a distinct bootstrap GUID/folder and exactly one retained map filename. The actual map archive is audited against that folder as well as the declaration; bootstrap and loose files cannot share targets. APIShared itself is distributed as an Extender-managed map. Installation is read-only under `SCDEModManagerLaunchId`; publishing is still available. No competing native or Fixes hooks are introduced.

Dialog recovery uses an internal `APISHARED-WORKSHOP-RECOVERY-1` operation file: restart/stop followed by interrupted transaction IDs and SHA-256 pins of their operation lists and journals. Existing installation protocols 1 and 2 are retained. Confirmed complete repairs carry `supersedes.tsv` plus its hash; launch pins that hash separately. Only a committed replacement with unchanged reference bytes resolves the original transactions. Original journals and backups remain intact while unresolved, including when replacement itself fails; validated resolved history follows the retention policy above. Recovery does not change public Workshop signatures or automatically repair a current receipt.

The installed managed contracts are checked by `tools/Validation/Verify-WorkshopContracts.ps1`, with file-policy regressions in Core.Tests and an installed-method IL transformation regression in APISharedTests. Renew the complete installer audit and binary allowlist when the Extender changes. These contracts concern managed Workshop staging, not native gameplay hooks.
