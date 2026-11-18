# Workshop packages and publishing

This guide is for package authors, consumers of the Workshop capability and
contributors maintaining its deployment integration. APIShared self-cleanup,
Extender-managed maps and explicitly declared loose packages are separate paths.

- [Public capability](#workshop-publishing-and-loose-packages): ownership, threading and plans.
- [File inventories](#workshop-package-cleanup): APIShared self-cleanup and schema.
- [Publishing](#loose-workshop-packages): the external uploader and platform limits.
- [Package format](#package-format-and-bootstrap): inventory and bootstrap preparation.
- [Keep list](#keep-list-apishared-keeptxt): player files that survive updates.
- [Installation (preloader swap)](#installation-preloader-swap): staging, the swap at the next start, recovery and limits.
- [Linux paths and first-install repair](#linux-paths-and-first-install-repair): Wine drive mappings and the independent manual patcher.
- [Internal implementation](#internal-implementation): staging hooks, the installation core, the independent patcher and persistent publishers.

## Workshop inventory

`APIShared.Workshop.WorkshopInventory.FileName` identifies the schema-1 file inventory. The Extender staging callback cleans only obsolete APIShared package files; foreign `.map` packages are Extender-owned. See [Workshop package cleanup](#workshop-package-cleanup).

## Workshop publishing and loose packages

Players manage subscribed packages in **Mod Options → APIShared → Workshop**. Startup offers use one explicit confirmation; confirmed changes are prepared during the session and installed by APIShared's preloader at the next game start. Authors use the shipped UploadMod.bat or UploadMod-linux.sh under BepInEx/tools/APISharedWorkshopPublisher; both use the same APIShared publisher. Linux execution through the existing Proton prefix awaits practical publishing verification.

`ApiShared.ForMod(guid).Workshop` is independent of native capability readiness. Its [public client](../src/Workshop/WorkshopClient.cs) validates packages, captures a subscription catalog, queries owned SHCDE items, prepares upload snapshots and owner-bound installation plans, and explicitly publishes or applies a plan. Calls that access Steam require initialized Steamworks on Unity; file preparation runs on workers. Failures throw with the relevant step, and submitted operations expose status, diagnostic, operation ID and Unity-thread Changed notifications. Closing a view does not cancel a submitted request.

The [schema and workflow](#loose-workshop-packages) use a top-level `apishared-workshop.json` plus `Payload/BepInEx/plugins/...`. `info.json` contracts are authoritative for individual mods. Pure maps without this manifest stay with Extender. Optional bootstrap maps have an independently validated, exclusively reserved folder. `PrepareInstallAsync` never changes installed files; `ApplyAsync` is an explicit consent boundary. It stages a verified copy of the package and leaves a request; nothing in `BepInEx/plugins` changes in the current session and the game is not closed. `AwaitingRestart` means "staged, applied at the next game start", not a committed installation; the receipt written by the preloader proves the installation. External managed launches reject Apply while retaining read-only discovery and upload.

**Compatibility of `ApplyAsync`.** Signatures, `WorkshopOperationState` values and plan types are unchanged. Behavior changed with the [preloader swap](#installation-preloader-swap): earlier releases handed the plan to an external updater and closed the game, and `AwaitingRestart` meant that this handoff was confirmed. Now `ApplyAsync` only stages; `AwaitingRestart` means the change is applied at the next start, and the caller no longer has to be in the main menu. `ApplyAsync` throws before staging when APIShared's preloader is missing or disabled. An update whose package drops a whole plugin root removes that root too, and `RemovedFiles` lists its files. `WorkshopInstallPlan.RemovedFiles` now lists every file in the affected plugin roots that the new package neither delivers nor keeps through its [keep list](#keep-list-apishared-keeptxt); for a removal it lists the complete roots. Consumers that relied on the game closing, or that show `RemovedFiles` as "obsolete files only", must adjust their texts.

Release versions are numeric, three or four parts. Only newer receipted releases offer Update; explicit Reinstall/Downgrade are separate actions. Receipted installations whose delivered files are missing or modified are offered a clean reinstallation at startup; nothing is changed without consent. An update replaces each plugin root as a whole: afterwards the root contains exactly the new package files plus the player files named by the package's [keep list](#keep-list-apishared-keeptxt). Windows and Proton live tests and Steam account tests are recommended before distributing loose packages; passing file-policy tests alone does not certify platform integration. Manual release and upload drivers do not require a platform-approval file.

An embedded APIShared copy requires explicit package replacement; subscribing to the central map item alone leaves that copy installed. The Extender installs the central manager first. Because the confirmed installation replaces the package roots as a whole, an embedded copy inside a package root disappears when the new release no longer delivers it (unless it is keep-listed). Bootstrap readiness checks both the selected plugin path and the actual loaded assembly path, and preparation requires the central version to exceed every verified embedded release.

## Workshop package cleanup

APIShared automatically installs a permanent staging callback when the loaded Script Extender satisfies the managed method contract, exclusively for cleaning obsolete files from its own installed packages. There is no exact SE version or whole-assembly hash allow-list; the declared minimum SE version remains required. Foreign `.map` items remain entirely Extender-managed. A missing or incompatible signature, managed body or IL transformation disables only this self-cleanup with a `Workshop cleanup unavailable:` warning describing the failed check; other services remain available. Neither Extender sources nor its updater are replaced. The inventory helpers and schema below describe delivered files and verified release hashes. This callback does not clean foreign maps.

Generate `apishared-workshop-files.json` inside `BepInEx/plugins/<GUID>` from the final package with `tools/WorkshopPackage.ps1` and `New-ApiWorkshopInventory -Directory <package>`. The JSON contract is schema 1, `Guid`, `Version`, `Files` and optional `LegacyFiles`; both file collections contain `{ "Path": "relative/path", "Sha256": "64 lowercase hexadecimal digits" }`. Identity and version must match the package's `info.json`. `Files` lists immutable delivered files, excluding the inventory itself. Never list user settings, captures, generated files or other mutable data. The helper excludes `config` and `LobbyModSettings`; authors must keep all other user data outside package staging.

`LegacyFiles` supplies path/hash alternatives from verified historical releases for users who never had an inventory. Keep applicable historical alternatives in future releases until those installations are no longer supported. Cleanup is driven by inventories and hashes, not package-specific paths. Only files absent from the new `Files` collection and matching a recorded old hash are scheduled. Current, unknown, modified, missing and concurrently staged files remain untouched. Paths must be relative forward-slash paths; traversal, Windows updater wildcard characters (including square brackets), device names, alternate streams, duplicate paths and reparse points are rejected. Other subscribed package payloads are inspected even if they have no inventory; competing GUIDs disable cleanup and competing file claims preserve that file.

Deletion is deferred through `_SE/.staging/delete_list.txt`; installed files are never deleted by APIShared. The existing Windows/Proton updater applies deletion after exit and before copying staged replacements. Locked-file/deployment failures are logged and reevaluated at the next startup. The Extender also owns removal of unsubscribed map items.

## Loose Workshop packages

Open **Mod Options → APIShared → Workshop** to install the version supplied by Steam. The tab lists subscribed APIShared packages even if their payload is absent from the game. Review the previewed version and planned changes before choosing **Install at next start**. This prepares the installation immediately on a worker: the package is copied into APIShared's staging area and verified there, without touching `BepInEx/plugins`. The next game start installs it before any mod is loaded (Install, Update, Reinstall or Downgrade as chosen in the tab), whether or not the package is installed or intact and regardless of **Offer updates at startup**. **Discard prepared installation** withdraws a prepared change that has not been installed yet; this is not possible while an interrupted installation of that item is still open, which the next start completes first.

Earlier releases first recorded only a request, offered it in the next startup dialog, then closed the game, installed through an external updater with a progress window and restarted the game. That updater and its restart handoff were removed; see [Installation (preloader swap)](#installation-preloader-swap).

A statically rooted Steam-initialization subscriber prepares one startup decision per process. It offers Update for newer releases of intact receipt-owned installations, a clean reinstallation when receipted files are missing or modified, and Remove for receipted items that are no longer subscribed. The local **Offer updates at startup** switch controls the update and reinstallation offers per Workshop item and defaults to on; switch it off for an item whose files are changed on purpose, such as a standalone development build. Items with a prepared, not yet installed change are not offered again. Extender work and bootstrap installation take priority. Invalid dependencies or ownership conflicts defer changes safely.

The startup dialog lists every change and asks once (**Yes**: prepare these changes now, they are installed at the next start; **No**: keep the current installation). After **Yes** staging continues in the background; nothing is applied in the current session and the game is never closed or restarted. Declining preserves files and rediscovers eligible changes on the next start. At the next start, the outcome of the preloader installation is reported once: successes are logged, failures are logged as errors and shown in a dialog; the previous version then remains installed and the installation is retried at the following start.

Startup diagnostics use timestamped Info, Warning and Error entries. Warnings explain policy blockers (including every conflicting plugin/asset GUID with its existing path and allowed payload roots, package versions and Workshop ID). Errors identify technical failures with their step and exception details, including unreadable manifests or receipts, staging failures and failed preloader installations. Each startup attempt ends with a result: current, blocked, declined, staging or failed; blocked items and technical failures cannot be reported as current. Staging confirms the decision only, not a committed installation. Repeated Steam initialization does not repeat the decision or its warnings. Pure Script Extender maps remain outside this package catalog.

Authors use `BepInEx/tools/APISharedWorkshopPublisher/UploadMod.bat`, shipped by APIShared outside the plugin scan. No running game or separate Steam password is needed; Steam must be running, signed in and online. The wizard accepts a prepared manifest or generates one from Payload, collects prerequisites/dependencies, offers owned SHCDE items and publishing metadata, validates a private snapshot and requires typing `UPLOAD` before submission. `/validate` skips Steam initialization and publishing, `/test` uses private visibility and account-scoped separate IDs, `/nopause` only suppresses the BAT closing pause; `/cancel` cancels a prompt. Saved settings and immediately persisted item IDs live in LocalApplicationData/APIShared/WorkshopPublisher, scoped by app/account/mode/package. Never add this state to uploaded content.

Linux users can run `bash UploadMod-linux.sh`. It uses the existing SHCDE Proton prefix, not a separate uploader. With custom Steam/Proton locations set `STEAM_COMPAT_CLIENT_INSTALL_PATH`, `STEAM_COMPAT_DATA_PATH` (directory containing pfx), and `APISHARED_PROTON` (the selected Proton script). Python 3 is required by Proton; host paths such as `/home/user/My Mod` are translated using the active Wine mappings rather than an assumed Z: drive. This launcher has automated syntax/argument tests; successful real Proton publishing is still required before claiming Linux support.

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

Generate the complete inventory using [Prepare-WorkshopPackage.ps1](../tools/Prepare-WorkshopPackage.ps1). Keep mutable settings and runtime output outside Payload; player files that must live inside a plugin root need a [keep list](#keep-list-apishared-keeptxt). Links, traversal, duplicate paths/GUIDs, protected infrastructure and overlapping ownership are rejected. All delivered info.json identities, including nested mods, are checked. A bootstrap map must contain only its own reserved plugin folder and root info.json. Its root/plugin metadata must agree with the declared GUID/version and BepInEx manifest. Package replacement preserves the loose host identity and GUIDs.

For authors without an existing manifest, `WorkshopPackageDraft` supplies identity, version, prerequisites and optional dependencies/bootstrap/history. `CreateManifestAsync` generates inventory text without changing source files. Passing the draft as `WorkshopUploadOptions.Draft` generates the manifest directly in the prepared immutable snapshot. The external wizard supports a new plain package using ID/version/minimum fields and dependency prompts; prepared manifests retain their dependency/bootstrap declarations. No registered mod or source-code compilation is required for plain publishing.

[New-WorkshopBootstrap.ps1](../tools/New-WorkshopBootstrap.ps1) generates independent bootstrap sources and a build.bat. Supply a unique GUID/folder, retained map filename, version newer than the legacy Extender receipt, APIShared minimum and its separate Workshop ID. Compile through that build.bat with SHCDE_GAME_DIR set, then stage only `info.json` at the root and the bootstrap DLL/matching `info.json` under its plugin folder. Run the Extender's existing map packager, then dot-source `tools/WorkshopPackage.ps1` and call `Set-ApiBootstrapMapPayload -MapPath <map> -Stage <stage>`: the packager's map template otherwise retains preview/config entries. This helper retains the native map prefix and creates/verifies exactly the staged ZIP inventory. There is no APIShared assembly reference. Missing/old APIShared shows a bilingual notice with its Workshop link; no files are installed, repaired or deleted by the bootstrap.

### Keep list (apishared-keep.txt)

An installation replaces every plugin root of a package as a whole. Files that a player creates or edits inside a root (settings, presets, captures) therefore disappear at the next update unless the package names them in a keep list. Storing such data outside the plugin folder, for example below the player's Documents folder, remains the recommended design; the keep list exists for mods that already keep data next to their DLL.

Place an optional `apishared-keep.txt` in any folder of a package root (`BepInEx/plugins/<folder>/...`) and include it in Payload and the manifest like any other delivered file. Each line names one path relative to the folder that contains the list:

```text
# comment
settings.json          a file
config/                a folder with everything below it (trailing slash)
presets.json 2         reset revision 2
config/ persist        also kept when the package is uninstalled
```

- A reset revision is an optional positive number after the path. When a release raises it above the revision applied at the player's installation, the player's copy is replaced once by the shipped file, or removed if the release does not ship one. The applied revisions are remembered in a generated `.apishared-keep.state` beside the root's files; a missing state counts as revision 0.
- The word `persist` keeps the entry also when the package is uninstalled (unsubscribed). The files are parked in `_APIShared/I/k/<PackageId>/<folder>` and restored when the package is installed again.
- `#` starts a comment. Absolute paths, drive letters, `..`, wildcards, duplicate entries and paths containing spaces are invalid; a package with an invalid keep list is rejected while it is staged, never during the swap.
- After an update a root contains exactly the new package files plus the listed player files; every other file, including files the author removed from the package, disappears. A root that the new version no longer ships at all is removed in the same swap (its `persist` entries are parked). A listed file that the player deleted is taken from the new package. If a listed path changes kind between versions (file vs folder), the shipped version wins.
- Shipped files covered by the keep list are excluded from the installation receipt, so player edits do not mark the installation as modified and do not trigger a reinstallation offer.

### Installation (preloader swap)

Automatic startup discovery waits inside the Script Extender Steam-initialization event until the player decides. File validation and staging run in the background. Only APIShared continuations return to the blocked game thread. The Extender handles its maps first and writes its separate bootstrap record. Existing plugin files without a payload receipt can be explicitly replaced from Mod Options; afterwards only keep-listed files of the previous folder remain. Asset GUIDs with author-defined, nonnumeric versions satisfy dependencies without version bounds. Bounded dependencies require a comparable numeric version. Loaded plugin versions take precedence over asset metadata.

The public Workshop capability remains available for consumer mods with their own explicitly confirmed workflows. Upload snapshots include only declared content and optional bootstrap; changed content requires a greater package version. Steam dependencies are soft links and do not replace local prerequisite checks.

```csharp
// Steam entry points on Unity, after Steamworks initialization. The built-in publisher is an external console wizard.
var workshop = ApiShared.ForMod("YourModGuid").Workshop;
var catalog = await workshop.GetCatalogAsync();
var plan = await workshop.PrepareInstallAsync(itemId, WorkshopAction.Update);
// Present plan.Files, plan.RemovedFiles and plan.Diagnostics, and wait for explicit user confirmation.
var operation = await workshop.ApplyAsync(plan);
// AwaitingRestart: staged; APIShared's preloader applies it at the next game start and then writes the receipt.
```

Startup update offers require the recorded immutable files to still match the receipt; missing or modified files lead to a clean reinstallation offer instead. Invalid or missing receipts require an explicitly chosen installation. Steam failures, downloads and missing content folders do not indicate an unsubscribe.

An installation runs in two phases:

1. **Staging (current session).** On a worker, APIShared copies every package root (`BepInEx/plugins/<folder>`) from the Steam Workshop folder into `<game>/_APIShared/I/s/<WorkshopId>.tmp`, verifies the copies (not the Steam source) against the manifest's size and SHA-256 values, validates the keep lists and writes the plan, the receipt to install and a completion marker. One rename then publishes the container as `_APIShared/I/s/<WorkshopId>`, and a request `_APIShared/I/r/<WorkshopId>.request` is written. If Steam replaces the item meanwhile, the staged change is withdrawn. Staging never touches `BepInEx/plugins`.
2. **Swap (next game start).** APIShared's preloader (`APIShared.DependencyBootstrap`, in its `Finish` entry, before the BepInEx chainloader opens anything in `BepInEx/plugins`) first recovers interrupted swaps, then applies each request: it writes a journal (`_APIShared/I/j/<WorkshopId>.journal`), carries keep-listed player files over into the staged roots, renames every installed root to a backup (`_APIShared/I/b`), renames every staged root into `BepInEx/plugins`, moves roots of the previous version that the new one no longer ships out of `BepInEx/plugins` as well, writes the receipt `_APIShared/Workshop/Receipts/<WorkshopId>.json` and deletes backups, container, journal and request. A removal (unsubscribed item) parks `persist` entries, removes the complete roots and deletes the receipt.

Guarantees and behavior:

- Plugin folders are only renamed, never changed file by file. BepInEx always sees the complete old or the complete new version of every root.
- Recovery decides from the file system and a transaction marker file (`.apishared-install`) inside each staged root. It rolls forward when the staged data is complete and otherwise restores the old roots. Recovery is idempotent and runs again if the preloader itself is interrupted.
- A locked file in an installed root (for example held by a virus scanner) makes the swap undo itself immediately; the old version stays installed and the request is retried at the next start.
- Outcomes are logged on the BepInEx log source `APIShared.WorkshopInstall` and written to `_APIShared/I/result.txt`. The APIShared plugin reports that file once at startup; failures are shown in a dialog.
- The preloader pass is silent and does nothing when no request or journal exists.
- A batch confirmed at startup is staged all or nothing: every container is prepared first and the requests are written last, so quitting the game while it still copies leaves nothing to apply; if one change fails to stage, the changes of that batch already staged are withdrawn and offered again at the next start. At the next start each request is swapped on its own, so an item whose swap fails there does not hold back the others.
- A swap that fails transiently (for example a briefly locked folder) and is completed by the immediate recovery counts as installed.
- A linked installed root (junction or symbolic link) is never moved; such a request fails with an error and stays pending.
- If a rollback cannot return an old root because a folder was recreated at its place, the old root is kept as `_APIShared/I/b/<WorkshopId>-<index>-kept-<transaction>` and named in the log, so player files are never lost.

Limits:

- The swap runs only when the dependency bootstrap is installed and active (no `APIShared.DependencyBootstrap.disabled` switch) and the APIShared plugin folder exists. The preloader marks the process when its pass ran; without that mark APIShared offers and stages nothing (startup logs a warning, Mod Options and `ApplyAsync` report why), so no change waits for an installer that never runs. Requests staged earlier wait until a start where it runs; see [Early dependency-index correction](DEPENDENCY_BOOTSTRAP.md#initialization-and-diagnostics).
- Staging needs free disk space for one complete copy of the affected packages below `_APIShared/I`, on the same volume as `BepInEx/plugins`.
- Proton/Wine live acceptance of the folder renames has not been performed yet; the Linux CI job covers the installation core only on a native Linux file system.
- Staging is refused under `SCDEModManagerLaunchId`; a change staged during an earlier ordinary start is still applied by the preloader at the next start.

If the game cannot be started or an installation keeps failing, players can reinstall a package by hand: close the game, copy the `Payload/BepInEx` folder of the subscribed item (`steamapps/workshop/content/3024040/<Workshop ID>/Payload/BepInEx`) over the game's `BepInEx` folder and overwrite existing files. Files of earlier releases may remain until the next installation through APIShared; if the copied files differ from the recorded receipt, the next start offers a clean reinstallation.

Extender staging/download restarts take precedence. Under `SCDEModManagerLaunchId`, startup offers and staging are unavailable (see the limits above); uploads are available. The external manager's current importer is separate. Loose APIShared packages require an importer explicitly supported by that manager. Publish APIShared's independently managed map first; test package replacement at the existing modpack ID using the same map filename and a separate bootstrap GUID before replacing the live release. Do not publish loose packages until real Windows/Proton and Steam tests pass.

Platform-test records are optional and are not read by the maintainer release/upload drivers. Missing, outdated or malformed records do not block manual publication. SerpsMods publishes a separate Workshop-content ZIP alongside its ordinary plugin ZIP; reusing a release restores the complete verified content ZIP, including manifest and Payload, rather than just its bootstrap map.

### Former updater leftovers

The former external updater kept transaction folders and history (`_APIShared/T`, `_APIShared/Workshop/Transactions`, `_APIShared/Workshop/Unrecoverable`, `apply.lock`, `cleanup.pending.tsv`). Startup removes these leftovers automatically, unless a former updater still holds `apply.lock`; then they are removed at a later start. Before deleting them, startup reads which items a transaction was installing when it was interrupted (journal `applying` without `committed`/`restored`, items from the receipt rows of `operations.tsv`) and records them in `_APIShared/I/repair.txt`. Such an item is offered as a clean installation at every start, even without a receipt (an interrupted first installation) and with update offers disabled, until its files match the Workshop package again. There is no history retention or resume/rollback dialog anymore.

## Linux paths and first-install repair

### Path configuration

APIShared's own installation no longer starts host processes: staging and the preloader swap use ordinary file operations inside the game directory. The Linux/Wine path helpers below are used by the [independent first-install patcher](#manual-first-install-repair) and by the publisher.

The helpers translate game, script and staging paths through Wine's own path API.
Mapped S:, Z: and user-defined drives use the same resolver;
interpreter discovery uses the reverse translation of `/bin/bash` or `/usr/bin/bash`.
The publisher uses the same native translation for author-supplied host paths.
Wine before 10.13 (Proton 10 and older) reports drives other than a root-mapped Z:
through the prefix's `dosdevices/<x>:` link; the helpers resolve only that drive link
once per drive with host `realpath`, so the link checks below the drive root still apply.
Only a confirmed Linux Wine/Proton host activates this path. The patcher's helper
requires host Bash, flock and GNU coreutils.

Normally no configuration is needed. The independent patcher reads the optional
`BepInEx/config/APIShared.ProtonPaths.cfg` once per process and never rewrites
existing contents:

    [ProtonPaths]
    HostGameDirectory =
    WineBashPath =

`HostGameDirectory` is an absolute Linux path to this exact game installation, used
only when automatic translation needs an explicit override. An identity probe
checks both filesystem views before the patcher lets the Extender close the game. `WineBashPath`
is the absolute Windows/Wine path to the host Bash when automatic discovery fails.
Invalid explicit values are reported, not silently ignored. Restart after editing.
Existing containment, hash, link and external-management boundaries still apply.

### Manual first-install repair

If Script Extender cannot install APIShared in the first place, the APIShared runtime
cannot repair that earlier step. The separate manual asset
`APIShared-ProtonWorkshopFix-v<version>.zip` contains one independent preloader:
`BepInEx/patchers/APIShared.ProtonWorkshopFix.dll`. Close the game, copy that DLL
to the matching game path, retain the APIShared subscription, then start and accept
the normal Script Extender installation prompt. Keep exactly one patcher copy.
The asset contains no APIShared runtime and is not automatically installed by the
normal APIShared ZIP/map. The [dependency-index bootstrap](DEPENDENCY_BOOTSTRAP.md)
is a separate feature.

An already exhausted deployment counter receives one additional confirmed attempt
after successful preflight. Declining does not consume it; a failed retry does not
repeat automatically. Failed copies retain staging and diagnostics. Inspect
`BepInEx/LogOutput.log` and the attempt's `journal.log` under
`BepInEx/cache/APIShared.ProtonWorkshopFix/` when the handoff fails.
The patcher is inert under native Windows and externally managed launches.
Remove or replace it only with the game closed.


### Platform acceptance limits

Automated path, IL, counter and package checks exercise separate boundaries.
The native Bash fixture of the patcher's helper and the Core.Tests installation tests
are required by the Linux CI job; they run on a native Linux file system and do not run
Wine, Unity Mono or Steam. The [host-run protocol](#linux-host-processes-and-game-exit-detection)
under Unity Mono, game-exit detection beside Steam's wrapper processes, real argument
conversion, hook activation after startup cleanup,
first installation without APIShared on S:, Z: and a custom mapping, configuration
overrides and a failed one-time retry still require Proton acceptance. Live Proton/Wine
acceptance of APIShared's preloader swap (folder renames inside the Wine prefix view)
has not been performed yet. Native Windows acceptance must confirm the
absence of new hooks, host processes and retry state without the patcher. Automated checks
alone do not establish these live results or successful Proton publishing.

## Internal implementation

### Interrupted-update regression

The historical late-lobby report is consistent with APIShared 0.7.1, which dispatched an `async void` startup workflow. Release 0.7.2 introduced the blocking startup gate. The player's exact loaded version and preceding confirmation are not established. Regressions retain that gate across asynchronous work and reject expired, worker-thread and reused startup consent.

An anonymized user transaction showed an update from pack 1.0.23 to 1.0.26 interrupted after 779 of 882 operations, before updating the pack host and writing its first APIShared receipt. All 874 planned write hashes matched the published 1.0.26 package. The journal ended with a completed locale file and contained no failure, commit or rollback marker. The later 1.0.27 update was blocked by this unresolved transaction. The journal establishes the interruption and partial installation, but does not establish why the updater stopped.

These findings motivated replacing the file-by-file updater by the [preloader swap](#installation-preloader-swap): an interrupted installation can no longer leave a partially updated plugin folder. The former updater's fixtures were removed with it. Current coverage:

- `tests/Core.Tests/WorkshopSafeInstallTests.cs` drives the installation core with deterministic simulated process death at every swap and recovery step, locked roots, keep-list parsing, carry-over, reset revisions, `persist` parking, damaged containers and the preloader result file. `WorkshopPackageTests.cs` covers staging from plans and `RemovedFiles`.
- `tools/Validation/Test-WorkshopInstall.ps1` builds the harness `tests/WorkshopInstall.Harness` and kills real processes at random moments during staging, the swap and the preloader's own recovery, then checks that no moment exposed a partial plugin folder and that the complete new version, its receipt and no leftovers remain. `tools/Build.ps1` runs it as step `workshop-install-kills`.
- The Linux CI job runs the Core.Tests Workshop tests on the native Linux file system. This does not cover Wine/Proton path views or Steam.

### Workshop staging integration

`Workshop/WorkshopCleanupRuntime` is rooted by a static `ILHook` installed in APIShared `Awake`. It intercepts every return of `MapModManager.TryStageModUpdate`; incoming branch labels are retargeted through the callback. The exact nonpublic instance signature and a managed body with return sites gate installation. Dry transformation/compilation of the loaded method occurs before publication; incompatible code stays unhooked regardless of its SE version. Complete SE assembly hashes do not authorize or refuse installation. There is no lifecycle unpatch, dispatcher or coroutine. The Extender's Steam initialization publisher supplies the long-lived invocation path; the first staging callback logs its execution after startup cleanup.

The staging callback only cleans obsolete APIShared package files. Foreign map packages remain exclusively Extender-owned. `WorkshopInventory` and its schema are public helpers; shipping an inventory does not opt a foreign map into APIShared deployment.

The owner-bound `ModApiClient.Workshop` capability handles a different schema, `apishared-workshop.json`, and exclusively declared loose `Payload` files. `WorkshopPackageCodec`, `WorkshopPaths`, `WorkshopInstallation`, `WorkshopStaging`, the installation core in `src/Workshop/Install` and `WorkshopPublishing` contain testable file policy without game dependencies. `WorkshopRuntime` captures Steam state on Unity and delegates file preparation and staging to workers. `WorkshopSteamUpload` roots its own call-results and never uses Vanilla's stateful upload singleton. The view is presentation state rather than persisted lobby settings. Callbacks are retained by static fields and Extender publishers after plugin-component destruction.

Receipts under `_APIShared/Workshop/Receipts` record the last committed delivery; the preloader writes them as the last step of a swap. Catalog discovery reads metadata. Startup offers and the Mod Options status additionally compare immutable files against the receipt on a worker; keep-listed shipped files are not part of the receipt. Planning has no installation side effects. Right before staging, APIShared rechecks that its preloader is active, the startup conditions (external management, pending Steam downloads, Script Extender staging; an active mission is allowed because nothing changes in the running session), each plan's Steam subscription and folder ownership; `ApplyAsync` also rechecks the prerequisites. Staging itself rechecks the receipt, manifest and source files and verifies the staged copies before publishing the container.

Mixed packages reserve a distinct bootstrap GUID/folder and exactly one retained map filename. The actual map archive is audited against that folder as well as the declaration; bootstrap and loose files cannot share targets. APIShared itself is distributed as an Extender-managed map. Installation is read-only under `SCDEModManagerLaunchId`; publishing is still available. No competing native or Fixes hooks are introduced.

The installed managed contracts are checked by `tools/Validation/Verify-WorkshopContracts.ps1`, with file-policy regressions in Core.Tests and an installed-method IL transformation regression in APISharedTests. Review the staging semantics when the Extender changes and run the installed-contract and transformation checks; see [Workshop staging cleanup](../UpdateToNewDLL.md#workshop-staging-cleanup). These contracts concern managed Workshop staging, not native gameplay hooks.

### Installation core

[WorkshopSafeInstall.cs](../src/Workshop/Install/WorkshopSafeInstall.cs) uses only System types. It is compiled into APIShared.dll (staging, requests, withdrawal, result reporting) and source-linked into the preloader `APIShared.DependencyBootstrap` (swap and recovery), and the offline tests compile it unchanged. [WorkshopStaging](../src/Workshop/WorkshopStaging.cs) turns a confirmed plan into roots, keep-list exclusions and the receipt; [WorkshopRuntime.Apply](../src/Workshop/WorkshopRuntime.Apply.cs) serializes staging on a worker; [WorkshopStartup](../src/Workshop/WorkshopStartup.cs) reports the preloader result, removes former updater leftovers and runs the startup decision. The preloader entry is [WorkshopInstallPass](../bootstrap/src/Workshop/WorkshopInstallPass.cs), called from `DependencyBootstrapPatcher.Finish` (see [Early dependency-index correction](DEPENDENCY_BOOTSTRAP.md#initialization-and-diagnostics)).

All state lives below `<game>/_APIShared/I` on the volume of `BepInEx/plugins`, so every step is a same-volume rename: `s` staged containers, `r` requests, `j` journals, `b` backups, `k` parked `persist` entries, plus `result.txt`. A container holds one numbered folder per target root, `plan.txt` (protocol `APISHARED-INSTALL-1`: Workshop ID, action, package ID, 32-hex transaction ID, game-relative targets, each exactly one folder directly below `BepInEx/plugins`; for an update, `-`-prefixed roots of the previous version to remove), `receipt.json` and the completion marker `.apishared-complete`. Each staged root contains `.apishared-install` with the transaction ID. A leftover `.tmp` container or a container without completion marker is never trusted.

The journal (`APISHARED-SWAP-1`) only records that a swap of an item started; recovery does not rely on journal progress entries. It compares the markers of the target roots with the plan's transaction ID: all targets marked means completed; no target marked and no backup means not started (journal dropped, request kept); otherwise it rolls forward with the remaining staged roots and, if that fails, moves the new roots back into the container and restores the backups. Keep-list carry-over first records the files it will overwrite in an undo folder inside the container, so a repeated attempt starts from the pristine staged root and uses the current player state. If cleanup after the receipt fails, the kept journal finishes it at the next start without reporting the installation as failed. Withdrawing a request refuses while a journal for the item exists. `Finish` deletes `plan.txt` before the rest of the container, so recovery never repeats the receipt copy from a half-deleted container, whatever order the file system deletes the remaining files in.

### Linux host processes and game-exit detection

This applies to the [independent first-install patcher](#independent-linux-first-install-patcher)
and its embedded helper; APIShared's own installation starts no host process.

Under Wine a Linux program started by the game has no Windows process. ntdll's
`fork_and_exec` (Proton 10 and 11) returns process handle 0, detaches the program with
`setsid` and connects its stdin/stdout to `/dev/null`. Unity's Mono `Process` then
reports it as exited at once with exit code 0 and empty output, and `Kill` cannot reach
it. `HostRun` in `src/Workshop/Proton/ProtonProcess.cs` therefore never consults the
`Process` object: a fixed Bash wrapper runs the requested arguments in the same host
Bash and atomically publishes stdout, stderr and the exit code as files in an owned run
directory under the Wine temp folder. Short commands wait for that record within their
budget; an unfinished run is reported as a timeout and keeps running. Helper handoffs
replace `HasExited` with the exit record.

Wine's double fork turns every helper into an orphan that Steam's subreapers (`reaper`
and pressure-vessel's `pv-adverb`) adopt. They wait for all descendants and name the game
only as a later argument. The helper therefore counts a process as the game only
when argv[0] or argv[1] names the game executable, which is Wine's program position after
its loader. It refuses before readiness when that game is not visible, so a detector
mismatch keeps the game open.

### Independent Linux first-install patcher

The [manual patcher](#manual-first-install-repair) source-links the internal
`src/Workshop/Proton` path and process helpers; it has no APIShared runtime dependency.
Its preloader observes `Chainloader ready`, then checks already loaded and newly
loaded Extender assemblies before plugin Awake. It neither initializes MapModManager
nor installs a runtime AssemblyResolve handler. APIShared itself adds no Extender
hooks for this correction.

After Linux confirmation, the patcher validates the Extender method and control-flow
contracts and prepares both transformed delegates before publishing its two hooks.
One redirects the Linux launcher, reading the private ByRef context through its
getters; the other preserves the original attempt-counter calls and adds the
one-time retry gate. Activation follows successful installation of both hooks.
Published hooks remain rooted for the process lifetime, including partial failures;
an inactive gate preserves the original method bodies. At an active launcher failure,
returning false keeps the game open without falling back to the original Linux launch.

A confirmed retry exclusively claims the generation-1 record
`BepInEx/config/APIShared.ProtonWorkshopFix/retry-v1.state` before permitting launch.
Existing malformed or incomplete records are treated as consumed. The original raw
counter is incremented with saturation; after success the Extender still owns its
ordinary counter reset.

The embedded helper uses the hash-bound `APISHARED-SE-PROTON-1` request and its own
cache directory and lock. All delete-list targets are translated and checked inside
the game directory. Readiness requires a live helper and visible game process before
the Extender may close the game. The helper then waits for game exit, rechecks sources,
applies payload files before metadata, and retains staging on failure or unknown files.
It does not overwrite the Extender's script or touch APIShared's installation state.
See [contributor build and check instructions](../CONTRIBUTING.md#building-and-checking-the-optional-proton-patcher)
and the [platform acceptance limits](#platform-acceptance-limits).

## Dated release acceptance records

These sections record the acceptance of earlier Workshop-only hotfix releases. They are
historical evidence, not current requirements.

They describe the former external updater (transactions, recovery dialog, history retention and restart handoff), which has been replaced by the [preloader swap](#installation-preloader-swap).

### Workshop-only 0.9.1 hotfix candidate

The hotfix branch `codex/workshop-hotfix-0.9.1` starts at the published `APIShared/v0.9.0` source (`d2102cad`), not the current main/develop trees. It combines Workshop recovery/retention, progress diagnostics, M3 startup coordination and PR #29 (`90d1af7`: M1/M2/Bash). Other development work, including savegame changes, native-site registration and general optimizations, is excluded. This release-branch source is the explicitly agreed exception to the usual main-based release preparation. Main, develop and the original development checkout remain intact; no release is published by the validation workflow.

The isolated hotfix passed its own acceptance on 2026-10-15, using runtime-tested source `0d28544afd618392fefb1032b7f2845af30f64de`. The elevated Windows build driver passed all 336 managed tests and installed the package. Consumer validation resolved 1,403 installed references against the candidate and confirmed its exported API against the published 0.9.0 assembly. The production fixtures passed rollback, complete repair, the 882-operation interrupted update, subsequent receipt installation, 100-update retention and the 13 Windows coordination cases. [The exact-source CI run](https://github.com/SHCDE-APIShared/APIShared/actions/runs/38647455154) passed Windows and Ubuntu, including 30 Linux managed tests and required native Bash recovery, coordination and retention fixtures with real flock/coreutils; skipped required fixtures are failures.

The real Windows/Steam M3 probe used transaction `b1d98de001a1483bb04ce042a8d05c3b`. It recorded pause, exactly one confirmed-handoff runtime quit, resume and commit; the operator confirmed that the automatic second start remained normally open at the main menu without another update or error dialog. That start reported `startup result=current; blocked=False; technicalErrors=0` for installed/current pack 1.0.27 and `Workshop maintenance: nothing to do; no process started`. Evidence is retained in `.local/live-restart-tests/b1d98de001a1483bb04ce042a8d05c3b`. This uses a synthetic file transaction with the real installed startup path; interrupted payload and recovery fault coverage comes from production fixtures. Final package, installed assets and provenance hashes were checked, and the original development checkout and its 22 saved changed/new files remained unchanged.

Native Linux fixtures establish file/process behavior without Wine or Steam; full Proton/Steam acceptance remains untested. Later documentation-only commits do not change the runtime-tested inputs. A later release of the excluded development features must use a higher version than this Workshop-only 0.9.1.

The existing release driver accepts `-PrepareOnly` to package a current validated build into local ZIP/map assets, checksums and provenance without creating a GitHub release/tag or uploading to Steam. It cannot be combined with `-Publish`, requires matching build evidence, and never silently rebuilds a stale package. Preparation still requires reviewed, committed source available in the repository. Normal release behavior is unchanged.

For an interrupted installation, install the APIShared hotfix and start the game normally. Keep the transaction and its backups. Choose repair to restore and restart, or rollback to restore and close the game; a subsequent start offers the current package. If rollback is unavailable, complete repair requires a second confirmation. Do not delete transaction files to bypass the repair dialog. No additional modpack release is required.

### Workshop-only 0.9.2 handoff hotfix

Version 0.9.2 retains the released 0.9.1 recovery, retention and startup coordination. It fixes the readiness-file sharing race and rejects an updater that exited before the handoff was confirmed. Proton restarts try `steam -applaunch 3024040` if `xdg-open` is missing or fails. A restart failure reports how to start Steam manually and does not undo a committed installation.

The release is prepared on `main` with only the released Workshop runtime and these corrections. Previously unreleased development functions remain on `develop` as 0.10.0; branch history is preserved. Public APIs and transaction formats are unchanged. Updating APIShared is sufficient; no modpack release is required. Keep interrupted transactions and backups, and use the repair dialog rather than deleting recovery data. Native Linux fixtures cover the production Bash updater and simulated Steam restart commands; they do not establish complete Proton/Steam runtime acceptance.

### Windows M3 startup-guard acceptance

Windows M3 acceptance passed on 2026-10-14 with the installed APIShared 0.9.1 development build, Script Extender 2.14.1, Fixes 1.26.4 and SerpsMods 1.0.27. Transaction `0f55645995994ab3b43cd34a1f568c68` recorded pause, resume and commit, with exactly one confirmed-handoff runtime quit at 23:37:51 Europe/Berlin. The automatic second start loaded the tested DLL, reported `startup result=current; blocked=False; technicalErrors=0`, and the operator confirmed that the main menu remained open. The probe was removed, its receipt address remained absent, the updater lock was released and installed package hashes still matched the build evidence. Evidence is retained locally in `.local/live-restart-tests/0f55645995994ab3b43cd34a1f568c68`. This exercises the real startup guard with a synthetic file transaction; recovery/payload fault coverage comes from the production fixtures. Native Linux/Proton acceptance and integration of the separately assigned M1/M2/Bash fixes remain outstanding.
