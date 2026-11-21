# Workshop packages and publishing

This guide is for package authors, consumers of the Workshop capability and
contributors maintaining its deployment integration. APIShared self-cleanup,
Extender-managed maps and explicitly declared loose packages are separate paths.

- [Upload your first mod](#upload-your-first-mod-to-steam-workshop)
- [Public capability](#workshop-publishing-and-loose-packages): ownership, threading and plans.
- [File inventories](#workshop-package-cleanup): APIShared self-cleanup and schema.
- [Loose Workshop packages](#loose-workshop-packages): installation and publisher details.
- [Package format](#package-format-and-bootstrap): inventory and bootstrap preparation.
- [Keep list](#keep-list-apishared-keeptxt): player files that survive updates.
- [Installation (preloader swap)](#installation-preloader-swap): staging, the swap at the next start, recovery and limits.
- [External mod managers](#external-mod-managers-apisharedmodmanagerjson): let another program take packages over.
- [Linux paths and first-install repair](#linux-paths-and-first-install-repair): Wine drive mappings and the independent manual patcher.
- [Internal implementation](#internal-implementation): staging hooks, the installation core, the independent patcher and persistent publishers.

## Upload your first mod to Steam Workshop

Use the APIShared Workshop Publisher to publish a finished SHCDE plugin as a
Workshop item that players can install through APIShared.

### What you need

- A built mod with its DLL, `info.json` and any files it needs in its plugin folder.
- The prebuilt publisher from the [APIShared release ZIP](https://github.com/SHCDE-APIShared/APIShared/releases/latest).
  Extract the complete `BepInEx/tools/APISharedWorkshopPublisher` folder so the
  launcher and its supporting files stay together. If you already have the
  complete package installed, look for that folder in your game directory.
- On Windows, the .NET Framework 4.8.1 runtime and an open Steam client signed in
  online with the account that will own the item. You can leave the game closed.
  You do not need SteamCMD, Visual Studio or a separate Steam login for the publisher.
  For Linux, see [the Proton launcher](#publisher-on-linux).

### Set up the upload folder

Create a folder for this release outside the game installation. Inside it,
recreate the path your mod will have when installed, starting with `Payload`:

```text
MyModUpload/
├── preview.png
└── Payload/
    └── BepInEx/
        └── plugins/
            └── MyMod/
                ├── info.json
                └── MyMod.dll
```

Copy any resources your mod needs into `MyMod/`, keeping their relative paths.
Use your real plugin folder name and a stable GUID in `info.json` that matches
your BepInEx plugin. See the [example info.json](../examples/ThirdPartyMod/info.json)
for the fields. If your DLL uses APIShared, declare the required version in your
[plugin and info.json](THIRD_PARTY_GUIDE.md#example-package-requirements).
Players install APIShared separately, so leave its DLLs and the publisher out of
`Payload`. Leave out logs and files generated during play. If your mod stores
player settings inside its plugin folder, add a
[keep list](#keep-list-apishared-keeptxt) so updates preserve them.

### Run the publisher

1. Double-click `UploadMod.bat` and enter the path to `MyModUpload` when asked for
   the package folder.
2. Enter a stable package ID. For a single mod, use its plugin GUID. Enter a
   numeric version such as `1.0.0`, the minimum APIShared and Script Extender
   versions needed, and the GUIDs of any additional required mods. Press Enter
   at the next dependency prompt when you are done. The publisher builds
   `apishared-workshop.json` and the file inventory in the upload copy. If you
   supply a prepared manifest, the publisher uses it instead.
3. Enter `0` to create a new Workshop item, or select the numeric ID of an item
   you already own to update it. Fill in the title, description, preview image,
   visibility and optional change note. The default image path is
   `preview.png` in the upload folder.
4. Review the package and its file list, then type `UPLOAD` to send it to Steam.
   If Steam asks you to accept the Workshop agreement, follow the link shown by
   the publisher.

From a command prompt in the publisher folder, use `UploadMod.bat /validate` to
check the package without contacting Steam, or `UploadMod.bat /test` to upload a
private test item with a separate Workshop ID. To publish an update, keep the
package ID and plugin GUID, raise the package version, update your mod version
where declared, and select the same Workshop item.

Players need Script Extender and a central APIShared installation. After
subscribing to your item, they can open **Mod Options → APIShared → Workshop** to
prepare installation for the next game start.

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

A statically rooted Steam-initialization subscriber prepares one startup decision per process. It offers the first installation of subscribed packages that APIShared has not installed yet (this also adopts matching files), Update for newer releases of intact receipt-owned installations, a clean reinstallation when receipted files are missing or modified, and Remove for receipted items that are no longer subscribed. The local **Offer updates at startup** switch controls the installation, update and reinstallation offers per Workshop item and defaults to on; switch it off for an item whose files are changed on purpose, such as a standalone development build. Items with a prepared, not yet installed change are not offered again. Extender work and bootstrap installation take priority. Invalid dependencies or ownership conflicts defer changes safely.

The startup dialog lists the proposed changes. **Yes** prepares the whole group and restarts the game through Steam. The preloader installs the confirmed group before mods load. **No** continues startup with the current installation. If the restart helper cannot start, the game remains open and the prepared changes wait for a manual Steam restart. If preparation itself fails, the current installation stays in place.

The restart helper runs outside the game process tree. It waits for the old game
process to exit and for the Steam client to remove the app from its running list.
It sends one Steam launch request after both checks pass. The same restart path
handles installations, updates and removals.

Startup diagnostics identify policy blockers, staging and restart outcomes, and the preloader's installation result. Installation failures are shown once on the resulting startup. A recovery conflict that prevents Unity startup is shown by the preloader and logged under `APIShared.WorkshopInstall`. Repeated Steam initialization does not repeat the decision. Pure Script Extender maps remain outside this package catalog.

The publisher saves settings and Workshop IDs outside the package in LocalApplicationData/APIShared/WorkshopPublisher, scoped by app, account, publishing mode and package. Enter `/cancel` to cancel at a prompt. The `/nopause` option suppresses the closing pause.

### Publisher on Linux

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

Schema 1 contains `SchemaVersion: 1`, stable `PackageId`, numeric `Version`, `MinimumAPISharedVersion`, optional `MinimumScriptExtenderVersion`/`MaximumScriptExtenderVersion`, nonempty `Roots` (full `BepInEx/plugins/<folder>` paths), and complete `Files` records `{Path, Size, Sha256}` relative to the game root. `Dependencies` records use `Guid`, optional inclusive `MinimumVersion`/`MaximumVersion`, and optional decimal-string `WorkshopId`. `Bootstrap` uses `MapFile`, `Guid`, `Version`, `PluginFolder`; it is optional. `LegacyReleases` contains `{Version, Files}` records of historical deliveries; they are still validated for compatibility, but the installation no longer uses them because it replaces whole plugin folders. No Workshop identity is taken from a folder name.

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

1. **Staging.** A worker copies package roots into temporary containers below `_APIShared/I/s`, verifies every staged file against the manifest, validates keep lists and records the installation plan. Once all containers are complete, a single atomic request publishes the group. Mod Options and the public client prepare one-item requests.
2. **Installation.** The preloader recovers interrupted transactions before handling new requests. It checks the plans, staged inventory and previous receipts before it moves plugin folders, receipts or parked player files. It retains the old state until the whole group commits. After an interrupted pre-commit operation, recovery restores the old state. After commit, recovery completes cleanup.

The preloader holds a lease for the running game. A second start of the same installation cannot read mods during a swap or replace folders used by the current session. If a recovered folder cannot be restored because its path is occupied or locked, the launch stops before mod discovery. The transaction, backups and error details remain available for the next start. Existing preloader journals are still recognized.

Limits:

- New installations require the matching dependency bootstrap. A disabled companion defers staged work and still recovers an interrupted installation before mod discovery. See [Early dependency-index correction](DEPENDENCY_BOOTSTRAP.md#initialization-and-diagnostics).
- Staging needs free disk space for one complete copy of the affected packages below `_APIShared/I`, on the same volume as `BepInEx/plugins`.
- Proton automatic restart uses a user systemd service outside the Steam game process tree. If that service is unavailable, the game stays open and the pending installation can be applied by restarting manually.
- External managed launches and pending Script Extender deployment defer new APIShared installations.

If the game cannot be started or an installation keeps failing, players can reinstall a package by hand: close the game, copy the `Payload/BepInEx` folder of the subscribed item (`steamapps/workshop/content/3024040/<Workshop ID>/Payload/BepInEx`) over the game's `BepInEx` folder and overwrite existing files. Files of earlier releases may remain until the next installation through APIShared; if the copied files differ from the recorded receipt, the next start offers a clean reinstallation.

Extender staging/download restarts take precedence. Under `SCDEModManagerLaunchId`, startup offers and staging are unavailable (see the limits above); uploads are available. The external manager's current importer is separate. Loose APIShared packages require an importer explicitly supported by that manager. Publish APIShared's independently managed map first; test package replacement at the existing modpack ID using the same map filename and a separate bootstrap GUID before replacing the live release. Do not publish loose packages until real Windows/Proton and Steam tests pass.

Platform-test records are optional and are not read by the maintainer release/upload drivers. Missing, outdated or malformed records do not block manual publication. SerpsMods publishes a separate Workshop-content ZIP alongside its ordinary plugin ZIP; reusing a release restores the complete verified content ZIP, including manifest and Payload, rather than just its bootstrap map.

### External mod managers (APIShared.ModManager.json)

A mod manager outside the game can take Workshop packages over, for example because the player switched a mod off or pinned a version there. It writes `BepInEx/config/APIShared.ModManager.json` (UTF-8):

```json
{ "SchemaVersion": 1, "Manager": "Example Manager", "Items": { "3788821961": "managed" } }
```

- `Items` maps Workshop IDs to `"managed"`; `"*": "managed"` takes over every package. `Manager` is the name shown to players.
- APIShared never installs, updates, repairs, removes or offers a managed package. Mod Options shows "Managed by <Manager>" without installation buttons, and `WorkshopClient.ApplyAsync` refuses it.
- A change staged before the manager took the package over is discarded by the preloader at the next start (log `WITHDRAWN <id>`). An interrupted swap is still finished or undone first, so no folder stays partial.
- Switching mods on or off and installing them stays the manager's job; APIShared does not load or hide mods. Remove the item (or the file) to hand the package back to APIShared.
- What APIShared installed can be read from the receipts `_APIShared/Workshop/Receipts/<WorkshopId>.json`.
- A file that exists but is not understood (invalid JSON, unknown `SchemaVersion`, invalid ID) takes over every package and is logged as an error; an unknown value for an item still marks that item managed. A manager's intent is never overridden by guessing.
- The file lives in `BepInEx/config`, not in `BepInEx/plugins`: plugin folders belong to their packages and are replaced as a whole by an update.
- The Script Extender's `SCDEModManagerLaunchId` launch variable keeps working as before: during such a launch APIShared offers and stages nothing for any package.
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

[The installation core](../src/Workshop/Install) is compiled into APIShared.dll, the independent preloader and the offline tests. [WorkshopStartup](../src/Workshop/WorkshopStartup.cs) owns the startup decision and awaits the staged request before handing off restart. [WorkshopInstallPass](../bootstrap/src/Workshop/WorkshopInstallPass.cs) holds the game lease and runs recovery and installation before the chainloader opens plugins.

State resides below `<game>/_APIShared/I` on the volume of the plugin folders. `s` holds staged containers, `q` holds confirmed group requests and `x` holds journals and the associated backups. Each request binds its members to specific plan and inventory identities. The journal identifies old and proposed plugin roots, receipts and parked player files. Publishing the active journal precedes the first live rename. Publishing the commit marker follows all renames and verification. Repeated recovery uses these identities to restore the old state or finish cleanup. Earlier single-item journals remain readable. Repair of transactions from the former external updater retains their evidence until the affected items verify.

### Linux host processes and game-exit detection

The [independent first-install patcher](#independent-linux-first-install-patcher)
uses a host helper for Extender deployment. APIShared's startup restart uses a
separate host waiter that only observes the game process and requests launch through
Steam. Its status lives below `_APIShared/I/restart`.

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
mismatch keeps the game open. APIShared submits the waiter to the systemd user
manager so Steam's reapers can exit independently before it asks Steam to start a
new instance.

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
